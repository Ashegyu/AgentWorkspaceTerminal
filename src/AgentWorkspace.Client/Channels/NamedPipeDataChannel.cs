using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Channels;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Client.Wire;

namespace AgentWorkspace.Client.Channels;

/// <summary>
/// Day-17 <see cref="IDataChannel"/> implementation. <see cref="SubscribeAsync"/> issues a
/// <see cref="RpcMethods.SubscribeFrames"/> RPC to register the pane on the daemon side, then
/// drains the per-pane channel populated by <see cref="ClientConnection"/>'s reader. On
/// completion (cancellation or pipe close) it issues the matching unsubscribe.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NamedPipeDataChannel : IDataChannel
{
    private readonly ClientConnection _connection;
    private readonly bool _ownsConnection;
    private bool _disposed;

    public NamedPipeDataChannel(ClientConnection connection, bool ownsConnection = false)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
        _ownsConnection = ownsConnection;
    }

    public async IAsyncEnumerable<PaneFrame> SubscribeAsync(
        PaneId pane,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var reader = _connection.RegisterFrameSink(pane);

        try
        {
            // Server-side enrolment must happen *after* the local sink is wired so we never miss a frame.
            _ = await _connection.InvokeAsync<PaneScopeRequest, EmptyResult>(
                RpcMethods.SubscribeFrames,
                new PaneScopeRequest(pane.ToString()),
                cancellationToken).ConfigureAwait(false);

            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var push))
                {
                    var bytes = Convert.FromBase64String(push.BytesBase64);
                    // Ownership: we hand the array to the consumer in a ReadOnlyMemory; PaneSession
                    // returns the underlying array to ArrayPool after consuming, but we don't rent
                    // here (we already allocated via Convert.FromBase64String), so the array will
                    // simply be garbage-collected. This matches the in-process channel semantics.
                    yield return new PaneFrame(pane, bytes, push.Sequence);
                }
            }
        }
        finally
        {
            _connection.UnregisterFrameSink(pane);

            // Best-effort unsubscribe on the daemon side. If the connection is already dead this
            // throws; we intentionally swallow because the daemon will clean up on its end anyway.
            using var unsubCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            unsubCts.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                _ = await _connection.InvokeAsync<PaneScopeRequest, EmptyResult>(
                    RpcMethods.UnsubscribeFrames,
                    new PaneScopeRequest(pane.ToString()),
                    unsubCts.Token).ConfigureAwait(false);
            }
            catch
            {
                // Daemon is gone or already cleaned up — fine.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsConnection)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
