using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Abstractions.Channels;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Layout;
using AgentWorkspace.Abstractions.Pty;
using AgentWorkspace.Abstractions.Sessions;
using AgentWorkspace.Abstractions.Workflows;
using AgentWorkspace.Agents.Claude;
using AgentWorkspace.App.Wpf.Agent;
using AgentWorkspace.App.Wpf.AgentTrace;
using AgentWorkspace.App.Wpf.Approval;
using AgentWorkspace.App.Wpf.CommandPalette;
using AgentWorkspace.Client.Channels;
using AgentWorkspace.Client.Discovery;
using AgentWorkspace.Client.Sessions;
using AgentWorkspace.Core.Templates;
using AgentWorkspace.Core.Transcripts;
using AgentWorkspace.Core.Workflows;
using Microsoft.Web.WebView2.Core;

namespace AgentWorkspace.App.Wpf;

/// <summary>
/// Hosts the WebView2 SPA, owns the multi-pane <see cref="Workspace"/>, and bridges JSON
/// messages between the JS bridge and the .NET runtime. Day 17 onwards the actual pane lifecycle
/// + session store live in <c>awtd.exe</c>; this class talks to the daemon through
/// <see cref="ClientConnection"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class MainWindow : Window
{
    private const string VirtualHost = "agentworkspace.local";

    private Workspace? _workspace;
    private ClientConnection? _connection;
    private NamedPipeControlChannel? _controlChannel;
    private NamedPipeDataChannel? _dataChannel;
    private RemoteSessionStore? _store;
    private string _shell = "cmd.exe";
    private bool _rendererReady;
    private readonly IAgentAdapter _agentAdapter = new ClaudeAdapter();
    private readonly AgentTraceViewModel _agentTrace = new();
    private readonly WorkflowEngine _workflowEngine;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;

        _workflowEngine = new WorkflowEngine(
            workflows: new IWorkflow[]
            {
                new ExplainBuildErrorWorkflow(),
                new FixDotnetTestsWorkflow(),
                new SummarizeSessionWorkflow(),
            },
            agentAdapter: _agentAdapter,
            approvalGateway: new DialogApprovalGateway(),
            policyEngine: BuildPolicyEngine(),
            policyContext: new AgentWorkspace.Abstractions.Policy.PolicyContext(
                WorkspaceRoot: Environment.CurrentDirectory,
                Level: AgentWorkspace.Abstractions.Policy.PolicyLevel.SafeDev,
                AgentName: _agentAdapter.Name));

        Palette.SetCommands(BuildCommands());
        Palette.Dismissed += (_, _) => _ = PostToRendererAsync(Envelope.FocusTerm());
    }

    /// <summary>
    /// Builds the policy engine, merging built-in <c>SafeDev</c>/<c>TrustedLocal</c> rule sets
    /// with optional user additions from <c>~/.agentworkspace/policies.yaml</c>. Parse failures
    /// are surfaced via the status bar and the engine falls back to built-ins only — never
    /// crash on a typo in user YAML.
    /// </summary>
    private AgentWorkspace.Core.Policy.PolicyEngine BuildPolicyEngine()
    {
        try
        {
            var loader   = new AgentWorkspace.Core.Policy.UserPolicyConfigLoader();
            var userCfg  = loader.LoadOrEmpty();
            if (!userCfg.IsEmpty)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[policy] user rules loaded: blacklist={userCfg.Blacklist.Count}, whitelist={userCfg.Whitelist.Count}");
            }
            return AgentWorkspace.Core.Policy.PolicyEngineFactory.WithUserConfig(userCfg);
        }
        catch (AgentWorkspace.Core.Policy.UserPolicyConfigException ex)
        {
            // Don't block app startup on a bad user policies file.
            System.Diagnostics.Debug.WriteLine($"[policy] user policy load failed: {ex.Message}");
            // The status bar isn't initialised yet on ctor — the warning surfaces via Debug only.
            return AgentWorkspace.Core.Policy.PolicyEngineFactory.Default();
        }
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

        // MVP-4 template commands ------------------------------------------------------------
        new CommandEntry(
            "Open Template…",
            "load a YAML workspace template and replace the current layout",
            "open template yaml load workspace",
            ct => OpenTemplateAsync(ct)),

        new CommandEntry(
            "Save Snapshot…",
            "save the current layout and pane commands as a YAML workspace template",
            "save snapshot export yaml template",
            ct => SaveSnapshotAsync(ct)),

        // MVP-5 agent commands ---------------------------------------------------------------
        new CommandEntry(
            "Ask Agent…",
            "open an agent session in a new pane (requires Claude Code CLI on PATH)",
            "ask agent claude ai assistant run",
            ct => AskAgentAsync(ct)),

        // MVP-6 workflow commands ------------------------------------------------------------
        new CommandEntry(
            "Summarize Session…",
            "summarize the most recent agent transcript using Claude",
            "summarize session transcript summary ai",
            ct => SummarizeSessionAsync(ct)),

        // Maintenance — ADR-008 #1 echo-latency manual measurement -----------------
        new CommandEntry(
            "Dump Echo Latency Samples…",
            "ADR-008 #1 — pipe the renderer's buffered keystroke→render samples through awt-perfprobe echo-latency",
            "echo latency perf benchmark adr008 dump samples",
            _ => PostToRendererAsync(Envelope.DumpEchoSamples(clear: true))),
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

    private async ValueTask BroadcastFocusChange(LayoutSnapshot snap)
    {
        await PostToRendererAsync(Envelope.Layout(snap)).ConfigureAwait(true);
        if (_workspace is not null)
        {
            await _workspace.PersistLayoutAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    private async ValueTask OpenTemplateAsync(CancellationToken ct)
    {
        if (_workspace is null || _controlChannel is null || _dataChannel is null) return;

        var path = PickTemplateFile();
        if (path is null) return;

        try
        {
            var template = await new YamlTemplateLoader().LoadAsync(path, ct).ConfigureAwait(true);
            var runner = new TemplateRunner(_controlChannel!, defaultCols: 120, defaultRows: 30);
            var result = await runner.RunAsync(template, ct).ConfigureAwait(true);

            var oldPaneIds = _workspace.Sessions.Keys.ToList();
            await _workspace.DisposeAsync().ConfigureAwait(true);

            var ws = new Workspace(
                sessionFactory: id => new PaneSession(id, PostToRendererAsync, _controlChannel!, _dataChannel!),
                defaultOptionsFactory: () => DefaultStartOptions(_shell),
                initialLayout: result.Layout,
                store: _store,
                sessionId: null);

            foreach (var pane in template.Panes)
            {
                var paneId = result.SlotToPaneId[pane.Id];
                ws.Register(paneId);
                await PostToRendererAsync(Envelope.OpenPane(paneId)).ConfigureAwait(true);
            }
            await PostToRendererAsync(Envelope.Layout(result.Layout)).ConfigureAwait(true);

            foreach (var oldId in oldPaneIds)
                await PostToRendererAsync(Envelope.ClosePane(oldId)).ConfigureAwait(true);

            var reattachTasks = template.Panes
                .Select(p => ws.Sessions[result.SlotToPaneId[p.Id]].ReattachAsync(ct).AsTask())
                .ToArray();
            await Task.WhenAll(reattachTasks).ConfigureAwait(true);

            _workspace = ws;
            StatusText.Text = $"template '{template.Name}' loaded  ·  {result.SlotToPaneId.Count} pane(s)";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"template load failed: {ex.Message}";
        }
    }

    private static string? PickTemplateFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open Workspace Template",
            Filter = "YAML templates (*.yaml;*.yml)|*.yaml;*.yml|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private async ValueTask SaveSnapshotAsync(CancellationToken ct)
    {
        if (_workspace is null) return;

        var suggestedName = $"snapshot-{DateTime.Now:yyyyMMdd-HHmm}";
        var path = PickSaveFile(suggestedName);
        if (path is null) return;

        var templateName = Path.GetFileNameWithoutExtension(path);
        try
        {
            await _workspace.SaveSnapshotAsync(path, templateName, ct).ConfigureAwait(true);
            StatusText.Text = $"snapshot saved → {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"snapshot failed: {ex.Message}";
        }
    }

    private static string? PickSaveFile(string suggestedName)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save Workspace Snapshot",
            Filter = "YAML templates (*.yaml)|*.yaml|All files (*.*)|*.*",
            FileName = suggestedName,
            DefaultExt = ".yaml",
            OverwritePrompt = true,
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

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

        StatusText.Text = "connecting to daemon…";
        _connection = await DaemonDiscovery.ConnectAsync(
            new DaemonDiscoveryOptions(),
            CancellationToken.None).ConfigureAwait(true);

        _controlChannel = new NamedPipeControlChannel(_connection);
        _dataChannel = new NamedPipeDataChannel(_connection);
        _store = new RemoteSessionStore(_connection);
        await _store.InitializeAsync(CancellationToken.None).ConfigureAwait(true);

        // Try to attach the most recent session. If it loads cleanly with at least one pane spec,
        // restore that workspace; otherwise create a fresh single-pane session.
        var (workspace, restoreText) = await TryRestoreSessionAsync(CancellationToken.None).ConfigureAwait(true);
        if (workspace is null)
        {
            workspace = await CreateFreshSessionAsync(CancellationToken.None).ConfigureAwait(true);
            restoreText = $"new session  ·  shell={_shell}";
        }
        _workspace = workspace;

        StatusText.Text = restoreText;
    }

    private async Task<(Workspace? Workspace, string Text)> TryRestoreSessionAsync(CancellationToken ct)
    {
        if (_store is null || _controlChannel is null || _dataChannel is null) return (null, string.Empty);

        try
        {
            var sessions = await _store.ListAsync(ct).ConfigureAwait(true);
            if (sessions.Count == 0) return (null, string.Empty);

            var snap = await _store.AttachAsync(sessions[0].Id, ct).ConfigureAwait(true);
            if (snap is null || snap.Panes.Count == 0) return (null, string.Empty);

            var ws = new Workspace(
                sessionFactory: id => new PaneSession(id, PostToRendererAsync, _controlChannel!, _dataChannel!),
                defaultOptionsFactory: () => DefaultStartOptions(_shell),
                initialLayout: snap.Layout,
                store: _store,
                sessionId: sessions[0].Id);

            // Send the renderer the openPane events for every restored leaf, then the layout
            // so it can position them, before any PTY output starts flowing.
            foreach (var pane in snap.Panes)
            {
                ws.Register(pane.Pane);
                await PostToRendererAsync(Envelope.OpenPane(pane.Pane)).ConfigureAwait(true);
            }
            await PostToRendererAsync(Envelope.Layout(snap.Layout)).ConfigureAwait(true);

            // Restore each pane. If the daemon still holds the pane (LiveState == "Running"),
            // subscribe without re-spawning; otherwise launch a fresh child process.
            var startTasks = snap.Panes.Select(pane =>
            {
                var session = ws.Sessions[pane.Pane];
                return pane.LiveState == "Running"
                    ? session.ReattachAsync(ct).AsTask()
                    : session.StartAsync(ToStartOptions(pane), ct).AsTask();
            }).ToArray();
            await Task.WhenAll(startTasks).ConfigureAwait(true);

            return (ws, $"restored session {sessions[0].Id.ToString()[..6]}…  ·  {snap.Panes.Count} pane(s)");
        }
        catch (Exception ex)
        {
            // Restore is best-effort; if anything is corrupt or the daemon hiccups, fall through
            // to a fresh session and log so the user can see what happened.
            StatusText.Text = $"restore failed: {ex.Message}";
            return (null, string.Empty);
        }
    }

    private async Task<Workspace> CreateFreshSessionAsync(CancellationToken ct)
    {
        var firstPane = PaneId.New();
        var sessionId = _store is null
            ? (SessionId?)null
            : await _store.CreateAsync(
                name: Environment.MachineName,
                workspaceRoot: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ct).ConfigureAwait(true);

        var ws = new Workspace(
            sessionFactory: id => new PaneSession(id, PostToRendererAsync, _controlChannel!, _dataChannel!),
            defaultOptionsFactory: () => DefaultStartOptions(_shell),
            initial: firstPane,
            store: _store,
            sessionId: sessionId);

        var session = ws.Register(firstPane);

        await PostToRendererAsync(Envelope.OpenPane(firstPane)).ConfigureAwait(true);
        await PostToRendererAsync(Envelope.Layout(ws.Layout.Current)).ConfigureAwait(true);

        var options = DefaultStartOptions(_shell);
        await session.StartAsync(options, ct).ConfigureAwait(true);
        await ws.PersistInitialPaneAsync(firstPane, options, ct).ConfigureAwait(true);
        await ws.PersistLayoutAsync(ct).ConfigureAwait(true);

        return ws;
    }

    private static PaneStartOptions ToStartOptions(PaneSpec spec) => new(
        Command: spec.Command,
        Arguments: spec.Arguments,
        WorkingDirectory: spec.WorkingDirectory,
        Environment: spec.Environment,
        InitialColumns: 120,
        InitialRows: 30);

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

            case "echoSamples":
                _ = HandleEchoSamplesAsync(root);
                break;
        }
    }

    private async Task HandleEchoSamplesAsync(JsonElement root)
    {
        if (!root.TryGetProperty("samples", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            StatusText.Text = "echo-latency: renderer returned no samples array.";
            return;
        }

        var samples = new double[arr.GetArrayLength()];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = arr[i].GetDouble();
        }

        try
        {
            string summary = await EchoLatencyDump.RunProbeAsync(samples).ConfigureAwait(true);
            StatusText.Text = summary;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"echo-latency: {ex.Message}";
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
            _ = BroadcastFocusChange(snap);
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
        // 1) Try the standard winget/MSI install location of PowerShell 7. We prefer this
        //    over a PATH lookup because PATH on a Store-only machine typically resolves
        //    pwsh.exe through %LOCALAPPDATA%\Microsoft\WindowsApps\ (an execution-alias
        //    reparse point) whose target lives under C:\Program Files\WindowsApps\ — a
        //    directory the current user has no NTFS read/traverse rights to. Either path
        //    fails CreateProcessW; only an MSI/winget install at this canonical location,
        //    or Windows-PowerShell 5.x in System32, is reliably launchable from ConPTY.
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles))
        {
            string pwshDirect = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
            if (File.Exists(pwshDirect)) return pwshDirect;
        }

        // 2) Fall back to PATH lookup, skipping any WindowsApps-routed result.
        foreach (string candidate in new[] { "pwsh.exe", "powershell.exe", "cmd.exe" })
        {
            string? full = SearchPath(candidate);
            if (full is not null) return full;
        }

        // 3) Last resort: System32\cmd.exe (essentially guaranteed to exist).
        string sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return Path.Combine(sys32, "cmd.exe");
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
                if (!File.Exists(full)) continue;

                // Reject anything routed through \WindowsApps\ — covers both
                //   - %LOCALAPPDATA%\Microsoft\WindowsApps\<alias>.exe (execution-alias stub),
                //   - C:\Program Files\WindowsApps\<package>\<exe>     (the package itself,
                //     which is locked down to TrustedInstaller / SYSTEM by NTFS ACL so the
                //     user process cannot traverse into it for CreateProcessW).
                // The alias stub is technically launchable via the AppExecutionAlias machinery
                // but ConPTY's direct CreateProcessW path bypasses that and hits 0xC0000142.
                if (full.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                return full;
            }
            catch
            {
                // skip invalid PATH entries
            }
        }
        return null;
    }

    private async ValueTask AskAgentAsync(CancellationToken ct)
    {
        if (_workspace is null || _controlChannel is null) return;
        var dialog = new AgentInputDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;
        string prompt = dialog.Prompt;
        string? workingDirectory = dialog.WorkingDirectory;

        PaneId newPane;
        try
        {
            var focused = _workspace.Layout.Current.Focused;
            newPane = await _workspace.OpenSplitAsync(focused, SplitDirection.Vertical, ct).ConfigureAwait(true);
            await PostToRendererAsync(Envelope.OpenPane(newPane)).ConfigureAwait(true);
            await PostToRendererAsync(Envelope.Layout(_workspace.Layout.Current)).ConfigureAwait(true);
        }
        catch (Exception ex) { StatusText.Text = $"agent split failed: {ex.Message}"; return; }

        var paneSession = _workspace.Sessions[newPane];
        var paneOptions = new PaneStartOptions(
            Command: _shell,
            Arguments: Array.Empty<string>(),
            WorkingDirectory: workingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment: null,
            InitialColumns: 120,
            InitialRows: 30);
        try { await paneSession.StartAsync(paneOptions, ct).ConfigureAwait(true); }
        catch (Exception ex) { StatusText.Text = $"agent pane start failed: {ex.Message}"; return; }

        IAgentSession agentSession;
        try
        {
            var options = new AgentSessionOptions(Prompt: prompt, WorkingDirectory: workingDirectory, SaveTranscript: true);
            agentSession = await _agentAdapter.StartSessionAsync(options, ct).ConfigureAwait(true);
        }
        catch (Exception ex) { StatusText.Text = $"agent start failed: {ex.Message}"; return; }

        var agentPaneSession = new AgentPaneSession(paneSession, agentSession);
        try
        {
            await _controlChannel.StartAgentSessionAsync(
                agentPaneSession.PaneId,
                agentPaneSession.AgentSessionId,
                prompt,
                workingDirectory,
                ct).ConfigureAwait(true);
        }
        catch (Exception ex) { StatusText.Text = $"agent registration failed: {ex.Message}"; }

        var sink = TranscriptSink.Open(agentPaneSession.AgentSessionId);
        _agentTrace.Clear();
        StatusText.Text = $"agent session started · {prompt[..Math.Min(40, prompt.Length)]}";
        _ = PumpAgentEventsAsync(agentPaneSession, sink, CancellationToken.None);
    }

    private async Task PumpAgentEventsAsync(AgentPaneSession session, TranscriptSink sink, CancellationToken ct)
    {
        try
        {
            await foreach (var evt in session.Events.WithCancellation(ct))
            {
                _agentTrace.Append(evt);
                await sink.AppendAsync(evt, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() => StatusText.Text = $"agent error: {ex.Message}");
        }
        finally
        {
            await sink.DisposeAsync().ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask SummarizeSessionAsync(CancellationToken ct)
    {
        // Find the most recent transcript file.
        var transcriptDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentWorkspace", "transcripts");

        if (!System.IO.Directory.Exists(transcriptDir))
        {
            StatusText.Text = "No transcripts found.";
            return;
        }

        var latest = System.IO.Directory.EnumerateFiles(transcriptDir, "*.jsonl")
            .Where(f => !f.EndsWith("summaries.jsonl", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => System.IO.File.GetLastWriteTimeUtc(f))
            .FirstOrDefault();

        if (latest is null)
        {
            StatusText.Text = "No transcript files found.";
            return;
        }

        StatusText.Text = $"Summarizing {System.IO.Path.GetFileName(latest)}…";
        var trigger = new ManualTrigger("Summarize Session", latest);
        var result = await _workflowEngine.RunAsync(trigger, ct).ConfigureAwait(true);

        StatusText.Text = result switch
        {
            WorkflowSuccess s => $"Summary saved · {(s.Summary is not null ? s.Summary[..Math.Min(60, s.Summary.Length)] + "…" : "done")}",
            WorkflowCancelled => "Summarize cancelled.",
            WorkflowFailure f => $"Summarize failed: {f.Reason}",
            _ => "done",
        };
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        if (_workspace is not null)
        {
            try { await _workspace.PersistLayoutAsync(CancellationToken.None).ConfigureAwait(true); }
            catch { /* persistence is best-effort */ }
            try { await _workspace.DisposeAsync().ConfigureAwait(true); }
            catch { /* swallow */ }
        }
        if (_controlChannel is not null)
        {
            try { await _controlChannel.DisposeAsync().ConfigureAwait(true); }
            catch { /* swallow */ }
        }
        if (_dataChannel is not null)
        {
            try { await _dataChannel.DisposeAsync().ConfigureAwait(true); }
            catch { /* swallow */ }
        }
        // The connection lives across the client process; we close it last so any pending
        // teardown RPC issued by the channels has a chance to flush.
        if (_connection is not null)
        {
            try { await _connection.DisposeAsync().ConfigureAwait(true); }
            catch { /* swallow */ }
        }
    }
}
