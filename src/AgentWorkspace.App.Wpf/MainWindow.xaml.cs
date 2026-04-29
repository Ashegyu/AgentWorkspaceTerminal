using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Layout;
using AgentWorkspace.Abstractions.Pty;
using AgentWorkspace.App.Wpf.CommandPalette;
using Microsoft.Web.WebView2.Core;

namespace AgentWorkspace.App.Wpf;

/// <summary>
/// Hosts the WebView2 SPA, owns the multi-pane <see cref="Workspace"/>, and bridges JSON
/// messages between the JS bridge and the .NET runtime.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class MainWindow : Window
{
    private const string VirtualHost = "agentworkspace.local";

    private Workspace? _workspace;
    private string _shell = "cmd.exe";
    private bool _rendererReady;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;

        Palette.SetCommands(BuildCommands());
        Palette.Dismissed += (_, _) => _ = PostToRendererAsync(Envelope.FocusTerm());
    }

    /// <summary>
    /// Ten MVP-1+MVP-2 commands. The terminal-shaping ones from MVP-1 are kept; five layout
    /// commands are added (split right/down, close, focus next/previous).
    /// </summary>
    private IReadOnlyList<CommandEntry> BuildCommands() => new[]
    {
        // MVP-1 commands ------------------------------------------------------------------
        new CommandEntry(
            "Restart Shell",
            "kills the focused pane's child tree and starts a fresh shell",
            "restart shell relaunch",
            ct => ActiveSession()?.RestartAsync(ct) ?? ValueTask.CompletedTask),

        new CommandEntry(
            "Send Ctrl+C",
            "interrupt the foreground program in the focused pane",
            "send ctrl c interrupt sigint cancel",
            ct => ActiveSession()?.SendInterruptAsync(ct) ?? ValueTask.CompletedTask),

        new CommandEntry(
            "Clear Terminal",
            "scrollback stays — only the focused pane's view is cleared",
            "clear terminal screen reset",
            _ =>
            {
                var s = ActiveSession();
                return s is null ? ValueTask.CompletedTask : PostToRendererAsync(Envelope.Clear(s.Id));
            }),

        new CommandEntry("Increase Font Size", "+1 px", "font size increase larger zoom in",
            _ => PostToRendererAsync(Envelope.FontSizeDelta(+1))),

        new CommandEntry("Decrease Font Size", "-1 px", "font size decrease smaller zoom out",
            _ => PostToRendererAsync(Envelope.FontSizeDelta(-1))),

        // MVP-2 layout commands ----------------------------------------------------------
        new CommandEntry(
            "Split Right",
            "horizontal split — new pane to the right of the focused one",
            "split right horizontal new pane",
            ct => OpenSplitAsync(SplitDirection.Horizontal, ct)),

        new CommandEntry(
            "Split Down",
            "vertical split — new pane below the focused one",
            "split down vertical new pane",
            ct => OpenSplitAsync(SplitDirection.Vertical, ct)),

        new CommandEntry(
            "Close Pane",
            "shuts down the focused pane (rejected if it is the only one)",
            "close pane kill remove",
            ct => CloseFocusedAsync(ct)),

        new CommandEntry(
            "Focus Next Pane",
            "cycle focus to the next pane (left-to-right)",
            "focus next pane cycle",
            _ => BroadcastFocusChange(_workspace!.Layout.FocusNext())),

        new CommandEntry(
            "Focus Previous Pane",
            "cycle focus to the previous pane",
            "focus previous pane cycle back",
            _ => BroadcastFocusChange(_workspace!.Layout.FocusPrevious())),
    };

    private PaneSession? ActiveSession()
    {
        if (_workspace is null) return null;
        var focused = _workspace.Layout.Current.Focused;
        return _workspace.Sessions.TryGetValue(focused, out var s) ? s : null;
    }

    private async ValueTask OpenSplitAsync(SplitDirection direction, CancellationToken ct)
    {
        if (_workspace is null) return;
        var focused = _workspace.Layout.Current.Focused;
        try
        {
            var newPane = await _workspace.OpenSplitAsync(focused, direction, ct).ConfigureAwait(true);
            await PostToRendererAsync(Envelope.OpenPane(newPane)).ConfigureAwait(true);
            await PostToRendererAsync(Envelope.Layout(_workspace.Layout.Current)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"split failed: {ex.Message}";
        }
    }

    private async ValueTask CloseFocusedAsync(CancellationToken ct)
    {
        if (_workspace is null) return;
        var focused = _workspace.Layout.Current.Focused;
        try
        {
            await _workspace.CloseAsync(focused, ct).ConfigureAwait(true);
            await PostToRendererAsync(Envelope.ClosePane(focused)).ConfigureAwait(true);
            await PostToRendererAsync(Envelope.Layout(_workspace.Layout.Current)).ConfigureAwait(true);
        }
        catch (InvalidOperationException ex)
        {
            // E.g. attempt to close the last remaining pane.
            StatusText.Text = ex.Message;
        }
    }

    private ValueTask BroadcastFocusChange(LayoutSnapshot snap)
        => PostToRendererAsync(Envelope.Layout(snap));

    private void TogglePalette()
    {
        if (Palette.IsOpen) Palette.Hide();
        else Palette.Show();
    }

    private void OnPaletteShortcut(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
    {
        TogglePalette();
        e.Handled = true;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await InitializeWebViewAsync().ConfigureAwait(true);
            await StartFirstPaneAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Startup failed: {ex.Message}";
        }
    }

    private async Task InitializeWebViewAsync()
    {
        StatusText.Text = "Bootstrapping WebView2…";

        string userDataDir = Path.Combine(AppContext.BaseDirectory, "WebView2Data");
        var env = await CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: userDataDir).ConfigureAwait(true);

        await WebView.EnsureCoreWebView2Async(env).ConfigureAwait(true);

        string webRoot = Path.Combine(AppContext.BaseDirectory, "web", "terminal");
        if (!Directory.Exists(webRoot))
        {
            throw new DirectoryNotFoundException($"SPA folder missing at '{webRoot}'.");
        }

        WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            VirtualHost,
            webRoot,
            CoreWebView2HostResourceAccessKind.Allow);

        WebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

        WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
#if !DEBUG
        WebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
#endif
        WebView.CoreWebView2.Settings.IsZoomControlEnabled = false;
        WebView.CoreWebView2.Settings.IsSwipeNavigationEnabled = false;

        WebView.CoreWebView2.Navigate($"https://{VirtualHost}/index.html");
    }

    private async Task StartFirstPaneAsync()
    {
        await WaitForRendererReadyAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);

        _shell = ResolveDefaultShell();
        var firstPane = PaneId.New();

        _workspace = new Workspace(
            sessionFactory: id => new PaneSession(id, PostToRendererAsync),
            defaultOptionsFactory: () => DefaultStartOptions(_shell),
            initial: firstPane);

        var session = _workspace.Register(firstPane);

        // Tell the renderer to create the xterm container *before* we send the layout, so the
        // very first 'output' chunk has somewhere to land.
        await PostToRendererAsync(Envelope.OpenPane(firstPane)).ConfigureAwait(true);
        await PostToRendererAsync(Envelope.Layout(_workspace.Layout.Current)).ConfigureAwait(true);

        await session.StartAsync(DefaultStartOptions(_shell), CancellationToken.None).ConfigureAwait(true);

        StatusText.Text = $"pane {firstPane.ToString()[..6]}…  shell={_shell}";
    }

    private static PaneStartOptions DefaultStartOptions(string shell) => new(
        Command: shell,
        Arguments: Array.Empty<string>(),
        WorkingDirectory: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment: null,
        InitialColumns: 120,
        InitialRows: 30);

    private async Task WaitForRendererReadyAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!_rendererReady)
        {
            try { await Task.Delay(25, cts.Token).ConfigureAwait(true); }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("WebView2 renderer did not signal ready in time.");
            }
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string raw = e.TryGetWebMessageAsString();
        if (string.IsNullOrEmpty(raw)) return;

        JsonElement root;
        try { root = JsonDocument.Parse(raw).RootElement; }
        catch (JsonException) { return; }

        if (!root.TryGetProperty("type", out var typeProp)) return;

        switch (typeProp.GetString())
        {
            case "ready":
                _rendererReady = true;
                break;

            case "input":
                HandleInput(root);
                break;

            case "resize":
                HandleResize(root);
                break;

            case "focusPane":
                HandleFocusPane(root);
                break;

            case "paletteToggle":
                TogglePalette();
                break;

            case "log":
                if (root.TryGetProperty("message", out var msg))
                {
                    StatusText.Text = msg.GetString() ?? string.Empty;
                }
                break;
        }
    }

    private void HandleInput(JsonElement root)
    {
        if (_workspace is null) return;
        if (!TryReadPaneId(root, out var paneId)) return;
        if (!_workspace.Sessions.TryGetValue(paneId, out var session)) return;
        if (!root.TryGetProperty("b64", out var b64Prop)) return;
        string? b64 = b64Prop.GetString();
        if (string.IsNullOrEmpty(b64)) return;

        byte[] bytes = Convert.FromBase64String(b64);
        _ = session.WriteInputAsync(bytes, CancellationToken.None);
    }

    private void HandleResize(JsonElement root)
    {
        if (_workspace is null) return;
        if (!TryReadPaneId(root, out var paneId)) return;
        if (!_workspace.Sessions.TryGetValue(paneId, out var session)) return;
        if (!root.TryGetProperty("cols", out var c) || !root.TryGetProperty("rows", out var r)) return;
        short cols = (short)Math.Clamp(c.GetInt32(), 1, short.MaxValue);
        short rows = (short)Math.Clamp(r.GetInt32(), 1, short.MaxValue);
        _ = session.ResizeAsync(cols, rows, CancellationToken.None);
    }

    private void HandleFocusPane(JsonElement root)
    {
        if (_workspace is null) return;
        if (!TryReadPaneId(root, out var paneId)) return;
        try
        {
            var snap = _workspace.Layout.Focus(paneId);
            _ = PostToRendererAsync(Envelope.Layout(snap));
        }
        catch (ArgumentException)
        {
            // Pane gone between message dispatch and our handling; ignore.
        }
    }

    private static bool TryReadPaneId(JsonElement root, out PaneId paneId)
    {
        paneId = default;
        if (!root.TryGetProperty("paneId", out var p)) return false;
        string? s = p.GetString();
        if (string.IsNullOrEmpty(s)) return false;
        try
        {
            paneId = PaneId.Parse(s);
            return true;
        }
        catch (FormatException) { return false; }
    }

    private ValueTask PostToRendererAsync(string envelope)
    {
        if (Dispatcher.CheckAccess())
        {
            try { WebView.CoreWebView2?.PostWebMessageAsString(envelope); }
            catch (InvalidOperationException) { /* webview disposed */ }
            return ValueTask.CompletedTask;
        }
        return new ValueTask(Dispatcher.InvokeAsync(() =>
        {
            try { WebView.CoreWebView2?.PostWebMessageAsString(envelope); }
            catch (InvalidOperationException) { /* webview disposed */ }
        }).Task);
    }

    private static string ResolveDefaultShell()
    {
        foreach (string candidate in new[] { "pwsh.exe", "powershell.exe", "cmd.exe" })
        {
            string? full = SearchPath(candidate);
            if (full is not null) return candidate;
        }
        return "cmd.exe";
    }

    private static string? SearchPath(string fileName)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (string dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir)) continue;
            try
            {
                string full = Path.Combine(dir, fileName);
                if (File.Exists(full)) return full;
            }
            catch
            {
                // skip invalid PATH entries
            }
        }
        return null;
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        if (_workspace is not null)
        {
            try { await _workspace.DisposeAsync().ConfigureAwait(true); }
            catch { /* swallow */ }
        }
    }
}
