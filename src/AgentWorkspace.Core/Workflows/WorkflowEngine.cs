using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Abstractions.Policy;
using AgentWorkspace.Abstractions.Workflows;
using AgentWorkspace.Core.Policy;
using AgentWorkspace.Core.Transcripts;

namespace AgentWorkspace.Core.Workflows;

/// <summary>
/// Singleton that owns the lifecycle of all running workflow executions.
/// Callers provide the registered workflows at construction; the engine routes each trigger
/// to the first workflow whose <see cref="IWorkflow.CanHandle"/> returns true.
/// Runs in App.Wpf, not in the Daemon, so approval dialogs have a UI dispatcher available.
/// </summary>
public sealed class WorkflowEngine : IAsyncDisposable
{
    private readonly IReadOnlyList<IWorkflow> _workflows;
    private readonly WorkflowDependencies _deps;
    private readonly PolicyContext _policyContext;

    private readonly ConcurrentDictionary<WorkflowExecutionId, CancellationTokenSource> _running = new();

    /// <param name="workflows">Registered workflow implementations to route triggers to.</param>
    /// <param name="agentAdapter">The underlying agent adapter.</param>
    /// <param name="approvalGateway">Gateway for obtaining user approval before risky actions.</param>
    /// <param name="policyEngine">Optional policy engine; defaults to pass-through.</param>
    /// <param name="policyContext">Optional policy context; defaults to <see cref="PolicyLevel.SafeDev"/>.</param>
    /// <param name="sinkFactory">
    ///   Optional factory used to open a <see cref="TranscriptSink"/> per session.
    ///   When non-null the adapter is wrapped with <see cref="TranscriptingAgentAdapter"/> so
    ///   every session whose <see cref="AgentSessionOptions.SaveTranscript"/> is
    ///   <see langword="true"/> writes a JSONL transcript to disk.
    ///   Pass <see langword="null"/> (the default) to disable transcript writing — this keeps
    ///   tests hermetic without polluting <c>%LOCALAPPDATA%\AgentWorkspace\transcripts\</c>.
    ///   Production callers pass <see cref="TranscriptSink.Open"/>.
    /// </param>
    public WorkflowEngine(
        IReadOnlyList<IWorkflow> workflows,
        IAgentAdapter agentAdapter,
        IApprovalGateway approvalGateway,
        IPolicyEngine? policyEngine = null,
        PolicyContext? policyContext = null,
        Func<AgentSessionId, string?, string?, AgentSessionId?, TranscriptSink>? sinkFactory = null)
    {
        _workflows = workflows;

        IAgentAdapter effectiveAdapter = sinkFactory is not null
            ? new TranscriptingAgentAdapter(agentAdapter, sinkFactory)
            : agentAdapter;

        _deps = new WorkflowDependencies(
            effectiveAdapter,
            approvalGateway,
            policyEngine ?? PassThroughPolicyEngine.Instance);
        _policyContext = policyContext
            ?? new PolicyContext(WorkspaceRoot: null, Level: PolicyLevel.SafeDev, AgentName: agentAdapter.Name);
    }

    /// <summary>
    /// Dispatches <paramref name="trigger"/> to a matching workflow and starts it.
    /// Returns immediately; the execution runs on the thread-pool.
    /// Returns <see langword="null"/> when no registered workflow handles the trigger.
    /// </summary>
    public WorkflowExecutionId? TriggerAsync(WorkflowTrigger trigger)
    {
        var workflow = FindWorkflow(trigger);
        if (workflow is null) return null;

        var id = WorkflowExecutionId.New();
        var cts = new CancellationTokenSource();
        _running[id] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                var ctx = new WorkflowContext(id, trigger, _deps, _policyContext, cts.Token);
                await workflow.ExecuteAsync(ctx).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* cancelled — normal path */ }
            catch (Exception) { /* workflow errors are surfaced via WorkflowResult; swallow here */ }
            finally
            {
                _running.TryRemove(id, out _);
                cts.Dispose();
            }
        }, cts.Token);

        return id;
    }

    /// <summary>
    /// Starts <paramref name="trigger"/> and awaits the result.
    /// Returns <see langword="null"/> when no workflow handles the trigger.
    /// </summary>
    public async ValueTask<WorkflowResult?> RunAsync(
        WorkflowTrigger trigger,
        CancellationToken cancellationToken = default)
    {
        var workflow = FindWorkflow(trigger);
        if (workflow is null) return null;

        var id = WorkflowExecutionId.New();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _running[id] = cts;

        try
        {
            var ctx = new WorkflowContext(id, trigger, _deps, _policyContext, cts.Token);
            return await workflow.ExecuteAsync(ctx).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new WorkflowCancelled();
        }
        catch (Exception ex)
        {
            return new WorkflowFailure(ex.Message);
        }
        finally
        {
            _running.TryRemove(id, out _);
            cts.Dispose();
        }
    }

    /// <summary>Cancels an in-flight execution. No-op if the id is unknown.</summary>
    public void Cancel(WorkflowExecutionId id)
    {
        if (_running.TryGetValue(id, out var cts))
            cts.Cancel();
    }

    public bool IsRunning(WorkflowExecutionId id) => _running.ContainsKey(id);

    private IWorkflow? FindWorkflow(WorkflowTrigger trigger)
    {
        foreach (var w in _workflows)
            if (w.CanHandle(trigger)) return w;
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, cts) in _running)
        {
            try { cts.Cancel(); } catch { /* best effort */ }
        }

        // give running tasks a moment to observe cancellation
        await Task.Delay(100).ConfigureAwait(false);

        foreach (var (_, cts) in _running)
        {
            try { cts.Dispose(); } catch { /* best effort */ }
        }
        _running.Clear();
    }
}
