using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Abstractions.Workflows;

namespace AgentWorkspace.App.Wpf.Approval;

/// <summary>
/// WPF implementation of <see cref="IApprovalGateway"/>. Marshals the approval dialog
/// to the UI thread via Application.Current.Dispatcher.
/// </summary>
public sealed class DialogApprovalGateway : IApprovalGateway
{
    public ValueTask<ApprovalDecision> RequestApprovalAsync(
        IReadOnlyList<ActionRequestEvent> actions,
        CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var reg = cancellationToken.Register(() =>
            tcs.TrySetCanceled(cancellationToken));

        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (cancellationToken.IsCancellationRequested) return;

            var dialog = new ApprovalDialog(actions)
            {
                Owner = Application.Current.MainWindow,
            };
            dialog.ShowDialog();

            if (dialog.WasApproved)
            {
                var ids = actions.Select(a => a.ActionId).ToList();
                tcs.TrySetResult(new ApprovalDecision(true, ids, []));
            }
            else
            {
                var ids = actions.Select(a => a.ActionId).ToList();
                tcs.TrySetResult(new ApprovalDecision(false, [], ids));
            }
        });

        return new ValueTask<ApprovalDecision>(tcs.Task);
    }
}
