using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Channels;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Pty;
using AgentWorkspace.Client.Wire;

namespace AgentWorkspace.Client.Channels;

/// <summary>
/// Day-17 <see cref="IControlChannel"/> implementation that talks to the daemon over the
/// authenticated NamedPipe owned by <see cref="ClientConnection"/>. Translates each method
/// into a single RPC; exit notifications are forwarded from the connection's
/// <see cref="ClientConnection.PaneExitedReceived"/> event.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NamedPipeControlChannel : IControlChannel
{
    private readonly ClientConnection _connection;
    private readonly bool _ownsConnection;
    private bool _disposed;

    public NamedPipeControlChannel(ClientConnection connection, bool ownsConnection = false)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
        _ownsConnection = ownsConnection;
        _connection.PaneExitedReceived += OnPaneExitedReceived;
    }

    public event EventHandler<PaneExitedEventArgs>? PaneExited;

    public async ValueTask<PaneState> StartPaneAsync(
        PaneId id,
        PaneStartOptions options,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(options);

        var req = new StartPaneRequest(
            id.ToString(),
            options.Command,
            options.Arguments,
            options.WorkingDirectory,
            options.Environment is null ? null : new Dictionary<string, string>(options.Environment),
            options.InitialColumns,
            options.InitialRows);

        var res = await _connection.InvokeAsync<StartPaneRequest, StartPaneResult>(
            RpcMethods.StartPane, req, cancellationToken).ConfigureAwait(false);

        return Enum.TryParse<PaneState>(res.State, ignoreCase: false, out var state)
            ? state
            : PaneState.Faulted;
    }

    public async ValueTask WriteInputAsync(
        PaneId id,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var req = new WriteInputRequest(id.ToString(), Convert.ToBase64String(bytes.Span));
        _ = await _connection.InvokeAsync<WriteInputRequest, EmptyResult>(
            RpcMethods.WriteInput, req, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ResizePaneAsync(
        PaneId id,
        short columns,
        short rows,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var req = new ResizePaneRequest(id.ToString(), columns, rows);
        _ = await _connection.InvokeAsync<ResizePaneRequest, EmptyResult>(
            RpcMethods.ResizePane, req, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SignalPaneAsync(
        PaneId id,
        PtySignal signal,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var req = new SignalPaneRequest(id.ToString(), signal.ToString());
        _ = await _connection.InvokeAsync<SignalPaneRequest, EmptyResult>(
            RpcMethods.SignalPane, req, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<int> ClosePaneAsync(
        PaneId id,
        KillMode mode,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var req = new ClosePaneRequest(id.ToString(), mode.ToString());
        var res = await _connection.InvokeAsync<ClosePaneRequest, ClosePaneResult>(
            RpcMethods.ClosePane, req, cancellationToken).ConfigureAwait(false);
        return res.ExitCode;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _connection.PaneExitedReceived -= OnPaneExitedReceived;
        if (_ownsConnection)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void OnPaneExitedReceived(object? sender, PaneExitedPushPayload payload)
    {
        if (_disposed) return;
        try
        {
            var paneId = PaneId.Parse(payload.PaneId);
            PaneExited?.Invoke(this, new PaneExitedEventArgs(paneId, payload.ExitCode));
        }
        catch (FormatException) { /* malformed push */ }
    }
}
