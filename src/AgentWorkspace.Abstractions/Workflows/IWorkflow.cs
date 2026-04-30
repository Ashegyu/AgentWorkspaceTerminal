using System.Threading.Tasks;

namespace AgentWorkspace.Abstractions.Workflows;

/// <summary>
/// A workflow is a named, trigger-matched, async operation that runs within a
/// <see cref="WorkflowContext"/> and returns a <see cref="WorkflowResult"/>.
/// </summary>
public interface IWorkflow
{
    /// <summary>Human-readable name used for logging and Command Palette display.</summary>
    string Name { get; }

    /// <summary>
    /// Returns <see langword="true"/> when this workflow can handle <paramref name="trigger"/>.
    /// WorkflowEngine calls this to route a trigger to the right workflow.
    /// </summary>
    bool CanHandle(WorkflowTrigger trigger);

    ValueTask<WorkflowResult> ExecuteAsync(WorkflowContext context);
}
