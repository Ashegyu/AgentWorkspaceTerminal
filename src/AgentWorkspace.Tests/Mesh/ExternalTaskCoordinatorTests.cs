using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Agents.Claude;
using AgentWorkspace.App.Wpf.Mesh;

namespace AgentWorkspace.Tests.Mesh;

/// <summary>
/// Unit tests for the pure-logic <see cref="ExternalTaskCoordinator"/> extracted from
/// <c>MainWindow.xaml.cs</c>. Covers reservation/registration race protection, dedup,
/// auto-pane budget gating, toggle-decoupled completion accounting, and idempotency.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ExternalTaskCoordinatorTests
{
    /// <summary>Fresh VM helper — coordinator only stores references; identity is what matters.</summary>
    private static SubAgentSessionViewModel NewVm() => new(
        childId:        AgentSessionId.New(),
        originalPrompt: "test prompt",
        adapter:        new ClaudeAdapter(),
        isExternal:     true,
        externalSubAgentType: "general-purpose");

    // ── slot reservation ─────────────────────────────────────────────────────────

    [Fact]
    public void TryReserveStartSlot_FirstCall_ReturnsTrue()
    {
        var coord = new ExternalTaskCoordinator();
        Assert.True(coord.TryReserveStartSlot("toolu_a"));
    }

    [Fact]
    public void TryReserveStartSlot_DuplicateId_ReturnsFalse()
    {
        var coord = new ExternalTaskCoordinator();
        Assert.True(coord.TryReserveStartSlot("toolu_a"));
        Assert.False(coord.TryReserveStartSlot("toolu_a"));
    }

    [Fact]
    public void TryReserveStartSlot_DifferentIds_BothSucceed()
    {
        var coord = new ExternalTaskCoordinator();
        Assert.True(coord.TryReserveStartSlot("toolu_a"));
        Assert.True(coord.TryReserveStartSlot("toolu_b"));
    }

    // ── start lifecycle ──────────────────────────────────────────────────────────

    [Fact]
    public void TryFindForCompletion_AfterReservationOnly_ReturnsFoundButNullVm()
    {
        var coord = new ExternalTaskCoordinator();
        coord.TryReserveStartSlot("toolu_a");

        var (found, vm) = coord.TryFindForCompletion("toolu_a");
        Assert.True(found);
        Assert.Null(vm);
    }

    [Fact]
    public void TryFindForCompletion_AfterRegistration_ReturnsFoundAndVm()
    {
        var coord = new ExternalTaskCoordinator();
        var vm = NewVm();
        coord.TryReserveStartSlot("toolu_a");
        coord.RegisterStartedVm("toolu_a", vm);

        var (found, found_vm) = coord.TryFindForCompletion("toolu_a");
        Assert.True(found);
        Assert.Same(vm, found_vm);
    }

    [Fact]
    public void TryFindForCompletion_UnknownId_ReturnsNotFound()
    {
        var coord = new ExternalTaskCoordinator();
        var (found, vm) = coord.TryFindForCompletion("never_seen");
        Assert.False(found);
        Assert.Null(vm);
    }

    [Fact]
    public void RollbackReservation_AllowsRetry()
    {
        var coord = new ExternalTaskCoordinator();
        Assert.True(coord.TryReserveStartSlot("toolu_a"));
        coord.RollbackReservation("toolu_a");
        Assert.True(coord.TryReserveStartSlot("toolu_a")); // can reserve again after rollback
    }

    // ── auto-pane budget ─────────────────────────────────────────────────────────

    [Fact]
    public void TryClaimAutoPaneSlot_ToggleOff_ReturnsFalse()
    {
        var coord = new ExternalTaskCoordinator();
        // Toggle defaults to off
        Assert.False(coord.IsAutoPaneEnabled);
        Assert.False(coord.TryClaimAutoPaneSlot("toolu_a"));
        Assert.Equal(0, coord.AutoPanesInFlight);
    }

    [Fact]
    public void TryClaimAutoPaneSlot_ToggleOn_FirstClaimsSucceed()
    {
        var coord = new ExternalTaskCoordinator();
        coord.ToggleAutoPane(); // ON

        Assert.True(coord.TryClaimAutoPaneSlot("toolu_a"));
        Assert.True(coord.TryClaimAutoPaneSlot("toolu_b"));
        Assert.True(coord.TryClaimAutoPaneSlot("toolu_c"));
        Assert.Equal(3, coord.AutoPanesInFlight);
    }

    [Fact]
    public void TryClaimAutoPaneSlot_ExceedsCap_ReturnsFalse()
    {
        var coord = new ExternalTaskCoordinator();
        coord.ToggleAutoPane();

        coord.TryClaimAutoPaneSlot("toolu_a");
        coord.TryClaimAutoPaneSlot("toolu_b");
        coord.TryClaimAutoPaneSlot("toolu_c");

        // Cap is MaxAutoPanesInFlight (3) — 4th claim must fail.
        Assert.False(coord.TryClaimAutoPaneSlot("toolu_d"));
        Assert.Equal(3, coord.AutoPanesInFlight);
    }

    [Fact]
    public void TryClaimAutoPaneSlot_DuplicateIdNotDoubleCounted()
    {
        var coord = new ExternalTaskCoordinator();
        coord.ToggleAutoPane();

        Assert.True(coord.TryClaimAutoPaneSlot("toolu_a"));
        // Second claim for same id must be a no-op (counter must NOT increment to 2).
        Assert.False(coord.TryClaimAutoPaneSlot("toolu_a"));
        Assert.Equal(1, coord.AutoPanesInFlight);
    }

    // ── completion / counter accounting ──────────────────────────────────────────

    [Fact]
    public void ReleaseCompletion_DecrementsAutoPaneCounter_WhenTagged()
    {
        var coord = new ExternalTaskCoordinator();
        coord.ToggleAutoPane();
        coord.TryReserveStartSlot("toolu_a");
        coord.TryClaimAutoPaneSlot("toolu_a");
        Assert.Equal(1, coord.AutoPanesInFlight);

        coord.ReleaseCompletion("toolu_a");
        Assert.Equal(0, coord.AutoPanesInFlight);
    }

    [Fact]
    public void ReleaseCompletion_DoesNotDecrement_WhenNotTagged()
    {
        var coord = new ExternalTaskCoordinator();
        // Reservation but no auto-pane tag (toggle was off when start fired).
        coord.TryReserveStartSlot("toolu_a");
        Assert.Equal(0, coord.AutoPanesInFlight);

        coord.ReleaseCompletion("toolu_a");
        Assert.Equal(0, coord.AutoPanesInFlight); // still zero, no underflow
    }

    [Fact]
    public void ReleaseCompletion_CounterIsToggleStateAgnostic()
    {
        // Critical: this is the HIGH bug fix from prior review. If the user toggles OFF
        // while a Task is in flight, the eventual completion MUST still decrement.
        var coord = new ExternalTaskCoordinator();
        coord.ToggleAutoPane();         // ON
        coord.TryReserveStartSlot("toolu_a");
        coord.TryClaimAutoPaneSlot("toolu_a");
        Assert.Equal(1, coord.AutoPanesInFlight);

        coord.ToggleAutoPane();         // OFF — happens mid-session
        Assert.False(coord.IsAutoPaneEnabled);

        coord.ReleaseCompletion("toolu_a");
        Assert.Equal(0, coord.AutoPanesInFlight); // counter decremented despite toggle being off
    }

    [Fact]
    public void ReleaseCompletion_Idempotent_DoesNotUnderflow()
    {
        var coord = new ExternalTaskCoordinator();
        coord.ToggleAutoPane();
        coord.TryReserveStartSlot("toolu_a");
        coord.TryClaimAutoPaneSlot("toolu_a");

        coord.ReleaseCompletion("toolu_a");
        coord.ReleaseCompletion("toolu_a"); // calling twice must not take counter negative
        Assert.Equal(0, coord.AutoPanesInFlight);
    }

    [Fact]
    public void ReleaseCompletion_MapEntryAlsoCleared()
    {
        var coord = new ExternalTaskCoordinator();
        coord.TryReserveStartSlot("toolu_a");
        coord.RegisterStartedVm("toolu_a", NewVm());
        Assert.True(coord.TryFindForCompletion("toolu_a").found);

        coord.ReleaseCompletion("toolu_a");
        Assert.False(coord.TryFindForCompletion("toolu_a").found);
    }

    [Fact]
    public void AfterRelease_NewReservationForSameIdSucceeds()
    {
        // If Claude rotates session files and re-emits the same id (rare), the coordinator
        // should still allow re-tracking it after the prior cycle completes.
        var coord = new ExternalTaskCoordinator();
        coord.TryReserveStartSlot("toolu_a");
        coord.ReleaseCompletion("toolu_a");

        Assert.True(coord.TryReserveStartSlot("toolu_a"));
    }

    // ── toggle ───────────────────────────────────────────────────────────────────

    [Fact]
    public void ToggleAutoPane_FlipsAndReturnsNewState()
    {
        var coord = new ExternalTaskCoordinator();
        Assert.False(coord.IsAutoPaneEnabled);

        Assert.True(coord.ToggleAutoPane());
        Assert.True(coord.IsAutoPaneEnabled);

        Assert.False(coord.ToggleAutoPane());
        Assert.False(coord.IsAutoPaneEnabled);
    }

    // ── ctor guards ─────────────────────────────────────────────────────────────

    [Fact]
    public void RegisterStartedVm_NullVm_Throws()
    {
        var coord = new ExternalTaskCoordinator();
        coord.TryReserveStartSlot("toolu_a");
        Assert.Throws<System.ArgumentNullException>(
            () => coord.RegisterStartedVm("toolu_a", null!));
    }

    // ── concurrency smoke ────────────────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentReservations_OnlyOneSucceedsForSameId()
    {
        var coord = new ExternalTaskCoordinator();
        const string id = "toolu_concurrent";
        const int threads = 16;
        int succeeded = 0;

        var tasks = new Task[threads];
        for (int i = 0; i < threads; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                if (coord.TryReserveStartSlot(id))
                    Interlocked.Increment(ref succeeded);
            });
        }
        await Task.WhenAll(tasks);

        Assert.Equal(1, succeeded);
    }

    [Fact]
    public async Task ConcurrentClaims_RespectCap()
    {
        var coord = new ExternalTaskCoordinator();
        coord.ToggleAutoPane();

        const int threads = 32;
        var tasks = new Task<bool>[threads];
        for (int i = 0; i < threads; i++)
        {
            int idx = i;
            tasks[idx] = Task.Run(() => coord.TryClaimAutoPaneSlot($"toolu_{idx}"));
        }
        var results = await Task.WhenAll(tasks);

        int claimed = 0;
        foreach (var r in results) if (r) claimed++;

        Assert.Equal(ExternalTaskCoordinator.MaxAutoPanesInFlight, claimed);
        Assert.Equal(ExternalTaskCoordinator.MaxAutoPanesInFlight, coord.AutoPanesInFlight);
    }

    [Fact]
    public async Task ConcurrentClaims_BarrierReleased_StillRespectCap()
    {
        // Stronger version of ConcurrentClaims_RespectCap — uses a Barrier to release
        // all threads simultaneously at the read-check point, exercising the CAS-loop
        // race fix for TryClaimAutoPaneSlot.
        var coord = new ExternalTaskCoordinator();
        coord.ToggleAutoPane();

        const int threads = 64;
        using var barrier = new System.Threading.Barrier(threads);

        var tasks = new Task<bool>[threads];
        for (int i = 0; i < threads; i++)
        {
            int idx = i;
            tasks[idx] = Task.Run(() =>
            {
                barrier.SignalAndWait();           // all threads paused here
                return coord.TryClaimAutoPaneSlot($"toolu_b{idx}");
            });
        }
        var results = await Task.WhenAll(tasks);

        int claimed = 0;
        foreach (var r in results) if (r) claimed++;

        // Cap MUST NOT be busted, even with all threads racing through the gate.
        Assert.Equal(ExternalTaskCoordinator.MaxAutoPanesInFlight, claimed);
        Assert.Equal(ExternalTaskCoordinator.MaxAutoPanesInFlight, coord.AutoPanesInFlight);
    }

    // ── auto-pane stale tag sweep (MEDIUM bug fix) ────────────────────────────────

    [Fact]
    public void PruneStaleAutoPaneTags_ReclaimsExpiredTags()
    {
        var coord = new ExternalTaskCoordinator();
        coord.ToggleAutoPane();
        coord.TryClaimAutoPaneSlot("toolu_x");
        coord.TryClaimAutoPaneSlot("toolu_y");
        Assert.Equal(2, coord.AutoPanesInFlight);

        // Wait long enough that any non-zero maxAge expires (we use 0 ms below to force).
        var reclaimed = coord.PruneStaleAutoPaneTags(TimeSpan.FromMilliseconds(-1));

        Assert.Equal(2, reclaimed);
        Assert.Equal(0, coord.AutoPanesInFlight);
    }

    [Fact]
    public void PruneStaleAutoPaneTags_KeepsRecentTags()
    {
        var coord = new ExternalTaskCoordinator();
        coord.ToggleAutoPane();
        coord.TryClaimAutoPaneSlot("toolu_recent");

        // Generous window — recent tag must NOT be reclaimed.
        var reclaimed = coord.PruneStaleAutoPaneTags(TimeSpan.FromMinutes(5));

        Assert.Equal(0, reclaimed);
        Assert.Equal(1, coord.AutoPanesInFlight);
    }

    [Fact]
    public async Task PruneStaleAutoPaneTags_OnlyExpiredAreSwept()
    {
        var coord = new ExternalTaskCoordinator();
        coord.ToggleAutoPane();
        coord.TryClaimAutoPaneSlot("toolu_old");
        await Task.Delay(40);
        coord.TryClaimAutoPaneSlot("toolu_new");

        // 30 ms cutoff: only toolu_old is older than that.
        var reclaimed = coord.PruneStaleAutoPaneTags(TimeSpan.FromMilliseconds(30));

        Assert.Equal(1, reclaimed);
        Assert.Equal(1, coord.AutoPanesInFlight);
    }
}
