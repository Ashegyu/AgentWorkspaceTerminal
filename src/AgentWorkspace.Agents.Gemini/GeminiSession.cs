using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Agents;

namespace AgentWorkspace.Agents.Gemini;

/// <summary>
/// Live session backed by a <c>gemini -p</c> process. Stdout lines are emitted as
/// <see cref="AgentMessageEvent"/>; on exit a single <see cref="AgentDoneEvent"/> or
/// <see cref="AgentErrorEvent"/> is published with stderr appended.
/// Mirrors the lifecycle pattern in <c>ClaudeSession</c> and <c>CodexSession</c>.
/// </summary>
internal sealed class GeminiSession : IAgentSession
{
    private readonly Process _process;
    private readonly Channel<AgentEvent> _channel;
    private readonly CancellationTokenSource _cts;
    private readonly Task _pump;
    private readonly Task _stderrPump;
    private readonly StringBuilder _stderr = new();

    internal GeminiSession(Process process)
    {
        _process = process;
        _channel = Channel.CreateUnbounded<AgentEvent>(
            new UnboundedChannelOptions { SingleReader = true });
        _cts = new CancellationTokenSource();
        Id = AgentSessionId.New();
        _stderrPump = Task.Run(() => PumpStderrAsync(_cts.Token));
        _pump       = PumpAsync(_cts.Token);
    }

    public AgentSessionId Id { get; }

    public IAsyncEnumerable<AgentEvent> Events =>
        _channel.Reader.ReadAllAsync(CancellationToken.None);

    public async ValueTask SendAsync(AgentMessage msg, CancellationToken cancellationToken = default)
    {
        // Single-shot mode — best-effort stdin write, no-op if pipe is already closed.
        try
        {
            await _process.StandardInput.WriteLineAsync(msg.Text.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { /* stdin closed in single-shot mode */ }
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
        catch { /* stderr is best-effort */ }
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

                // Plain-text path — each non-empty line surfaces as one assistant message.
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

                    try { await _stderrPump.ConfigureAwait(false); } catch { }

                    string stderrText;
                    lock (_stderr) stderrText = _stderr.ToString().Trim();

                    AgentEvent finalEvt = exit != 0
                        ? new AgentErrorEvent(stderrText.Length > 0
                            ? $"gemini exited with code {exit}: {stderrText}"
                            : $"gemini exited with code {exit} (no stderr — check 'gemini' is installed and GEMINI_API_KEY is set)")
                        : new AgentDoneEvent(exit, stderrText.Length > 0 ? stderrText : null);
                    await _channel.Writer.WriteAsync(finalEvt).ConfigureAwait(false);
                }
                catch { }
            }
            _channel.Writer.TryComplete();
        }
    }
}
