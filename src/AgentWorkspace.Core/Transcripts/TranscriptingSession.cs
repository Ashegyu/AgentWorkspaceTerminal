using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Agents;

namespace AgentWorkspace.Core.Transcripts;

/// <summary>
/// Wraps an <see cref="IAgentSession"/> so that every <see cref="AgentEvent"/> yielded
/// by the inner session's <see cref="IAgentSession.Events"/> is also appended to the
/// provided <see cref="TranscriptSink"/> before being forwarded to the caller.
/// Disposes the inner session first, then the sink.
/// </summary>
internal sealed class TranscriptingSession : IAgentSession
{
    private readonly IAgentSession _inner;
    private readonly TranscriptSink _sink;

    internal TranscriptingSession(IAgentSession inner, TranscriptSink sink)
    {
        _inner = inner;
        _sink  = sink;
    }

    public AgentSessionId Id => _inner.Id;

    public IAsyncEnumerable<AgentEvent> Events => TapAsync();

    private async IAsyncEnumerable<AgentEvent> TapAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var evt in _inner.Events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            await _sink.AppendAsync(evt, cancellationToken).ConfigureAwait(false);
            yield return evt;
        }
    }

    public ValueTask SendAsync(AgentMessage msg, CancellationToken cancellationToken = default)
        => _inner.SendAsync(msg, cancellationToken);

    public ValueTask CancelAsync(CancellationToken cancellationToken = default)
        => _inner.CancelAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await _sink.DisposeAsync().ConfigureAwait(false);
        }
    }
}
