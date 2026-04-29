using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Layout;
using AgentWorkspace.Abstractions.Sessions;
using AgentWorkspace.Client.Channels;
using AgentWorkspace.Client.Wire;

namespace AgentWorkspace.Client.Sessions;

/// <summary>
/// Day-17 <see cref="ISessionStore"/> implementation that proxies every call to the daemon-side
/// SQLite store via RPC. The daemon is the only process that talks to <c>sessions.db</c>; the
/// client never touches the file directly anymore.
/// </summary>
/// <remarks>
/// Layout serialisation: the daemon already owns <c>LayoutJson</c> for the SQLite schema, so the
/// wire format simply forwards the JSON string. <see cref="SaveLayoutAsync"/> serialises here on
/// the client side using a private helper to avoid leaking <c>AgentWorkspace.Core</c> into the
/// client; <see cref="AttachAsync"/> reverses with the same helper.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class RemoteSessionStore : ISessionStore
{
    private readonly ClientConnection _connection;

    public RemoteSessionStore(ClientConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        _ = await _connection.InvokeAsync<EmptyResult, EmptyResult>(
            RpcMethods.StoreInitialize, new EmptyResult(), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<SessionId> CreateAsync(string name, string? workspaceRoot, CancellationToken cancellationToken)
    {
        var res = await _connection.InvokeAsync<CreateSessionRequest, CreateSessionResult>(
            RpcMethods.StoreCreateSession,
            new CreateSessionRequest(name, workspaceRoot),
            cancellationToken).ConfigureAwait(false);
        return SessionId.Parse(res.SessionId);
    }

    public async ValueTask<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken cancellationToken)
    {
        var res = await _connection.InvokeAsync<EmptyResult, ListSessionsResult>(
            RpcMethods.StoreListSessions, new EmptyResult(), cancellationToken).ConfigureAwait(false);
        return res.Sessions.Select(s => new SessionInfo(
            SessionId.Parse(s.SessionId),
            s.Name,
            s.WorkspaceRoot,
            s.CreatedAtUtc,
            s.LastAttachedAtUtc)).ToList();
    }

    public async ValueTask<SessionSnapshot?> AttachAsync(SessionId id, CancellationToken cancellationToken)
    {
        var res = await _connection.InvokeAsync<AttachSessionRequest, AttachSessionResult>(
            RpcMethods.StoreAttachSession,
            new AttachSessionRequest(id.ToString()),
            cancellationToken).ConfigureAwait(false);

        if (!res.Found || res.Info is null || res.LayoutJson is null
            || res.FocusedPaneId is null || res.Panes is null)
        {
            return null;
        }

        var info = new SessionInfo(
            SessionId.Parse(res.Info.SessionId),
            res.Info.Name,
            res.Info.WorkspaceRoot,
            res.Info.CreatedAtUtc,
            res.Info.LastAttachedAtUtc);

        var layoutNode = LayoutJsonClient.Deserialize(res.LayoutJson);
        var snapshot = new LayoutSnapshot(layoutNode, PaneId.Parse(res.FocusedPaneId));
        var panes = res.Panes.Select(p => new PaneSpec(
            PaneId.Parse(p.PaneId),
            p.Command,
            p.Arguments,
            p.WorkingDirectory,
            p.Environment)).ToList();

        return new SessionSnapshot(info, snapshot, panes);
    }

    public async ValueTask UpsertPaneAsync(SessionId id, PaneSpec pane, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pane);
        var dto = new PaneSpecDto(
            pane.Pane.ToString(),
            pane.Command,
            pane.Arguments,
            pane.WorkingDirectory,
            pane.Environment is null ? null : new Dictionary<string, string>(pane.Environment));

        _ = await _connection.InvokeAsync<UpsertPaneRequest, EmptyResult>(
            RpcMethods.StoreUpsertPane,
            new UpsertPaneRequest(id.ToString(), dto),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DeletePaneAsync(SessionId id, PaneId pane, CancellationToken cancellationToken)
    {
        _ = await _connection.InvokeAsync<DeletePaneRequest, EmptyResult>(
            RpcMethods.StoreDeletePane,
            new DeletePaneRequest(id.ToString(), pane.ToString()),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SaveLayoutAsync(SessionId id, LayoutSnapshot layout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var json = LayoutJsonClient.Serialize(layout.Root);
        _ = await _connection.InvokeAsync<SaveLayoutRequest, EmptyResult>(
            RpcMethods.StoreSaveLayout,
            new SaveLayoutRequest(id.ToString(), json, layout.Focused.ToString()),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DeleteAsync(SessionId id, CancellationToken cancellationToken)
    {
        _ = await _connection.InvokeAsync<DeleteSessionRequest, EmptyResult>(
            RpcMethods.StoreDeleteSession,
            new DeleteSessionRequest(id.ToString()),
            cancellationToken).ConfigureAwait(false);
    }
}
