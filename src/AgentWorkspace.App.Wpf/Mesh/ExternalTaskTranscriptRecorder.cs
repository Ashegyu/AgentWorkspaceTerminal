using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Agents.Claude;
using AgentWorkspace.Core.Transcripts;

namespace AgentWorkspace.App.Wpf.Mesh;

/// <summary>
/// Persists external Task observations to per-Task transcript JSONL files for audit
/// and recall, alongside the in-memory cards rendered by <c>MainWindow</c>.
/// <para>
/// Each <see cref="TaskInvocation"/> opens a new <see cref="TranscriptSink"/> keyed
/// by a synthetic child <see cref="AgentSessionId"/>. The sink's session_start header
/// records <c>parent_session_id</c> so consumers can trace the task back to the root
/// pane that spawned it. On <see cref="TaskResult"/> the sink writes the assistant
/// message + a done/error event and closes — a Task crash that never produces a
/// completion is reclaimed at <see cref="DisposeAsync"/>.
/// </para>
/// <para>
/// Redaction is applied by <see cref="TranscriptSink"/> internally — recorder callers
/// pass raw text and trust the sink to scrub before persisting. This matches the
/// "redact at the persistence boundary" rule from DESIGN.md §9.3.
/// </para>
/// </summary>
public sealed class ExternalTaskTranscriptRecorder : IAsyncDisposable
{
    private readonly Func<AgentSessionId, AgentSessionId?, TranscriptSink> _sinkFactory;
    /// <summary>
    /// tool_use_id → sink. Value is null between reservation and the sink-factory call;
    /// any call observing null in <see cref="OnTaskCompletedAsync"/> treats it as "start
    /// failed mid-flight" and skips. The placeholder pattern is what closes the
    /// duplicate-observation race — we MUST claim the key before opening a file at the
    /// same path, otherwise a second open fails with FileShare contention.
    /// </summary>
    private readonly ConcurrentDictionary<string, TranscriptSink?> _openSinks = new();
    private int _disposed;

    /// <param name="sinkFactory">
    ///   Builds a <see cref="TranscriptSink"/> given a child session id and an
    ///   optional parent. Default uses <see cref="TranscriptSink.Open"/> with
    ///   provider <c>"Claude (external Task)"</c>. Tests inject a factory that
    ///   writes under a temp directory.
    /// </param>
    public ExternalTaskTranscriptRecorder(
        Func<AgentSessionId, AgentSessionId?, TranscriptSink>? sinkFactory = null)
    {
        _sinkFactory = sinkFactory ?? ((id, parent) =>
            TranscriptSink.Open(
                sessionId:        id,
                provider:         "Claude (external Task)",
                parentSessionId:  parent));
    }

    /// <summary>Test-only — number of currently-open sinks.</summary>
    internal int OpenSinkCount => _openSinks.Count;

    /// <summary>
    /// Records the start of an external Task. Opens a per-Task <see cref="TranscriptSink"/>
    /// and writes the initial user message. Subsequent calls with the same
    /// <c>tool_use_id</c> are no-ops (defensive — the watcher already dedups).
    /// </summary>
    public async ValueTask OnTaskStartedAsync(
        TaskInvocation task,
        AgentSessionId childSessionId,
        AgentSessionId? rootSessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (Volatile.Read(ref _disposed) != 0) return;

        // Step 1: claim the key with a null placeholder BEFORE opening any file. If the key
        // is already taken, this is a duplicate observation — drop it without ever touching
        // the underlying file. (Opening a second TranscriptSink at the same path would fail
        // with FileShare contention because the first sink holds it Write-locked.)
        if (!_openSinks.TryAdd(task.ToolUseId, null)) return;

        // Step 2: now that we own the key, open the sink.
        TranscriptSink sink;
        try
        {
            sink = _sinkFactory(childSessionId, rootSessionId);
        }
        catch
        {
            // Sink factory threw — release the placeholder so a future legitimate retry
            // (different toolUseId or after recreation) can claim the key again.
            _openSinks.TryRemove(task.ToolUseId, out _);
            throw;
        }

        // Step 3: upgrade the placeholder to the real sink ATOMICALLY. Use TryUpdate
        // (not indexer-set) so a concurrent OnTaskCompletedAsync that just consumed our
        // null placeholder can't see the slot reappear. If the slot was already removed,
        // close the freshly-opened sink immediately to avoid a file-handle leak with no
        // matching completion to close it.
        if (!_openSinks.TryUpdate(task.ToolUseId, newValue: sink, comparisonValue: null))
        {
            await sink.DisposeAsync().ConfigureAwait(false);
            return;
        }

        // Step 4: persist the inbound prompt as a user message. TranscriptSink.AppendAsync
        // routes the text through the redaction engine before writing to disk.
        await sink.AppendAsync(
            new AgentMessageEvent(
                "user",
                $"[external-task subagent_type={task.SubAgentType}]\n{task.Prompt ?? string.Empty}"),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Records the completion of a previously-started external Task. Writes the
    /// assistant output + a done/error event and disposes the sink. If no matching
    /// start was recorded (orphan completion), the call is a no-op.
    /// </summary>
    public async ValueTask OnTaskCompletedAsync(
        TaskResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (Volatile.Read(ref _disposed) != 0) return;

        if (!_openSinks.TryRemove(result.ToolUseId, out var sink)) return;
        // Null = placeholder reserved but the sink-factory hadn't run yet (start failed
        // mid-flight). Removing the entry is enough — there's no file handle to close.
        if (sink is null) return;

        try
        {
            await sink.AppendAsync(
                new AgentMessageEvent("assistant", result.Output ?? string.Empty),
                cancellationToken).ConfigureAwait(false);

            AgentEvent terminal = result.IsError
                ? new AgentErrorEvent(result.Output ?? "external Task error (no output)")
                : new AgentDoneEvent(0, result.Output);
            await sink.AppendAsync(terminal, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await sink.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Closes any sinks left open by Tasks that never produced a completion event
    /// (Claude crash, transcript truncation, etc.). Safe to call multiple times.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        foreach (var (key, sink) in _openSinks)
        {
            _openSinks.TryRemove(key, out _);
            if (sink is null) continue; // placeholder slot — nothing to close
            try { await sink.DisposeAsync().ConfigureAwait(false); }
            catch { /* best-effort — file may already be closed */ }
        }
    }
}
