using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.App.Wpf.AgentTrace;

namespace AgentWorkspace.App.Wpf.Mesh;

/// <summary>
/// Adapts the visual agent-trace panel as an <see cref="IAgentSession"/> so it can serve as
/// the root node in an <see cref="AgentWorkspace.Core.Mesh.AgentMesh"/> tree.
/// <para>
/// <b>Design:</b> incoming <see cref="SendAsync"/> calls — typically merged child summaries —
/// are routed to <see cref="AgentTraceViewModel.Append"/> rather than written to a PTY stdin,
/// which would corrupt whatever the user is currently typing in the interactive shell.
/// </para>
/// <para>
/// <b>Lifecycle:</b> the mesh pump drains <see cref="Events"/> in a background task and waits
/// for an <see cref="AgentDoneEvent"/>.  Call <see cref="CancelAsync"/> (or dispose the session)
/// to push that sentinel and let the mesh clean up its pump gracefully.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class PaneAgentSession : IAgentSession
{
    private readonly AgentTraceViewModel _trace;
    private readonly Channel<AgentEvent> _channel = Channel.CreateUnbounded<AgentEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    /// <inheritdoc/>
    public AgentSessionId Id { get; } = AgentSessionId.New();

    /// <param name="trace">
    ///   The trace view-model that receives merged child summaries. Must not be null.
    /// </param>
    public PaneAgentSession(AgentTraceViewModel trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        _trace = trace;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Backed by an unbounded channel.  The channel completes (and the enumerator ends) when
    /// <see cref="CancelAsync"/> is called or the session is disposed.
    /// </remarks>
    public IAsyncEnumerable<AgentEvent> Events =>
        _channel.Reader.ReadAllAsync(CancellationToken.None);

    /// <inheritdoc/>
    /// <remarks>
    /// Intentionally a no-op for this root visual session.
    /// <para>
    /// When <see cref="AgentWorkspace.Core.Mesh.AgentMesh"/> merges a child it calls
    /// <c>parent.SendAsync</c> to inject the result into the parent's conversation context
    /// (the AI-to-AI handoff).  For a PTY-based root session there is no AI context to
    /// continue, so the injection is discarded here.  The bus subscriber in
    /// <c>MainWindow.SubscribeToMergeEvents</c> is the single source of truth for surfacing
    /// the merged summary in the agent trace panel — this keeps the UI entry-count at exactly
    /// one per merge regardless of how many children complete simultaneously.
    /// </para>
    /// </remarks>
    public ValueTask SendAsync(AgentMessage msg, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(msg);
        // Intentionally no-op: UI fanout is handled by the merged bus event.
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Writes a terminal <see cref="AgentDoneEvent"/> into the channel and completes the
    /// writer, causing any active event pump in the mesh to exit cleanly.
    /// </remarks>
    public ValueTask CancelAsync(CancellationToken cancellationToken = default)
    {
        _channel.Writer.TryWrite(new AgentDoneEvent(0, null));
        _channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
