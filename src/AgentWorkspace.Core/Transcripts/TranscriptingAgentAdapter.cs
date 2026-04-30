using System;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Agents;

namespace AgentWorkspace.Core.Transcripts;

/// <summary>
/// Decorator over <see cref="IAgentAdapter"/> that opens a <see cref="TranscriptSink"/>
/// for each session whose <see cref="AgentSessionOptions.SaveTranscript"/> is
/// <see langword="true"/> and wraps the session in a <see cref="TranscriptingSession"/>
/// so every event is persisted before being forwarded to the caller.
/// </summary>
internal sealed class TranscriptingAgentAdapter : IAgentAdapter
{
    private readonly IAgentAdapter _inner;
    private readonly Func<AgentSessionId, string?, string?, AgentSessionId?, TranscriptSink> _sinkFactory;

    /// <param name="inner">The real adapter to delegate to.</param>
    /// <param name="sinkFactory">
    ///   Called when a session is started with <c>SaveTranscript = true</c>.
    ///   Receives the new session id, the provider name, the requested model (nullable),
    ///   and the parent session id (nullable for root sessions).
    ///   Defaults to <see cref="TranscriptSink.Open"/>.
    /// </param>
    internal TranscriptingAgentAdapter(
        IAgentAdapter inner,
        Func<AgentSessionId, string?, string?, AgentSessionId?, TranscriptSink>? sinkFactory = null)
    {
        _inner       = inner;
        _sinkFactory = sinkFactory
            ?? ((id, provider, model, parentId) =>
                TranscriptSink.Open(id, provider: provider, model: model, parentSessionId: parentId));
    }

    public string Name => _inner.Name;

    public AgentCapabilities Capabilities => _inner.Capabilities;

    public async ValueTask<IAgentSession> StartSessionAsync(
        AgentSessionOptions options,
        CancellationToken cancellationToken = default)
    {
        var session = await _inner
            .StartSessionAsync(options, cancellationToken)
            .ConfigureAwait(false);

        if (!options.SaveTranscript)
            return session;

        var sink = _sinkFactory(session.Id, _inner.Name, options.Model, options.ParentSessionId);
        return new TranscriptingSession(session, sink);
    }
}
