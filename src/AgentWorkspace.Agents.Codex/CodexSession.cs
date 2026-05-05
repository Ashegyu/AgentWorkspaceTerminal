using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Agents;

namespace AgentWorkspace.Agents.Codex;

/// <summary>
/// Live session backed by a <c>codex exec</c> process. Stdout lines are emitted as
/// <see cref="AgentMessageEvent"/>; on exit a single <see cref="AgentDoneEvent"/> or
/// <see cref="AgentErrorEvent"/> is published with stderr appended for diagnostics.
/// Mirrors the lifecycle pattern in <c>ClaudeSession</c>.
/// </summary>
internal sealed class CodexSession : IAgentSession
{
    private readonly Process _process;
    private readonly Channel<AgentEvent> _channel;
    private readonly CancellationTokenSource _cts;
    private readonly Task _pump;
    private readonly Task _stderrPump;
    private readonly StringBuilder _stderr = new();

    internal CodexSession(Process process)
    {
        _process = process;
        _channel = Channel.CreateUnbounded<AgentEvent>(
            new UnboundedChannelOptions { SingleReader = true });
        _cts = new CancellationTokenSource();
        Id = AgentSessionId.New();
        // Stderr drain is mandatory — otherwise the OS pipe buffer fills and the
        // child blocks on its next stderr write, deadlocking the whole session.
        _stderrPump = Task.Run(() => PumpStderrAsync(_cts.Token));
        _pump       = PumpAsync(_cts.Token);
    }

    public AgentSessionId Id { get; }

    public IAsyncEnumerable<AgentEvent> Events =>
        _channel.Reader.ReadAllAsync(CancellationToken.None);

    public async ValueTask SendAsync(AgentMessage msg, CancellationToken cancellationToken = default)
    {
        // Codex `exec` mode is single-shot — multi-turn input is not currently supported.
        // The contract still requires a working SendAsync so the mesh's auto-merge
        // pathway doesn't throw; we no-op (best-effort write) rather than crash.
        try
        {
            await _process.StandardInput.WriteLineAsync(msg.Text.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // stdin is closed in single-shot mode — silently ignore.
        }
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
        catch { /* stderr is best-effort diagnostic only */ }
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

                // Plain-text path: each non-empty line becomes one assistant message event.
                // Future: replace this block with JSON-line parsing once Codex stabilises a
                // stream-json format (mirror Claude.Wire.StreamJsonParser pattern).
                await _channel.Writer.WriteAsync(
                    new AgentMessageEvent("assistant", line), ct).ConfigureAwait(false);
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

                    // Drain stderr fully before reading so the diagnostic message is complete.
                    try { await _stderrPump.ConfigureAwait(false); } catch { }

                    string stderrText;
                    lock (_stderr) stderrText = _stderr.ToString().Trim();

                    AgentEvent finalEvt = exit != 0
                        ? new AgentErrorEvent(stderrText.Length > 0
                            ? $"codex exited with code {exit}: {stderrText}"
                            : $"codex exited with code {exit} (no stderr — check 'codex' is installed and authenticated)")
                        : new AgentDoneEvent(exit, stderrText.Length > 0 ? stderrText : null);
                    await _channel.Writer.WriteAsync(finalEvt).ConfigureAwait(false);
                }
                catch { }
            }
            _channel.Writer.TryComplete();
        }
    }
}
