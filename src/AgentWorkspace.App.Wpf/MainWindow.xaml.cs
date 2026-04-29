using System;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Pty;
using Microsoft.Web.WebView2.Core;

namespace AgentWorkspace.App.Wpf;

/// <summary>
/// Hosts the WebView2 SPA, owns the single <see cref="PaneSession"/> for MVP-1, and bridges
/// JSON messages between the JS bridge and the .NET runtime.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class MainWindow : Window
{
    private const string VirtualHost = "agentworkspace.local";

    private PaneSession? _session;
    private bool _rendererReady;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await InitializeWebViewAsync().ConfigureAwait(true);
            await StartShellPaneAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Startup failed: {ex.Message}";
        }
    }

    private async Task InitializeWebViewAsync()
    {
        StatusText.Text = "Bootstrapping WebView2…";

        // Keep the browser data folder alongside the binary so we don't pollute the user profile
        // before MVP-3 (Daemon) decides on its own data location.
        string userDataDir = Path.Combine(AppContext.BaseDirectory, "WebView2Data");
        var env = await CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: userDataDir).ConfigureAwait(true);

        await WebView.EnsureCoreWebView2Async(env).ConfigureAwait(true);

        // Map the SPA folder to a virtual host. SetVirtualHostNameToFolderMapping requires the
        // mapping to point at a *real* directory; we ship the SPA via the csproj <None> include.
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

        // Lock down what the page can do — no zoom, no devtools in release, no swipe nav.
        WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
#if !DEBUG
        WebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
#endif
        WebView.CoreWebView2.Settings.IsZoomControlEnabled = false;
        WebView.CoreWebView2.Settings.IsSwipeNavigationEnabled = false;

        WebView.CoreWebView2.Navigate($"https://{VirtualHost}/index.html");
    }

    private async Task StartShellPaneAsync()
    {
        // Wait for the renderer to send "ready" before we create the PTY, otherwise the first
        // bytes of output would arrive before the xterm instance exists.
        await WaitForRendererReadyAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);

        string shell = ResolveDefaultShell();
        var paneId = PaneId.New();
        _session = new PaneSession(paneId, PostToRendererAsync);

        await _session.StartAsync(new PaneStartOptions(
            Command: shell,
            Arguments: Array.Empty<string>(),
            WorkingDirectory: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment: null,
            // Initial size is a reasonable default; the renderer will immediately overwrite it
            // via a resize message once xterm computes its layout.
            InitialColumns: 120,
            InitialRows: 30), CancellationToken.None).ConfigureAwait(true);

        StatusText.Text = $"pane {paneId.ToString()[..6]}…  shell={shell}";
    }

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
        // The bridge always sends JSON encoded as a string.
        string raw = e.TryGetWebMessageAsString();
        if (string.IsNullOrEmpty(raw))
        {
            return;
        }

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(raw).RootElement;
        }
        catch (JsonException)
        {
            return;
        }

        if (!root.TryGetProperty("type", out var typeProp))
        {
            return;
        }

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
        if (_session is null) return;
        if (!root.TryGetProperty("b64", out var b64Prop)) return;
        string? b64 = b64Prop.GetString();
        if (string.IsNullOrEmpty(b64)) return;

        byte[] bytes = Convert.FromBase64String(b64);
        _ = _session.WriteInputAsync(bytes, CancellationToken.None);
    }

    private void HandleResize(JsonElement root)
    {
        if (_session is null) return;
        if (!root.TryGetProperty("cols", out var c) || !root.TryGetProperty("rows", out var r))
        {
            return;
        }
        short cols = (short)Math.Clamp(c.GetInt32(), 1, short.MaxValue);
        short rows = (short)Math.Clamp(r.GetInt32(), 1, short.MaxValue);
        _ = _session.ResizeAsync(cols, rows, CancellationToken.None);
    }

    /// <summary>
    /// Posts a string envelope to the WebView2 renderer. Must run on the UI thread because the
    /// CoreWebView2 API is single-threaded.
    /// </summary>
    private ValueTask PostToRendererAsync(string envelope)
    {
        if (Dispatcher.CheckAccess())
        {
            try
            {
                WebView.CoreWebView2?.PostWebMessageAsString(envelope);
            }
            catch (InvalidOperationException) { /* webview disposed */ }
            return ValueTask.CompletedTask;
        }

        return new ValueTask(Dispatcher.InvokeAsync(() =>
        {
            try
            {
                WebView.CoreWebView2?.PostWebMessageAsString(envelope);
            }
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
        if (_session is not null)
        {
            try { await _session.DisposeAsync().ConfigureAwait(true); }
            catch { /* swallow */ }
        }
    }
}
