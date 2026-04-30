using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AgentWorkspace.Abstractions.Workflows;

namespace AgentWorkspace.App.Wpf.Approval;

/// <summary>
/// WPF implementation of <see cref="IApprovalGateway"/>. Marshals the approval dialog
/// to the UI thread via Application.Current.Dispatcher.
/// </summary>
public sealed class DialogApprovalGateway : IApprovalGateway
{
    public ValueTask<ApprovalDecision> RequestApprovalAsync(
        IReadOnlyList<ApprovalRequestItem> items,
        CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var reg = cancellationToken.Register(() =>
            tcs.TrySetCanceled(cancellationToken));

        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (cancellationToken.IsCancellationRequested) return;

            var dialog = new ApprovalDialog(items)
            {
                Owner = Application.Current.MainWindow,
            };
            dialog.ShowDialog();

            var ids = items.Select(i => i.Action.ActionId).ToList();
            tcs.TrySetResult(dialog.WasApproved
                ? new ApprovalDecision(true, ids, [])
                : new ApprovalDecision(false, [], ids));
        });

        return new ValueTask<ApprovalDecision>(tcs.Task);
    }
}
