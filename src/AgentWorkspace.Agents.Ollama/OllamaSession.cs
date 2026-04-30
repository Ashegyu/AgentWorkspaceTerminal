using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Agents;

namespace AgentWorkspace.Agents.Ollama;

/// <summary>
/// A single-shot Ollama chat session backed by a streaming HTTP POST to
/// <c>/api/chat</c>.  Each line in the NDJSON response is decoded and forwarded
/// as an <see cref="AgentEvent"/> via an unbounded channel.
/// </summary>
internal sealed class OllamaSession : IAgentSession
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly string _initialPrompt;
    private readonly Channel<AgentEvent> _channel;
    private readonly CancellationTokenSource _cts;
    private readonly Task _pump;

    internal OllamaSession(HttpClient http, string baseUrl, string model, string initialPrompt)
    {
        _http          = http;
        _baseUrl       = baseUrl;
        _model         = model;
        _initialPrompt = initialPrompt;
        _channel       = Channel.CreateUnbounded<AgentEvent>(
            new UnboundedChannelOptions { SingleReader = true });
        _cts  = new CancellationTokenSource();
        Id    = AgentSessionId.New();
        _pump = Task.Run(() => PumpAsync(_cts.Token));
    }

    public AgentSessionId Id { get; }

    public IAsyncEnumerable<AgentEvent> Events =>
        _channel.Reader.ReadAllAsync(CancellationToken.None);

    /// <summary>
    /// Ollama HTTP sessions are single-shot in this adapter; multi-turn is not yet implemented.
    /// </summary>
    public ValueTask SendAsync(AgentMessage msg, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public async ValueTask CancelAsync(CancellationToken cancellationToken = default) =>
        await _cts.CancelAsync().ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try { await _pump.ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        bool resultEmitted = false;
        try
        {
            var requestBody = new
            {
                model    = _model,
                messages = new[]
                {
                    new { role = "user", content = _initialPrompt },
                },
                stream = true,
            };

            var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/chat")
            {
                Content = JsonContent.Create(requestBody),
            };

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new System.IO.StreamReader(stream);

            var assistantBuffer = new System.Text.StringBuilder();

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                // Surface content chunks as streaming message events.
                if (root.TryGetProperty("message", out var msgEl) &&
                    msgEl.TryGetProperty("content", out var contentEl))
                {
                    var chunk = contentEl.GetString() ?? string.Empty;
                    if (chunk.Length > 0)
                    {
                        assistantBuffer.Append(chunk);
                        await _channel.Writer
                            .WriteAsync(new AgentMessageEvent("assistant", chunk), ct)
                            .ConfigureAwait(false);
                    }
                }

                // When Ollama sets done=true the stream is finished.
                if (root.TryGetProperty("done", out var doneEl) && doneEl.GetBoolean())
                {
                    await _channel.Writer
                        .WriteAsync(new AgentDoneEvent(0, null), ct)
                        .ConfigureAwait(false);
                    resultEmitted = true;
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (HttpRequestException ex)
        {
            try
            {
                await _channel.Writer
                    .WriteAsync(new AgentErrorEvent(
                        $"Ollama HTTP error: {ex.Message} — is Ollama running at {_baseUrl}?"))
                    .ConfigureAwait(false);
                resultEmitted = true;
            }
            catch { }
        }
        catch (Exception ex)
        {
            try
            {
                await _channel.Writer
                    .WriteAsync(new AgentErrorEvent(ex.Message))
                    .ConfigureAwait(false);
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
                    await _channel.Writer
                        .WriteAsync(new AgentDoneEvent(0, null))
                        .ConfigureAwait(false);
                }
                catch { }
            }
            _channel.Writer.TryComplete();
        }
    }
}
