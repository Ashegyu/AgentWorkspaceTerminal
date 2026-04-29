using System;
using System.Buffers;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Pty;
using AgentWorkspace.ConPTY.Native;
using Microsoft.Win32.SafeHandles;

namespace AgentWorkspace.ConPTY;

/// <summary>
/// A running pseudo-console pane: ConPTY + child process + Job Object.
/// </summary>
/// <remarks>
/// <para>
/// The Win32 ConPTY surface is not thread-safe for the same handle. We funnel
/// <see cref="WriteAsync"/>, <see cref="ResizeAsync"/>, <see cref="SignalAsync"/> and
/// <see cref="KillAsync"/> onto a single actor channel drained by a background task; reads run on
/// a separate cooperatively-cancelled loop driven by direct stream I/O against the ConPTY output
/// pipe.
/// </para>
/// <para>
/// Disposal performs deterministic teardown: signal the actor channel, close ConPTY (the child
/// observes EOF on stdin), close the Job Object (descendants are terminated by
/// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>), then release pipes and process handles.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PseudoConsoleProcess : IPseudoTerminal
{
    private enum CommandKind { Write, Resize, Signal, Kill }

    private readonly record struct ActorCommand(
        CommandKind Kind,
        ReadOnlyMemory<byte> Payload,
        short Cols,
        short Rows,
        PtySignal Signal,
        KillMode Kill,
        TaskCompletionSource Completion);

    private readonly Channel<ActorCommand> _actor =
        Channel.CreateUnbounded<ActorCommand>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _exitedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Persistent streams; each owns the underlying SafeFileHandle for the lifetime of the pane.
    private FileStream? _inputStream;
    private FileStream? _outputStream;

    private PseudoConsoleHandle? _hpcon;
    private JobObject? _job;
    private SafeProcessHandle? _process;
    private SafeFileHandle? _mainThread;
    private int _processId;

    private Task? _actorTask;
    private Task? _waitForExitTask;
    private long _sequence;
    private int _state = (int)PaneState.Created;

    public PseudoConsoleProcess(PaneId id) => Id = id;

    public PaneId Id { get; }

    public PaneState State => (PaneState)Volatile.Read(ref _state);

    public int ProcessId => _processId;

    public Task Exit => _exitedTcs.Task;

    public event EventHandler<int>? Exited;

    public ValueTask StartAsync(PaneStartOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.InitialColumns, (short)1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.InitialRows, (short)1);

        if (Interlocked.CompareExchange(ref _state, (int)PaneState.Starting, (int)PaneState.Created) != (int)PaneState.Created)
        {
            throw new InvalidOperationException("StartAsync may only be invoked once on a fresh pane.");
        }

        try
        {
            // 1. Two anonymous pipes. ConPTY reads input from inputReadSide and writes child output
            //    to outputWriteSide. We hold the opposite ends.
            if (!NativeMethods.CreatePipe(out var inputReadSide, out var inputWriter, 0, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (input) failed.");
            }
            if (!NativeMethods.CreatePipe(out var outputReader, out var outputWriteSide, 0, 0))
            {
                inputReadSide.Dispose();
                inputWriter.Dispose();
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (output) failed.");
            }

            // 2. HPCON. The function duplicates the read+write handles internally; we may close
            //    our copies of the sides ConPTY now owns once it succeeds.
            int hr = NativeMethods.CreatePseudoConsole(
                new Coord(options.InitialColumns, options.InitialRows),
                inputReadSide,
                outputWriteSide,
                dwFlags: 0,
                out var hpconRaw);
            if (hr != 0)
            {
                inputReadSide.Dispose();
                inputWriter.Dispose();
                outputReader.Dispose();
                outputWriteSide.Dispose();
                throw new Win32Exception(hr, $"CreatePseudoConsole failed (HRESULT 0x{hr:X8}).");
            }

            _hpcon = new PseudoConsoleHandle(hpconRaw);
            inputReadSide.Dispose();
            outputWriteSide.Dispose();

            // Wrap the surviving handles in FileStreams that own them. Anonymous pipes returned by
            // CreatePipe are *synchronous* handles — they do not support OVERLAPPED I/O — so the
            // streams are constructed with isAsync:false. PipeReader and our actor still expose
            // async surfaces; .NET's FileStream falls back to thread-pool I/O for sync handles.
            _inputStream = new FileStream(inputWriter, FileAccess.Write, bufferSize: 4096, isAsync: false);
            _outputStream = new FileStream(outputReader, FileAccess.Read, bufferSize: 4096, isAsync: false);

            // 3. Job Object — kill the descendant tree when the handle closes.
            _job = new JobObject();

            // 4. Proc-thread attribute list.
            using var attrList = ProcThreadAttributeList.Create(
                _hpcon.DangerousGetHandle(),
                jobHandles: new[] { _job.Handle.DangerousGetHandle() });

            // 5. Spawn the child.
            var startupInfo = default(StartupInfoEx);
            startupInfo.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
            startupInfo.lpAttributeList = attrList.Pointer;

            string commandLine = CommandLine.Build(options.Command, options.Arguments);
            uint flags = NativeMethods.EXTENDED_STARTUPINFO_PRESENT | NativeMethods.CREATE_UNICODE_ENVIRONMENT;

            nint envBlock = 0;
            try
            {
                if (options.Environment is { Count: > 0 } env)
                {
                    string block = CommandLine.BuildEnvironmentBlock(env);
                    envBlock = Marshal.StringToHGlobalUni(block);
                }

                if (!NativeMethods.CreateProcessW(
                        lpApplicationName: null,
                        lpCommandLine: commandLine,
                        lpProcessAttributes: 0,
                        lpThreadAttributes: 0,
                        bInheritHandles: false,
                        dwCreationFlags: flags,
                        lpEnvironment: envBlock,
                        lpCurrentDirectory: options.WorkingDirectory,
                        lpStartupInfo: ref startupInfo,
                        lpProcessInformation: out var pi))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        $"CreateProcessW failed for command line: {commandLine}");
                }

                _process = new SafeProcessHandle(pi.hProcess, ownsHandle: true);
                _mainThread = new SafeFileHandle(pi.hThread, ownsHandle: true);
                _processId = pi.dwProcessId;
            }
            finally
            {
                if (envBlock != 0)
                {
                    Marshal.FreeHGlobal(envBlock);
                }
            }

            // 6. Start background tasks. Output reading is implemented as a direct stream loop in
            //    ReadAsync rather than a PipeReader; PipeReader will be reintroduced once we wire
            //    the bounded-channel + frame-coalescer described in DESIGN §4.
            _actorTask = Task.Run(() => RunActorAsync(_cts.Token));
            _waitForExitTask = Task.Run(WaitForExitAsync);

            Volatile.Write(ref _state, (int)PaneState.Running);
        }
        catch
        {
            Volatile.Write(ref _state, (int)PaneState.Faulted);
            // Best-effort teardown; DisposeAsync handles the rest from a non-async context.
            _ = DisposeAsync().AsTask();
            throw;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return EnqueueAsync(new ActorCommand(CommandKind.Write, input, 0, 0, default, default, tcs), cancellationToken);
    }

    public ValueTask ResizeAsync(short columns, short rows, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, (short)1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, (short)1);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return EnqueueAsync(new ActorCommand(CommandKind.Resize, default, columns, rows, default, default, tcs), cancellationToken);
    }

    public ValueTask SignalAsync(PtySignal signal, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return EnqueueAsync(new ActorCommand(CommandKind.Signal, default, 0, 0, signal, default, tcs), cancellationToken);
    }

    public ValueTask KillAsync(KillMode mode, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return EnqueueAsync(new ActorCommand(CommandKind.Kill, default, 0, 0, default, mode, tcs), cancellationToken);
    }

    public async IAsyncEnumerable<PtyChunk> ReadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_outputStream is null)
        {
            throw new InvalidOperationException("Pane has not started.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        var token = linked.Token;

        // Each chunk is rented from the shared ArrayPool so the consumer can outlive the read loop.
        // The buffer size matches the FileStream's internal buffer; the OS may return shorter reads.
        const int RentSize = 8192;

        while (!token.IsCancellationRequested)
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(RentSize);
            int read;
            try
            {
                read = await _outputStream.ReadAsync(rented.AsMemory(0, RentSize), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ArrayPool<byte>.Shared.Return(rented);
                yield break;
            }
            catch (IOException)
            {
                ArrayPool<byte>.Shared.Return(rented);
                yield break;
            }
            catch (ObjectDisposedException)
            {
                ArrayPool<byte>.Shared.Return(rented);
                yield break;
            }

            if (read == 0)
            {
                ArrayPool<byte>.Shared.Return(rented);
                yield break;
            }

            long seq = Interlocked.Increment(ref _sequence);
            yield return new PtyChunk(
                new ReadOnlyMemory<byte>(rented, 0, read),
                seq,
                DateTimeOffset.UtcNow);
        }
    }

    private async ValueTask EnqueueAsync(ActorCommand cmd, CancellationToken cancellationToken)
    {
        var s = State;
        if (s is PaneState.Created or PaneState.Starting)
        {
            throw new InvalidOperationException("Pane has not finished starting.");
        }
        if (s is PaneState.Exited or PaneState.Faulted)
        {
            throw new InvalidOperationException("Pane is no longer running.");
        }

        await _actor.Writer.WriteAsync(cmd, cancellationToken).ConfigureAwait(false);
        await using var reg = cancellationToken.Register(static state =>
        {
            ((TaskCompletionSource)state!).TrySetCanceled();
        }, cmd.Completion).ConfigureAwait(false);
        await cmd.Completion.Task.ConfigureAwait(false);
    }

    private async Task RunActorAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var cmd in _actor.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    switch (cmd.Kind)
                    {
                        case CommandKind.Write:
                            await DoWriteAsync(cmd.Payload, cancellationToken).ConfigureAwait(false);
                            break;
                        case CommandKind.Resize:
                            DoResize(cmd.Cols, cmd.Rows);
                            break;
                        case CommandKind.Signal:
                            await DoSignalAsync(cmd.Signal, cancellationToken).ConfigureAwait(false);
                            break;
                        case CommandKind.Kill:
                            await DoKillAsync(cmd.Kill, cancellationToken).ConfigureAwait(false);
                            break;
                    }
                    cmd.Completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    cmd.Completion.TrySetException(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Drain pending commands as cancelled.
        }

        while (_actor.Reader.TryRead(out var pending))
        {
            pending.Completion.TrySetCanceled();
        }
    }

    private async Task DoWriteAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (_inputStream is null)
        {
            throw new InvalidOperationException("Input stream is not initialised.");
        }
        await _inputStream.WriteAsync(payload, ct).ConfigureAwait(false);
        await _inputStream.FlushAsync(ct).ConfigureAwait(false);
    }

    private void DoResize(short cols, short rows)
    {
        if (_hpcon is null || _hpcon.IsInvalid)
        {
            return;
        }
        int hr = NativeMethods.ResizePseudoConsole(_hpcon.DangerousGetHandle(), new Coord(cols, rows));
        if (hr != 0)
        {
            throw new Win32Exception(hr, $"ResizePseudoConsole failed (HRESULT 0x{hr:X8}).");
        }
    }

    private async Task DoSignalAsync(PtySignal signal, CancellationToken ct)
    {
        // ConPTY children share a private console; GenerateConsoleCtrlEvent only works for the
        // current console. Writing the canonical control byte (Ctrl+C = 0x03, Ctrl+\ = 0x1C) on
        // stdin is interpreted by the line discipline of every shell we care about.
        byte b = signal switch
        {
            PtySignal.Interrupt => 0x03,
            PtySignal.Break => 0x1C,
            _ => 0x03,
        };
        var buf = new byte[] { b };
        if (_inputStream is null)
        {
            return;
        }
        await _inputStream.WriteAsync(buf, ct).ConfigureAwait(false);
        await _inputStream.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task DoKillAsync(KillMode mode, CancellationToken ct)
    {
        if (mode == KillMode.Graceful)
        {
            try { await DoSignalAsync(PtySignal.Interrupt, ct).ConfigureAwait(false); } catch { /* best effort */ }
            if (_process is not null)
            {
                uint waited = await Task.Run(() => NativeMethods.WaitForSingleObject(_process, 1500), ct).ConfigureAwait(false);
                if (waited == NativeMethods.WAIT_OBJECT_0)
                {
                    return;
                }
            }
        }
        _job?.Terminate();
    }

    private async Task WaitForExitAsync()
    {
        if (_process is null)
        {
            return;
        }
        try
        {
            await Task.Run(() => NativeMethods.WaitForSingleObject(_process, NativeMethods.INFINITE)).ConfigureAwait(false);
        }
        catch
        {
            // process already disposed
        }

        int exitCode = -1;
        if (_process is not null && !_process.IsClosed && NativeMethods.GetExitCodeProcess(_process, out uint code))
        {
            exitCode = unchecked((int)code);
        }

        Volatile.Write(ref _state, (int)PaneState.Exited);
        _exitedTcs.TrySetResult();
        Exited?.Invoke(this, exitCode);

        // Closing input drives ConPTY to shut down. Give it a brief window to emit any final
        // cell-grid diff for the just-exited child before we tear down the output side, then
        // close output to deliver EOF to ReadAsync. The 250ms grace is empirical: shorter values
        // race the final flush on fast machines; longer ones just delay teardown.
        try { _inputStream?.Dispose(); } catch { /* best effort */ }
        try { await Task.Delay(250).ConfigureAwait(false); } catch { /* best effort */ }
        try { _outputStream?.Dispose(); } catch { /* best effort */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }
        _actor.Writer.TryComplete();

        if (_actorTask is not null)
        {
            try { await _actorTask.ConfigureAwait(false); } catch { /* swallow */ }
        }

        try { _hpcon?.Dispose(); } catch { /* swallow */ }
        try { _job?.Dispose(); } catch { /* swallow */ }
        try { _inputStream?.Dispose(); } catch { /* swallow */ }
        try { _outputStream?.Dispose(); } catch { /* swallow */ }
        try { _process?.Dispose(); } catch { /* swallow */ }
        try { _mainThread?.Dispose(); } catch { /* swallow */ }

        if (_waitForExitTask is not null)
        {
            try { await _waitForExitTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { /* swallow */ }
        }

        _cts.Dispose();

        if (State == PaneState.Running)
        {
            Volatile.Write(ref _state, (int)PaneState.Exited);
        }
    }
}
