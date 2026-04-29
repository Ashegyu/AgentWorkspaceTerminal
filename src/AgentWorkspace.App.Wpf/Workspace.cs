using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Layout;
using AgentWorkspace.Abstractions.Pty;
using AgentWorkspace.Abstractions.Sessions;

namespace AgentWorkspace.App.Wpf;

/// <summary>
/// Owns the layout tree plus all <see cref="PaneSession"/> instances for the workspace. The
/// MainWindow drives <see cref="OpenSplitAsync"/>, <see cref="CloseAsync"/>, focus changes etc.
/// and reads <see cref="Layout"/> for broadcast to the renderer.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Workspace : IAsyncDisposable
{
    private readonly Func<PaneId, PaneSession> _sessionFactory;
    private readonly Func<PaneStartOptions> _defaultOptionsFactory;
    private readonly ConcurrentDictionary<PaneId, PaneSession> _sessions = new();
    private readonly ISessionStore? _store;
    private readonly SessionId? _sessionId;

    public Workspace(
        Func<PaneId, PaneSession> sessionFactory,
        Func<PaneStartOptions> defaultOptionsFactory,
        PaneId initial,
        ISessionStore? store = null,
        SessionId? sessionId = null)
    {
        _sessionFactory = sessionFactory;
        _defaultOptionsFactory = defaultOptionsFactory;
        _store = store;
        _sessionId = sessionId;
        Layout = new BinaryLayoutManager(initial);
    }

    /// <summary>
    /// Variant of the constructor that accepts a pre-built layout (used when restoring a session).
    /// The caller is responsible for registering and starting each pane via <see cref="Register"/>.
    /// </summary>
    public Workspace(
        Func<PaneId, PaneSession> sessionFactory,
        Func<PaneStartOptions> defaultOptionsFactory,
        LayoutSnapshot initialLayout,
        ISessionStore? store = null,
        SessionId? sessionId = null)
    {
        _sessionFactory = sessionFactory;
        _defaultOptionsFactory = defaultOptionsFactory;
        _store = store;
        _sessionId = sessionId;
        Layout = BinaryLayoutManager.FromSnapshot(initialLayout);
    }

    public BinaryLayoutManager Layout { get; }
    public SessionId? SessionId => _sessionId;

    public IReadOnlyDictionary<PaneId, PaneSession> Sessions => _sessions;

    /// <summary>
    /// Adds a session for a pane that is already in the layout. Used for the workspace's first
    /// pane created out-of-band; <see cref="OpenSplitAsync"/> handles subsequent panes.
    /// </summary>
    public PaneSession Register(PaneId pane)
    {
        var s = _sessionFactory(pane);
        _sessions[pane] = s;
        return s;
    }

    /// <summary>
    /// Splits <paramref name="target"/> in <paramref name="direction"/>, then starts a fresh
    /// pane session for the new <see cref="PaneId"/> using the captured default options.
    /// </summary>
    public async ValueTask<PaneId> OpenSplitAsync(
        PaneId target,
        SplitDirection direction,
        CancellationToken cancellationToken)
    {
        var split = Layout.Split(target, direction);
        var session = _sessionFactory(split.NewPane);
        _sessions[split.NewPane] = session;

        var options = _defaultOptionsFactory();
        try
        {
            await session.StartAsync(options, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Roll the layout back so the tree never references a pane without a running PTY.
            try { Layout.Close(split.NewPane); } catch { /* swallow */ }
            _sessions.TryRemove(split.NewPane, out _);
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        await PersistPaneAsync(split.NewPane, options, cancellationToken).ConfigureAwait(false);
        await PersistLayoutAsync(cancellationToken).ConfigureAwait(false);
        return split.NewPane;
    }

    /// <summary>
    /// Removes <paramref name="target"/> from the layout and shuts down its session.
    /// Refuses if it would close the last remaining pane.
    /// </summary>
    public async ValueTask CloseAsync(PaneId target, CancellationToken cancellationToken)
    {
        Layout.Close(target);   // throws if last pane

        if (_sessions.TryRemove(target, out var session))
        {
            try { await session.DisposeAsync().ConfigureAwait(false); }
            catch { /* swallow */ }
        }

        if (_store is not null && _sessionId is { } sid)
        {
            try { await _store.DeletePaneAsync(sid, target, cancellationToken).ConfigureAwait(false); }
            catch { /* persistence is best-effort */ }
        }
        await PersistLayoutAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Public for hosts that mutate the layout outside of <see cref="OpenSplitAsync"/> /
    /// <see cref="CloseAsync"/> (e.g. focus changes from the renderer). Best-effort.
    /// </summary>
    public async ValueTask PersistLayoutAsync(CancellationToken cancellationToken)
    {
        if (_store is null || _sessionId is null) return;
        try
        {
            await _store.SaveLayoutAsync(_sessionId.Value, Layout.Current, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Persistence is non-blocking — never fail a UI op because the disk hiccuped.
        }
    }

    /// <summary>
    /// Persists the meta of the *initial* (Register-spawned) pane. The split helper above
    /// handles new panes itself.
    /// </summary>
    public async ValueTask PersistInitialPaneAsync(
        PaneId pane,
        PaneStartOptions options,
        CancellationToken cancellationToken)
        => await PersistPaneAsync(pane, options, cancellationToken).ConfigureAwait(false);

    private async ValueTask PersistPaneAsync(PaneId pane, PaneStartOptions options, CancellationToken ct)
    {
        if (_store is null || _sessionId is null) return;
        try
        {
            var spec = new PaneSpec(
                pane,
                options.Command,
                options.Arguments.ToArray(),
                options.WorkingDirectory,
                options.Environment is null ? null : options.Environment.ToDictionary(p => p.Key, p => p.Value));
            await _store.UpsertPaneAsync(_sessionId.Value, spec, ct).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort.
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
        {
            try { await session.DisposeAsync().ConfigureAwait(false); }
            catch { /* swallow */ }
        }
        _sessions.Clear();
    }
}
