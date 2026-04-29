using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Layout;
using AgentWorkspace.Abstractions.Pty;
using AgentWorkspace.Core.Layout;

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

    public Workspace(
        Func<PaneId, PaneSession> sessionFactory,
        Func<PaneStartOptions> defaultOptionsFactory,
        PaneId initial)
    {
        _sessionFactory = sessionFactory;
        _defaultOptionsFactory = defaultOptionsFactory;
        Layout = new BinaryLayoutManager(initial);
    }

    public BinaryLayoutManager Layout { get; }

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

        try
        {
            await session.StartAsync(_defaultOptionsFactory(), cancellationToken).ConfigureAwait(false);
            return split.NewPane;
        }
        catch
        {
            // Roll the layout back so the tree never references a pane without a running PTY.
            try { Layout.Close(split.NewPane); } catch { /* swallow */ }
            _sessions.TryRemove(split.NewPane, out _);
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
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
