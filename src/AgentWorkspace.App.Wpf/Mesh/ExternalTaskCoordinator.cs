using System;
using System.Collections.Concurrent;
using System.Threading;

namespace AgentWorkspace.App.Wpf.Mesh;

/// <summary>
/// Pure-logic bookkeeper for external Task tracking + auto-pane budget.
/// <para>
/// Holds the dictionaries and counters that <c>MainWindow</c> previously owned inline:
///   - <c>tool_use_id → SubAgentSessionViewModel</c> map (for completion correlation)
///   - <c>tool_use_id → byte</c> tag set (for "we auto-paned this task")
///   - in-flight auto-pane counter
///   - the on/off toggle
/// </para>
/// <para>
/// Has no dependency on <c>Workspace</c>, <c>Dispatcher</c>, or any WPF type — the
/// coordinator's job is to make consistent state-machine decisions; the caller does the
/// actual UI work (VM creation, pane opening, clipboard) based on those decisions.
/// </para>
/// <para>
/// Thread-safety: all public methods are safe to call from any thread. The internal
/// dictionaries are <see cref="ConcurrentDictionary{TKey,TValue}"/>; the counter uses
/// <see cref="Interlocked"/>.
/// </para>
/// </summary>
public sealed class ExternalTaskCoordinator
{
    /// <summary>Hard cap on simultaneous auto-spawned panes to prevent layout explosion.</summary>
    public const int MaxAutoPanesInFlight = 3;

    /// <summary>tool_use_id → VM. Value is null while the slot is reserved but the VM hasn't been created yet.</summary>
    private readonly ConcurrentDictionary<string, SubAgentSessionViewModel?> _externalTaskMap = new();
    /// <summary>
    /// tool_use_ids that we auto-paned, with the UTC timestamp the tag was added.
    /// Decrement is gated on membership here, not toggle state. Timestamp lets
    /// <see cref="PruneStaleAutoPaneTags"/> reclaim slots when Claude crashes mid-Task.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _autoPanedTaskIds = new();
    private int _autoPanesInFlight;
    /// <summary>
    /// volatile keyword makes reads/writes on this flag visible across threads without
    /// requiring callers to take a lock. ToggleAutoPane writes from the UI thread; the
    /// fast-path readers in <see cref="TryClaimAutoPaneSlot"/> may run on the watcher thread.
    /// </summary>
    private volatile bool _autoPaneOnExternalTask;

    // ── toggle ───────────────────────────────────────────────────────────────────

    /// <summary>Whether auto-pane creation is currently enabled.</summary>
    public bool IsAutoPaneEnabled => _autoPaneOnExternalTask;

    /// <summary>Toggles auto-pane creation. Returns the new state.</summary>
    public bool ToggleAutoPane()
    {
        _autoPaneOnExternalTask = !_autoPaneOnExternalTask;
        return _autoPaneOnExternalTask;
    }

    // ── start lifecycle ──────────────────────────────────────────────────────────

    /// <summary>
    /// Reserves a map slot for the given <paramref name="toolUseId"/>. Returns false if the
    /// id is already known (dedup) — caller should drop the duplicate observation.
    /// <para>
    /// MUST be called BEFORE any UI dispatch so that a fast-arriving completion can find
    /// the slot reserved (even if the VM isn't created yet) and use the placeholder retry.
    /// </para>
    /// </summary>
    public bool TryReserveStartSlot(string toolUseId)
    {
        return _externalTaskMap.TryAdd(toolUseId, null);
    }

    /// <summary>Replaces the placeholder reserved by <see cref="TryReserveStartSlot"/> with the real VM.</summary>
    public void RegisterStartedVm(string toolUseId, SubAgentSessionViewModel vm)
    {
        _externalTaskMap[toolUseId] = vm ?? throw new ArgumentNullException(nameof(vm));
    }

    /// <summary>Rolls back a reservation when VM creation fails downstream.</summary>
    public void RollbackReservation(string toolUseId)
    {
        _externalTaskMap.TryRemove(toolUseId, out _);
    }

    // ── auto-pane budget ─────────────────────────────────────────────────────────

    /// <summary>
    /// Claims an auto-pane budget slot if (a) toggle is on AND (b) under the cap AND (c)
    /// not already claimed for this id. Returns true if the caller may proceed to open
    /// an auto-pane. Tags the id so the corresponding completion releases the slot
    /// regardless of toggle state at completion time.
    /// <para>
    /// The check-then-increment is implemented as a CAS loop so concurrent claims from
    /// multiple threads cannot collectively bust <see cref="MaxAutoPanesInFlight"/>.
    /// A naive read+increment under contention can take the counter past the cap because
    /// each thread reads a stale value before any of them increments.
    /// </para>
    /// </summary>
    public bool TryClaimAutoPaneSlot(string toolUseId)
    {
        if (!_autoPaneOnExternalTask) return false;

        // CAS loop: increment counter only if it's still below the cap when we commit.
        // Doing this BEFORE TryAdd ensures we never bust the cap; if TryAdd then fails
        // (duplicate id) we roll back the increment.
        int current;
        do
        {
            current = Volatile.Read(ref _autoPanesInFlight);
            if (current >= MaxAutoPanesInFlight) return false;
        } while (Interlocked.CompareExchange(ref _autoPanesInFlight, current + 1, current) != current);

        if (!_autoPanedTaskIds.TryAdd(toolUseId, DateTimeOffset.UtcNow))
        {
            // Duplicate tag — undo the increment.
            Interlocked.Decrement(ref _autoPanesInFlight);
            return false;
        }
        return true;
    }

    /// <summary>Currently-in-flight auto-pane count (test/diagnostic accessor).</summary>
    public int AutoPanesInFlight => Volatile.Read(ref _autoPanesInFlight);

    // ── completion lifecycle ─────────────────────────────────────────────────────

    /// <summary>
    /// Looks up the VM associated with a previously-started Task, or null if there's no
    /// registration. The VM may also be null if the placeholder hasn't been replaced yet —
    /// callers should retry on null with a short delay.
    /// </summary>
    /// <returns>
    /// <c>(found: true, vm: ...)</c> — there's an entry; vm may still be null (placeholder).<br/>
    /// <c>(found: false, vm: null)</c> — no entry exists; the start was never observed.
    /// </returns>
    public (bool found, SubAgentSessionViewModel? vm) TryFindForCompletion(string toolUseId)
    {
        if (_externalTaskMap.TryGetValue(toolUseId, out var vm))
            return (true, vm);
        return (false, null);
    }

    /// <summary>
    /// Releases the start-slot map entry and the auto-pane budget slot for the given id.
    /// Idempotent — calling twice does no harm. The auto-pane counter clamps at zero so
    /// drift can't take it negative.
    /// </summary>
    public void ReleaseCompletion(string toolUseId)
    {
        _externalTaskMap.TryRemove(toolUseId, out _);

        if (_autoPanedTaskIds.TryRemove(toolUseId, out _))
        {
            int current = Interlocked.Decrement(ref _autoPanesInFlight);
            if (current < 0) Interlocked.Exchange(ref _autoPanesInFlight, 0);
        }
    }

    /// <summary>
    /// Reclaims auto-pane budget slots for tasks whose tags are older than
    /// <paramref name="maxAge"/>. Covers the case where Claude crashes or disconnects
    /// mid-Task and the corresponding completion never fires — without this sweep, the
    /// counter would drift toward the cap permanently. Returns the number of stale tags
    /// that were reclaimed (zero if none expired).
    /// <para>
    /// Caller is responsible for invoking this on a periodic cadence (e.g. once a minute).
    /// The coordinator does not own a timer to keep itself dependency-free for testing.
    /// </para>
    /// </summary>
    public int PruneStaleAutoPaneTags(TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        int reclaimed = 0;
        foreach (var (key, ts) in _autoPanedTaskIds)
        {
            if (ts >= cutoff) continue;
            if (!_autoPanedTaskIds.TryRemove(key, out _)) continue;
            int current = Interlocked.Decrement(ref _autoPanesInFlight);
            if (current < 0) Interlocked.Exchange(ref _autoPanesInFlight, 0);
            reclaimed++;
        }
        return reclaimed;
    }
}
