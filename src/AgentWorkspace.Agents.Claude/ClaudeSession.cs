using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Agents.Claude.Wire;

namespace AgentWorkspace.Agents.Claude;

/// <summary>
/// Live session backed by a <c>claude --print --output-format stream-json</c> process.
/// Stdout is pumped into an unbounded channel; callers consume via <see cref="Events"/>.
/// </summary>
internal sealed class ClaudeSession : IAgentSession
{
    private readonly Process _process;
    private readonly Channel<AgentEvent> _channel;
    private readonly CancellationTokenSource _cts;
    private readonly Task _pump;
    private readonly Task _stderrPump;
    private readonly StringBuilder _stderr = new();

    internal ClaudeSession(Process process)
    {
        _process = process;
        _channel = Channel.CreateUnbounded<AgentEvent>(
            new UnboundedChannelOptions { SingleReader = true });
        _cts = new CancellationTokenSource();
        Id = AgentSessionId.New();
        // Stderr must be drained continuously — otherwise the OS pipe buffer fills,
        // claude blocks on its next stderr write, and the whole session deadlocks.
        // Capturing the text also lets us surface the real failure reason (auth error,
        // bad arguments, missing config) when claude exits non-zero without emitting
        // any stream-json events on stdout.
        _stderrPump = Task.Run(() => PumpStderrAsync(_cts.Token));
        _pump = PumpAsync(_cts.Token);
    }

    private async Task PumpStderrAsync(CancellationToken ct)
    {
        try
        {
            var reader = _process.StandardError;
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                lock (_stderr) _stderr.AppendLine(line);
            }
        }
        catch (OperationCanceledException) { }
        catch { /* swallow — stderr is best-effort diagnostic only */ }
    }

    public AgentSessionId Id { get; }

    public IAsyncEnumerable<AgentEvent> Events =>
        _channel.Reader.ReadAllAsync(CancellationToken.None);

    public async ValueTask SendAsync(AgentMessage msg, CancellationToken cancellationToken = default)
    {
        await _process.StandardInput.WriteLineAsync(msg.Text.AsMemory(), cancellationToken).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask CancelAsync(CancellationToken cancellationToken = default)
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try { _process.Kill(entireProcessTree: false); }
        catch (InvalidOperationException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try { _process.Kill(entireProcessTree: false); }
        catch (InvalidOperationException) { }
        await _pump.ConfigureAwait(false);
        try { await _stderrPump.ConfigureAwait(false); } catch { }
        _process.Dispose();
        _cts.Dispose();
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        bool resultEmitted = false;
        try
        {
            var reader = _process.StandardOutput;
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                var evt = StreamJsonParser.Parse(line);
                if (evt is null) continue;

                await _channel.Writer.WriteAsync(evt, ct).ConfigureAwait(false);

                if (evt is AgentDoneEvent or AgentErrorEvent)
                {
                    resultEmitted = true;
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            try
            {
                await _channel.Writer.WriteAsync(new AgentErrorEvent(ex.Message)).ConfigureAwait(false);
                resultEmitted = true;
            }
            catch { }
        }
        finally
        {
            if (!ct.IsCancellationRequested && !resultEmitted)
            {
                try
                {
                    await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    int exit = _process.ExitCode;

                    // Drain any buffered stderr so the diagnostic message is complete
                    // before we read it. The reader exits naturally on EOF after the
                    // process closes its stderr pipe.
                    try { await _stderrPump.ConfigureAwait(false); } catch { }

                    string stderrText;
                    lock (_stderr) stderrText = _stderr.ToString().Trim();

                    AgentEvent finalEvt = exit != 0
                        ? new AgentErrorEvent(stderrText.Length > 0
                            ? $"claude exited with code {exit}: {stderrText}"
                            : $"claude exited with code {exit} (no stderr output — check that 'claude' is authenticated; run 'claude login')")
                        : new AgentDoneEvent(exit, stderrText.Length > 0 ? stderrText : null);
                    await _channel.Writer.WriteAsync(finalEvt).ConfigureAwait(false);
                }
                catch { }
            }
            _channel.Writer.TryComplete();
        }
    }
}
