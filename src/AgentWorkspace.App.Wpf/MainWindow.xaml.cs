using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
using AgentWorkspace.Abstractions.Mesh;
using AgentWorkspace.Abstractions.Pty;
using AgentWorkspace.Abstractions.Sessions;
using AgentWorkspace.Abstractions.Workflows;
using AgentWorkspace.Agents.Claude;
using AgentWorkspace.Agents.Codex;
using AgentWorkspace.Agents.Gemini;
using AgentWorkspace.Agents.Ollama;
using AgentWorkspace.App.Wpf.AgentTrace;
using AgentWorkspace.App.Wpf.Approval;
using AgentWorkspace.App.Wpf.CommandPalette;
using AgentWorkspace.App.Wpf.PaneMessage;
using AgentWorkspace.Client.Channels;
using AgentWorkspace.Client.Discovery;
using AgentWorkspace.Client.Sessions;
using AgentWorkspace.App.Wpf.Mesh;
using AgentWorkspace.Core.Mesh;
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
    private readonly IAgentAdapter _agentAdapter  = new ClaudeAdapter();
    private readonly IAgentAdapter _ollamaAdapter = new OllamaAdapter();
    private readonly IAgentAdapter _codexAdapter  = new CodexAdapter();
    private readonly IAgentAdapter _geminiAdapter = new GeminiAdapter();
    private readonly AgentTraceViewModel _agentTrace = new();
    private readonly WorkflowEngine _workflowEngine;

    // ── AgentMesh (P3) ────────────────────────────────────────────────────────────
    private readonly InMemoryMessageBus _meshBus = new();
    private readonly AgentMesh _mesh;
    /// <summary>Root pane session registered with the mesh on first Claude pane open.</summary>
    private PaneAgentSession? _rootMeshSession;
    private AgentSessionId _rootMeshSessionId;
    /// <summary>Subscription handle for <c>agent.*.merged</c> events; disposed on window close.</summary>
    private IAsyncDisposable? _mergeSubscription;
    /// <summary>Live list of sub-agent cards bound to <see cref="SubAgentCardList"/>.</summary>
    private readonly ObservableCollection<SubAgentSessionViewModel> _subAgentSessions = new();
    /// <summary>Maps child <see cref="AgentSessionId"/> → its card VM for O(1) lookup in merge handler.</summary>
    private readonly ConcurrentDictionary<AgentSessionId, SubAgentSessionViewModel> _subAgentSessionsMap = new();
    /// <summary>Per-child bus subscription handles; disposed on window close.</summary>
    private readonly ConcurrentDictionary<AgentSessionId, IAsyncDisposable> _subAgentSubscriptions = new();
    /// <summary>Per-pane bus subscription handles for <c>pane.{id}.send</c> routing; disposed on pane close.</summary>
    private readonly ConcurrentDictionary<PaneId, IAsyncDisposable> _paneSubscriptions = new();

    // ── External Task tracking (Y-A) ──────────────────────────────────────────────
    /// <summary>
    /// Watches Claude's transcript JSONL files and surfaces Task tool invocations
    /// from the user's interactive Claude CLI as "external" sub-agent cards.
    /// </summary>
    private ClaudeTranscriptWatcher? _claudeTranscriptWatcher;

    /// <summary>
    /// Pure-logic bookkeeper for the external Task lifecycle and auto-pane budget.
    /// MainWindow handles UI work (VM construction, pane opening, clipboard); the
    /// coordinator owns the dictionaries, counters, and the on/off toggle.
    /// </summary>
    private readonly ExternalTaskCoordinator _externalTasks = new();

    /// <summary>
    /// Periodic sweep that reclaims auto-pane budget slots whose corresponding Task
    /// completion never fired (Claude crash, network drop, transcript corruption).
    /// Without this, abandoned Tasks would permanently inflate the in-flight counter
    /// and eventually block all auto-pane creation. Wakes once a minute.
    /// </summary>
    private System.Windows.Threading.DispatcherTimer? _externalTaskPruneTimer;
    /// <summary>How long an auto-pane tag may live before being considered abandoned.</summary>
    private static readonly TimeSpan ExternalTaskPruneAge = TimeSpan.FromMinutes(30);

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
        SizeChanged += OnWindowSizeChanged;

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
                AgentName: _agentAdapter.Name),
            sinkFactory: (id, provider, model, parentId) =>
                TranscriptSink.Open(id, provider: provider, model: model, parentSessionId: parentId));

        _mesh = new AgentMesh(_meshBus);

        PalettePopup.PlacementTarget = this;
        Palette.SetCommands(BuildCommands());
        Palette.Dismissed += OnPaletteDismissed;

        // The trace panel binds to _agentTrace; the column stays at width 0 until
        // ShowAgentTrace() expands it on first agent session start.
        TracePanel.DataContext = _agentTrace;
        SubAgentCardList.ItemsSource = _subAgentSessions;
    }

    private void ShowAgentTrace()
    {
        TraceCol.Width = new GridLength(420);
    }

    /// <summary>
    /// Reveals the sub-agent section (Row 1 of the trace column) by giving it an equal star share.
    /// Safe to call multiple times — subsequent calls are no-ops because the height is already nonzero.
    /// </summary>
    private void ShowSubAgentSection()
    {
        if (SubAgentRow.Height.IsAbsolute && SubAgentRow.Height.Value == 0)
            SubAgentRow.Height = new GridLength(1, GridUnitType.Star);
    }

    private void OnCloseTraceClicked(object sender, RoutedEventArgs e)
    {
        TraceCol.Width = new GridLength(0);
        _agentTrace.Clear();
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Keep the Palette sized to the Window so its dim backdrop covers everything.
        Palette.Width  = e.NewSize.Width;
        Palette.Height = e.NewSize.Height;
    }

    private void OnPaletteDismissed(object? sender, EventArgs e)
    {
        PalettePopup.IsOpen = false;
        _ = PostToRendererAsync(Envelope.FocusTerm());
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
    /// Localised palette commands (Korean primary). Search keywords keep both Korean and
    /// English tokens so users can find a command by typing in either language.
    /// </summary>
    private IReadOnlyList<CommandEntry> BuildCommands() => new[]
    {
        // MVP-1 — terminal control ----------------------------------------------------------
        new CommandEntry(
            "쉘 재시작",
            "포커스된 패널의 child 프로세스를 종료하고 새 쉘을 시작합니다",
            "쉘 재시작 다시 시작 restart shell relaunch",
            ct => ActiveSession()?.RestartAsync(ct) ?? ValueTask.CompletedTask),

        new CommandEntry(
            "Ctrl+C 전송",
            "포커스된 패널의 foreground 프로그램을 중단합니다",
            "ctrl c 전송 인터럽트 중단 취소 send interrupt sigint cancel",
            ct => ActiveSession()?.SendInterruptAsync(ct) ?? ValueTask.CompletedTask),

        new CommandEntry(
            "터미널 화면 지우기",
            "스크롤백은 유지 — 포커스된 패널의 화면만 지웁니다",
            "터미널 화면 지우기 클리어 clear terminal screen reset",
            _ =>
            {
                var s = ActiveSession();
                return s is null ? ValueTask.CompletedTask : PostToRendererAsync(Envelope.Clear(s.Id));
            }),

        new CommandEntry("글자 크기 키우기", "+1 px",
            "글자 크기 폰트 키우기 확대 font size increase larger zoom in",
            _ => PostToRendererAsync(Envelope.FontSizeDelta(+1))),

        new CommandEntry("글자 크기 줄이기", "-1 px",
            "글자 크기 폰트 줄이기 축소 font size decrease smaller zoom out",
            _ => PostToRendererAsync(Envelope.FontSizeDelta(-1))),

        // MVP-2 — layout --------------------------------------------------------------------
        new CommandEntry(
            "오른쪽으로 분할",
            "수평 분할 — 포커스된 패널 오른쪽에 새 패널을 엽니다",
            "분할 오른쪽 수평 split right horizontal new pane",
            ct => OpenSplitAsync(SplitDirection.Horizontal, ct)),

        new CommandEntry(
            "아래쪽으로 분할",
            "수직 분할 — 포커스된 패널 아래에 새 패널을 엽니다",
            "분할 아래 수직 split down vertical new pane",
            ct => OpenSplitAsync(SplitDirection.Vertical, ct)),

        new CommandEntry(
            "패널 닫기",
            "포커스된 패널을 닫습니다 (마지막 한 개일 때는 거부)",
            "패널 닫기 종료 제거 close pane kill remove",
            ct => CloseFocusedAsync(ct)),

        new CommandEntry(
            "다음 패널로 포커스",
            "다음 패널로 포커스 이동 (왼쪽→오른쪽)",
            "다음 패널 포커스 이동 focus next pane cycle",
            _ => BroadcastFocusChange(_workspace!.Layout.FocusNext())),

        new CommandEntry(
            "이전 패널로 포커스",
            "이전 패널로 포커스 이동",
            "이전 패널 포커스 이동 focus previous pane cycle back",
            _ => BroadcastFocusChange(_workspace!.Layout.FocusPrevious())),

        // MVP-4 — templates -----------------------------------------------------------------
        new CommandEntry(
            "템플릿 열기…",
            "YAML 워크스페이스 템플릿을 불러와 현재 레이아웃을 교체합니다",
            "템플릿 열기 불러오기 open template yaml load workspace",
            ct => OpenTemplateAsync(ct)),

        new CommandEntry(
            "스냅샷 저장…",
            "현재 레이아웃과 패널 명령을 YAML 워크스페이스 템플릿으로 저장합니다",
            "스냅샷 저장 내보내기 save snapshot export yaml template",
            ct => SaveSnapshotAsync(ct)),

        // MVP-5 — agent ---------------------------------------------------------------------
        new CommandEntry(
            "Claude 패널 열기",
            "현재 패널을 세로로 분할하고 새 패널에서 claude REPL을 시작합니다 (PATH에 Claude Code CLI 필요)",
            "claude 패널 열기 에이전트 ai ask repl interactive assistant 클로드",
            ct => AskAgentAsync(ct)),

        new CommandEntry(
            "Ollama 패널 열기",
            "현재 패널을 세로로 분할하고 새 패널에서 ollama run llama3를 시작합니다 (로컬 Ollama 설치 필요)",
            "ollama 패널 열기 에이전트 로컬 ai llm llama local model repl interactive",
            ct => AskOllamaAsync(ct)),

        new CommandEntry(
            "Claude 하위 에이전트 실행…",
            "AgentMesh를 통해 Claude 하위 에이전트를 스폰합니다. 결과는 에이전트 트레이스 패널에 표시됩니다 (Claude 패널 먼저 열기 필요).",
            "claude 하위 에이전트 실행 spawn subagent mesh child agent 스폰 클로드",
            ct => SpawnSubAgentAsync(_agentAdapter, ct)),

        new CommandEntry(
            "Codex 하위 에이전트 실행…",
            "OpenAI Codex CLI(`codex exec`)로 하위 에이전트를 스폰합니다. PATH에 codex 필요.",
            "codex 하위 에이전트 실행 spawn subagent mesh child openai gpt 스폰",
            ct => SpawnSubAgentAsync(_codexAdapter, ct)),

        new CommandEntry(
            "Gemini 하위 에이전트 실행…",
            "Google Gemini CLI(`gemini -p`)로 하위 에이전트를 스폰합니다. PATH에 gemini와 GEMINI_API_KEY 필요.",
            "gemini 하위 에이전트 실행 spawn subagent mesh child google 제미니 스폰",
            ct => SpawnSubAgentAsync(_geminiAdapter, ct)),

        // MVP-6 — workflow ------------------------------------------------------------------
        new CommandEntry(
            "세션 요약…",
            "Claude로 가장 최근 에이전트 transcript를 요약합니다",
            "세션 요약 transcript 트랜스크립트 summarize session summary ai",
            ct => SummarizeSessionAsync(ct)),

        // External task auto-pane (Z) ---------------------------------------------------------
        new CommandEntry(
            "외부 Task 자동 패널 토글",
            "Claude CLI가 내부 Task tool로 sub-agent를 호출할 때 자동으로 새 Claude 패널 + prompt 클립보드 복사 (기본 OFF, 동시 ≤ 3)",
            "외부 task 자동 패널 토글 auto pane external auto-spawn",
            _ => ToggleAutoPaneOnExternalTaskAsync()),

        // Inter-pane messaging --------------------------------------------------------------
        new CommandEntry(
            "패널로 텍스트 전송…",
            "대화 상자에서 대상 패널을 선택하고 텍스트를 입력해 PTY 표준 입력으로 전송합니다",
            "패널 전송 텍스트 send pane text input write stdin 입력",
            ct => SendToPaneAsync(ct)),

        // Maintenance — ADR-008 #1 echo-latency manual measurement --------------------------
        new CommandEntry(
            "Echo Latency 샘플 덤프…",
            "ADR-008 #1 — 렌더러의 keystroke→render 샘플을 awt-perfprobe echo-latency로 전달",
            "echo latency 지연 성능 perf benchmark adr008 dump samples",
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
            // Dispose the bus subscription before closing the pane to prevent in-flight
            // pane.{id}.send messages from calling WriteInputAsync on a torn-down IControlChannel.
            await UnsubscribePaneAsync(focused).ConfigureAwait(true);
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
            foreach (var oldId in oldPaneIds)
                await UnsubscribePaneAsync(oldId).ConfigureAwait(true);
            await _workspace.DisposeAsync().ConfigureAwait(true);

            var ws = new Workspace(
                sessionFactory: id =>
                {
                    var s = new PaneSession(id, PostToRendererAsync, _controlChannel!, _dataChannel!);
                    WirePaneSendSubscription(id, s);
                    return s;
                },
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
        if (PalettePopup.IsOpen)
        {
            // Hide() fires Dismissed → OnPaletteDismissed closes the Popup.
            Palette.Hide();
        }
        else
        {
            PalettePopup.IsOpen = true;
            Palette.Show();
        }
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
                sessionFactory: id =>
                {
                    var s = new PaneSession(id, PostToRendererAsync, _controlChannel!, _dataChannel!);
                    WirePaneSendSubscription(id, s);
                    return s;
                },
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
            sessionFactory: id =>
            {
                var s = new PaneSession(id, PostToRendererAsync, _controlChannel!, _dataChannel!);
                WirePaneSendSubscription(id, s);
                return s;
            },
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

            case "paneMessage":
                _ = HandlePaneMessageAsync(root);
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

    // ── Inter-pane messaging ──────────────────────────────────────────────────────

    /// <summary>
    /// Wires a <c>pane.{paneId}.</c> subscription on the mesh bus so that any
    /// <c>Kind = "send"</c> message with a <see cref="string"/> payload is written
    /// verbatim to the pane's PTY stdin. Called immediately after creating each
    /// <see cref="PaneSession"/> inside the <c>sessionFactory</c> lambda so every
    /// pane is automatically wired regardless of the creation path.
    /// </summary>
    private void WirePaneSendSubscription(PaneId paneId, PaneSession session)
    {
        var sub = _meshBus.Subscribe($"pane.{paneId}.", (msg, ct) =>
        {
            if (msg.Kind == "send" && msg.Payload is string text)
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
                _ = session.WriteInputAsync(bytes, ct);
            }
            return ValueTask.CompletedTask;
        });
        _paneSubscriptions[paneId] = sub;
    }

    /// <summary>
    /// Removes and disposes the bus subscription for <paramref name="paneId"/>.
    /// Must be called <em>before</em> <see cref="Workspace.CloseAsync"/> to avoid
    /// in-flight messages reaching a torn-down <see cref="IControlChannel"/>.
    /// </summary>
    private async ValueTask UnsubscribePaneAsync(PaneId paneId)
    {
        if (_paneSubscriptions.TryRemove(paneId, out var sub))
            await sub.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Handles a <c>paneMessage</c> envelope from the JS bridge.
    /// Expected shape: <c>{ "type": "paneMessage", "targetPaneId": "&lt;guid&gt;", "text": "…" }</c>.
    /// Publishes to <c>pane.{targetPaneId}.send</c> on the mesh bus so the target
    /// pane's PTY receives the text.
    /// </summary>
    private async Task HandlePaneMessageAsync(JsonElement root)
    {
        if (!root.TryGetProperty("targetPaneId", out var idProp)) return;
        string? idStr = idProp.GetString();
        if (string.IsNullOrEmpty(idStr)) return;

        PaneId targetPaneId;
        try { targetPaneId = PaneId.Parse(idStr); }
        catch (FormatException) { return; }

        if (!root.TryGetProperty("text", out var textProp)) return;
        string? text = textProp.GetString();
        if (string.IsNullOrEmpty(text)) return;

        await _meshBus.PublishAsync(new MeshMessage(
            Topic: $"pane.{targetPaneId}.send",
            Timestamp: DateTimeOffset.UtcNow,
            Kind: "send",
            Payload: text
        ), CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>
    /// Opens the <see cref="SendToPaneDialog"/>, lets the user pick a pane and enter text,
    /// then publishes to <c>pane.{id}.send</c> on the mesh bus so the target PTY receives it.
    /// </summary>
    private async ValueTask SendToPaneAsync(CancellationToken ct)
    {
        if (_workspace is null || _workspace.Sessions.Count == 0)
        {
            StatusText.Text = "열려 있는 패널이 없습니다.";
            return;
        }

        var focused = _workspace.Layout.Current.Focused;
        var choices = _workspace.Sessions.Keys
            .Select((id, i) => new PaneChoiceItem(i + 1, id, id == focused))
            .ToList();

        var dlg = new SendToPaneDialog(choices) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        string text = dlg.AppendNewline ? dlg.Text + "\n" : dlg.Text;

        await _meshBus.PublishAsync(new MeshMessage(
            Topic: $"pane.{dlg.SelectedPaneId}.send",
            Timestamp: DateTimeOffset.UtcNow,
            Kind: "send",
            Payload: text
        ), ct).ConfigureAwait(true);

        StatusText.Text = $"→ {dlg.SelectedLabel}  ·  {dlg.Text.Length}자 전송";
    }

    private void OnSendToPaneClicked(object sender, RoutedEventArgs e)
    {
        _ = SendToPaneAsync(CancellationToken.None);
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

    /// <summary>
    /// Splits the focused pane vertically, starts a shell in the new pane, and sends
    /// <c>claude\r</c> to drop the user into an interactive Claude REPL. The user types
    /// directly in the terminal — no dialog, no stream-JSON pipe.
    /// </summary>
    private async ValueTask AskAgentAsync(CancellationToken ct)
    {
        if (_workspace is null) return;
        var focused = _workspace.Layout.Current.Focused;
        try
        {
            var newPane = await _workspace.OpenSplitAsync(focused, SplitDirection.Vertical, ct).ConfigureAwait(true);
            var session = _workspace.Sessions[newPane];

            await PostToRendererAsync(Envelope.OpenPane(newPane)).ConfigureAwait(true);
            await PostToRendererAsync(Envelope.Layout(_workspace.Layout.Current)).ConfigureAwait(true);
            await PostToRendererAsync(Envelope.PaneBadge(newPane, "claude")).ConfigureAwait(true);

            // Brief pause so the shell prompt appears before sending the command.
            await Task.Delay(200, ct).ConfigureAwait(true);
            await session.WriteInputAsync(System.Text.Encoding.UTF8.GetBytes("claude\r"), ct).ConfigureAwait(true);
            UpdateProviderBadge("Claude Code");

            // Register this pane as the root mesh session on first open so sub-agents can
            // be spawned from it. Subsequent calls to AskAgentAsync reuse the same root.
            if (_rootMeshSession is null)
            {
                _rootMeshSession = new PaneAgentSession(_agentTrace);
                _rootMeshSessionId = _rootMeshSession.Id;
                _mesh.RegisterRoot(_rootMeshSessionId, _rootMeshSession);
                SubscribeToMergeEvents();
                ShowAgentTrace();
            }

            // Start watching Claude's transcript JSONL files so internal Task tool invocations
            // (which run inside the user's interactive claude CLI, NOT through our mesh)
            // surface as "external" sub-agent cards. Idempotent — second AskAgentAsync is a no-op.
            await StartClaudeTranscriptWatcherAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Claude 패널 열기 실패: {ex.Message}";
        }
    }

    // ── External Task watcher (Y-A) ──────────────────────────────────────────────

    /// <summary>
    /// Lazily starts <see cref="ClaudeTranscriptWatcher"/> on the first Claude pane open.
    /// Subscribes to TaskStarted/Completed events and routes them to external card VMs.
    /// </summary>
    private async Task StartClaudeTranscriptWatcherAsync()
    {
        if (_claudeTranscriptWatcher is not null) return;

        var watcher = new ClaudeTranscriptWatcher();
        watcher.TaskStarted   += OnExternalTaskStarted;
        watcher.TaskCompleted += OnExternalTaskCompleted;
        await watcher.StartAsync().ConfigureAwait(true);
        _claudeTranscriptWatcher = watcher;

        // Start the periodic auto-pane stale-tag sweep alongside the watcher. Both share
        // the same lifetime: born when the first Claude pane opens, killed on window close.
        // DispatcherTimer fires on the UI thread so the coordinator's mutators can be
        // called without additional marshalling.
        if (_externalTaskPruneTimer is null)
        {
            _externalTaskPruneTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(1),
            };
            _externalTaskPruneTimer.Tick += OnExternalTaskPruneTick;
            _externalTaskPruneTimer.Start();
        }
    }

    private void OnExternalTaskPruneTick(object? sender, EventArgs e)
    {
        try
        {
            int reclaimed = _externalTasks.PruneStaleAutoPaneTags(ExternalTaskPruneAge);
            if (reclaimed > 0)
            {
                System.Diagnostics.Debug.WriteLine($"[external-task] pruned {reclaimed} stale auto-pane tag(s)");
            }
        }
        catch (Exception ex)
        {
            // Pruning is best-effort — never crash the UI thread on a sweep failure.
            System.Diagnostics.Debug.WriteLine($"[external-task] prune tick failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Claude's interactive CLI invoked the <c>Task</c> tool — we observed the line in
    /// the session JSONL. Create a read-only "external" card so the user can see the
    /// invocation alongside their own mesh-spawned sub-agents.
    /// </summary>
    private void OnExternalTaskStarted(object? sender, TaskInvocation task)
    {
        // STEP 1 (watcher thread, synchronous): reserve the map slot BEFORE dispatching
        // to the UI thread. Closes the race where TaskCompleted arrives for the same
        // tool_use_id between TaskStarted and the deferred UI work; also dedups duplicate
        // emissions from a session-file rewrite. Coordinator returns false if already seen.
        if (!_externalTasks.TryReserveStartSlot(task.ToolUseId)) return;

        // STEP 2 (UI thread, deferred): build the VM and replace the placeholder.
        Dispatcher.InvokeAsync(() =>
        {
            try
            {
                // Synthetic AgentSessionId — the external task isn't in our AgentMesh, so
                // any id will do for the VM. Used only as a card identity, not as a mesh key.
                var fakeSessionId = AgentSessionId.New();
                var subVm = new SubAgentSessionViewModel(
                    childId:              fakeSessionId,
                    originalPrompt:       task.Prompt,
                    adapter:              _agentAdapter,    // external Tasks always come from Claude
                    onFocus:              OnSubAgentFocus,
                    onPromoteToPane:      OnSubAgentPromoteToPane,
                    onSpawnChild:         OnSubAgentSpawnChild, // CanExecute=false for external
                    isExternal:           true,
                    externalSubAgentType: task.SubAgentType);

                _externalTasks.RegisterStartedVm(task.ToolUseId, subVm);
                _subAgentSessions.Add(subVm);
                ShowSubAgentSection();
                ShowAgentTrace();

                // Surface a one-line "in-progress" message in the card body so the user
                // sees something even if the result line never arrives (e.g. Claude crashed).
                subVm.Trace.Append(new AgentMessageEvent(
                    "user",
                    $"🔗 외부 Task 시작: {task.SubAgentType}\n{task.Prompt}"));

                StatusText.Text = $"🔗 외부 Task 감지 ({task.SubAgentType})";

                // Z — auto-pane on external Task (opt-in via palette toggle).
                // Coordinator atomically checks toggle + budget + dedup-tag-add and either
                // returns true (we may proceed) or false (skip). Tagging the id here ensures
                // the corresponding completion releases the slot regardless of toggle state.
                if (_externalTasks.TryClaimAutoPaneSlot(task.ToolUseId))
                {
                    _ = OpenAutoPaneForExternalTaskAsync(task);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[external-task] start handler failed: {ex.Message}");
                _externalTasks.RollbackReservation(task.ToolUseId);
            }
        });
    }

    // ── Z: auto-pane on external Task ─────────────────────────────────────────────

    /// <summary>
    /// Toggles auto-pane creation on external Task observations. Surfaces the new state
    /// in the status bar. Coordinator owns the actual flag.
    /// </summary>
    private ValueTask ToggleAutoPaneOnExternalTaskAsync()
    {
        bool nowOn = _externalTasks.ToggleAutoPane();
        StatusText.Text = nowOn
            ? $"🔗 외부 Task 자동 패널: ON  (동시 패널 ≤ {ExternalTaskCoordinator.MaxAutoPanesInFlight})"
            : "🔗 외부 Task 자동 패널: OFF  (카드만 표시)";
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Opens a new Claude pane and copies the external Task's prompt to clipboard so the
    /// user can paste with Ctrl+V. Mirrors <see cref="PromoteSubAgentToPaneAsync"/> but
    /// triggered automatically rather than by a card button click.
    /// </summary>
    private async Task OpenAutoPaneForExternalTaskAsync(TaskInvocation task)
    {
        try
        {
            if (_workspace is null) return;

            // Clipboard FIRST so a slow split below doesn't leave the user without a way
            // to populate the new REPL.
            try { System.Windows.Clipboard.SetText(task.Prompt); }
            catch (Exception clipEx)
            {
                System.Diagnostics.Debug.WriteLine($"[auto-pane] clipboard set failed: {clipEx.Message}");
            }

            var focused = _workspace.Layout.Current.Focused;
            var newPane = await _workspace.OpenSplitAsync(focused, SplitDirection.Vertical, CancellationToken.None).ConfigureAwait(true);
            var session = _workspace.Sessions[newPane];

            await PostToRendererAsync(Envelope.OpenPane(newPane)).ConfigureAwait(true);
            await PostToRendererAsync(Envelope.Layout(_workspace.Layout.Current)).ConfigureAwait(true);
            await PostToRendererAsync(Envelope.PaneBadge(newPane, $"claude · {task.SubAgentType}")).ConfigureAwait(true);

            await Task.Delay(200).ConfigureAwait(true);
            await session.WriteInputAsync(System.Text.Encoding.UTF8.GetBytes("claude\r"), CancellationToken.None).ConfigureAwait(true);
            UpdateProviderBadge("Claude Code");

            StatusText.Text = $"🔗 자동 패널 열림 ({task.SubAgentType}) · prompt 클립보드 복사 · Ctrl+V";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[auto-pane] open failed: {ex.Message}");
            StatusText.Text = $"자동 패널 열기 실패: {ex.Message}";
        }
        // We deliberately do NOT decrement _autoPanesInFlight here — it's released on the
        // corresponding Task completion (see OnExternalTaskCompleted) so a long-running
        // sub-agent counts against the limit for its full lifetime.
    }

    /// <summary>
    /// Claude's Task tool returned a result — match it to a previously-created external card
    /// via <c>tool_use_id</c> and mark the card as merged with the redacted output.
    /// </summary>
    private void OnExternalTaskCompleted(object? sender, TaskResult result)
    {
        // The starter reserves the map slot synchronously, so a "not found" result means
        // we never saw the start — drop the orphan completion. A null VM means the slot
        // is reserved but the VM hasn't been built yet; re-queue on UI thread so we run
        // AFTER the start continuation drains.
        var (found, subVm) = _externalTasks.TryFindForCompletion(result.ToolUseId);
        if (!found) return;

        Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                // Defensive: if the placeholder hasn't been replaced yet, give the start
                // continuation one chance to land. 100 ms mirrors the merge-handler retry.
                if (subVm is null)
                {
                    await Task.Delay(100).ConfigureAwait(true);
                    (_, subVm) = _externalTasks.TryFindForCompletion(result.ToolUseId);
                    if (subVm is null)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[external-task] completion arrived but VM never materialised (toolUseId={result.ToolUseId})");
                        _externalTasks.ReleaseCompletion(result.ToolUseId);
                        return;
                    }
                }

                // Append the full tool_result text into the card body so the user can read it.
                subVm.Trace.Append(new AgentMessageEvent("assistant", result.Output));

                subVm.Status        = result.IsError ? SubAgentStatus.Error : SubAgentStatus.Merged;
                subVm.ExitCode      = result.IsError ? 1 : 0;
                subVm.MergedSummary = result.Output;
                subVm.IsExpanded    = false;
                subVm.IsFocused     = false;

                StatusText.Text = result.IsError
                    ? $"🔗 외부 Task 실패 ({subVm.ExternalSubAgentType ?? "?"})"
                    : $"🔗 외부 Task 완료 ({subVm.ExternalSubAgentType ?? "?"})";

                // Coordinator handles both: removing the map entry and (if tagged) releasing
                // the auto-pane budget slot. Toggle state at completion time is irrelevant —
                // gate is on the start-time tag (HIGH bug fix from prior review).
                _externalTasks.ReleaseCompletion(result.ToolUseId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[external-task] complete handler failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Splits the focused pane vertically, starts a shell in the new pane, and sends
    /// <c>ollama run llama3\r</c> to drop the user into an interactive Ollama REPL.
    /// Requires a local Ollama installation with the llama3 model pulled.
    /// </summary>
    private async ValueTask AskOllamaAsync(CancellationToken ct)
    {
        if (_workspace is null) return;
        var focused = _workspace.Layout.Current.Focused;
        try
        {
            var newPane = await _workspace.OpenSplitAsync(focused, SplitDirection.Vertical, ct).ConfigureAwait(true);
            var session = _workspace.Sessions[newPane];

            await PostToRendererAsync(Envelope.OpenPane(newPane)).ConfigureAwait(true);
            await PostToRendererAsync(Envelope.Layout(_workspace.Layout.Current)).ConfigureAwait(true);
            await PostToRendererAsync(Envelope.PaneBadge(newPane, "ollama")).ConfigureAwait(true);

            // Brief pause so the shell prompt appears before sending the command.
            await Task.Delay(200, ct).ConfigureAwait(true);
            await session.WriteInputAsync(System.Text.Encoding.UTF8.GetBytes("ollama run llama3\r"), ct).ConfigureAwait(true);
            UpdateProviderBadge("Ollama · llama3");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Ollama 패널 열기 실패: {ex.Message}";
        }
    }

    /// <summary>
    /// Updates the global provider badge in the top chrome strip.
    /// Per-pane badges are set separately via <see cref="Envelope.PaneBadge"/>.
    /// </summary>
    private void UpdateProviderBadge(string providerLabel) =>
        ProviderBadgeText.Text = $"  ⬡ {providerLabel}";

    // ── AgentMesh wiring (P3) ────────────────────────────────────────────────────

    /// <summary>
    /// Subscribes to all <c>agent.*.merged</c> events on the mesh bus and routes them
    /// to the agent trace panel. Called once when the first Claude pane is opened.
    /// </summary>
    private void SubscribeToMergeEvents()
    {
        _mergeSubscription = _meshBus.Subscribe("agent.", async (msg, ct) =>
        {
            if (msg.Kind != "merged" || msg.Payload is not MergedPayload merged) return;

            // Defensive: there is a microsecond TOCTOU window between SpawnSubAgentInternalAsync
            // returning from `await _mesh.SpawnAsync(...)` and the line that inserts the new
            // VM into _subAgentSessionsMap. For an extremely fast-completing child the merge
            // event can race ahead of that insert. A single short retry covers that window
            // without burdening the steady-state path.
            if (!_subAgentSessionsMap.TryGetValue(merged.ChildId, out var subVm))
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
                _subAgentSessionsMap.TryGetValue(merged.ChildId, out subVm);
            }

            // Update the sub-agent card VM — property setters marshal to UI thread internally.
            if (subVm is not null)
            {
                subVm.ExitCode      = merged.ExitCode;
                subVm.Status        = SubAgentStatus.Merged;
                subVm.MergedSummary = merged.RedactedSummary ?? string.Empty;
                subVm.IsExpanded    = false;
                subVm.IsFocused     = false;
            }

            // Dispose & remove the per-child bus subscription now that the child has merged.
            // Without this, _subAgentSubscriptions grows unbounded over a long session.
            // The subscription contract (IAsyncDisposable) is idempotent so a missed lookup
            // is harmless.
            if (_subAgentSubscriptions.TryRemove(merged.ChildId, out var childSub))
            {
                try { await childSub.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[mesh] child subscription dispose failed: {ex.Message}");
                }
            }

            // AgentTraceViewModel.Append is thread-safe (marshals to UI dispatcher internally).
            _agentTrace.Append(new AgentMessageEvent(
                "assistant",
                $"[하위 에이전트 {merged.ChildId.ToString()[..8]}… 완료 · 종료코드 {merged.ExitCode}]\n{merged.RedactedSummary}"));

            // ShowAgentTrace and StatusText require the UI thread.
            await Dispatcher.InvokeAsync(() =>
            {
                ShowAgentTrace();
                StatusText.Text = $"하위 에이전트 완료 · 종료코드 {merged.ExitCode}";
            });
        });
    }

    /// <summary>
    /// Palette entry: spawns a sub-agent under the root mesh session with a default prompt
    /// using the requested vendor adapter. Card-driven grandchild spawns go through
    /// <see cref="SpawnSubAgentInternalAsync"/> directly with the parent card's adapter.
    /// </summary>
    private async ValueTask SpawnSubAgentAsync(IAgentAdapter adapter, CancellationToken ct)
    {
        if (_rootMeshSession is null)
        {
            StatusText.Text = "Claude 패널을 먼저 열어주세요 (단축키: Ctrl+P → 'Claude 패널 열기').";
            return;
        }

        const string DefaultPrompt = "현재 작업 디렉토리의 파일 구조를 간략히 요약해주세요.";
        await SpawnSubAgentInternalAsync(_rootMeshSessionId, adapter, DefaultPrompt, ct).ConfigureAwait(true);
    }

    /// <summary>
    /// Shared spawn implementation used by both the palette commands (with the root
    /// mesh session as parent) and per-card grandchild spawns (with a sub-agent as parent).
    /// Builds the live card VM, wires per-child bus subscription, and enforces
    /// <see cref="SpawnPolicy"/> hard limits (policy violations surface in the status bar).
    /// </summary>
    /// <param name="parentId">Parent mesh session id (root or another sub-agent).</param>
    /// <param name="adapter">Vendor adapter (Claude/Codex/Gemini/etc.) to spawn the child with.</param>
    /// <param name="prompt">Initial prompt sent to the child.</param>
    /// <param name="ct">Cancellation token.</param>
    private async ValueTask SpawnSubAgentInternalAsync(
        AgentSessionId parentId,
        IAgentAdapter adapter,
        string prompt,
        CancellationToken ct)
    {
        try
        {
            StatusText.Text = $"{adapter.Name} 하위 에이전트 스폰 중…";
            var options = new AgentSessionOptions(
                Prompt: prompt,
                WorkingDirectory: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                SaveTranscript: false);

            var childId = await _mesh.SpawnAsync(
                parentId, adapter, options, ct).ConfigureAwait(true);

            // Build the card VM and register it before subscribing to the bus so no events
            // are dropped between spawn and subscription start. The three callbacks let the
            // card's header buttons drive focus / promote / spawn-child gestures.
            var subVm = new SubAgentSessionViewModel(
                childId,
                originalPrompt:  prompt,
                adapter:         adapter,
                onFocus:         OnSubAgentFocus,
                onPromoteToPane: OnSubAgentPromoteToPane,
                onSpawnChild:    OnSubAgentSpawnChild);
            _subAgentSessionsMap[childId] = subVm;

            // Subscribe to all events published for this child agent.
            // The handler is synchronous (no await) so it never blocks the bus pump.
            var subscription = _meshBus.Subscribe($"agent.{childId}.", (msg, _) =>
            {
                if (msg.Payload is AgentEvent agentEvt)
                    subVm.Trace.Append(agentEvt);
                return ValueTask.CompletedTask;
            });
            _subAgentSubscriptions[childId] = subscription;

            // ConfigureAwait(true) above keeps us on the UI thread — safe to touch UI directly.
            _subAgentSessions.Add(subVm);
            ShowSubAgentSection();
            ShowAgentTrace();

            StatusText.Text = $"{adapter.Name} 하위 에이전트 시작됨: {childId.ToString()[..8]}…";
        }
        catch (SpawnPolicyViolatedException ex)
        {
            StatusText.Text = $"스폰 정책 위반 ({ex.Kind}): {ex.Message}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"{adapter.Name} 하위 에이전트 시작 실패: {ex.Message}";
        }
    }

    // ── Per-card actions ─────────────────────────────────────────────────────────

    /// <summary>
    /// 🔍 — Pin <paramref name="target"/> as the focused card: expand it and collapse all
    /// other cards. Highlights the focused card with a colored left border via the IsFocused
    /// data trigger in <c>SubAgentCardControl.xaml</c>.
    /// </summary>
    private void OnSubAgentFocus(SubAgentSessionViewModel target)
    {
        // Card buttons are bound via XAML Command, which always dispatches on the UI thread.
        // Assert that contract here so future callers fail loudly rather than silently
        // corrupting the ObservableCollection from a background thread.
        Dispatcher.VerifyAccess();

        foreach (var vm in _subAgentSessions)
        {
            bool isTarget = ReferenceEquals(vm, target);
            vm.IsExpanded = isTarget;
            vm.IsFocused  = isTarget;
        }
        ShowAgentTrace();
        StatusText.Text = $"🔍 {target.ShortId} 포커스";
    }

    /// <summary>
    /// ⇗ 패널 — Open a new interactive Claude pane and re-issue the sub-agent's
    /// original prompt as the user's first message. The original sub-agent keeps running
    /// in its card; this gesture creates a parallel interactive thread the user can drive.
    /// </summary>
    private void OnSubAgentPromoteToPane(SubAgentSessionViewModel target)
    {
        // Fire-and-forget — the gesture is one-shot and reports its own status.
        _ = PromoteSubAgentToPaneAsync(target);
    }

    private async Task PromoteSubAgentToPaneAsync(SubAgentSessionViewModel target)
    {
        if (_workspace is null)
        {
            StatusText.Text = "워크스페이스가 아직 준비되지 않았습니다.";
            return;
        }
        if (string.IsNullOrEmpty(target.OriginalPrompt))
        {
            StatusText.Text = "이 sub-agent의 원본 prompt가 비어 있어 승격할 수 없습니다.";
            return;
        }

        try
        {
            // Copy the prompt to clipboard FIRST, before any awaits, so a slow split below
            // does not leave the user looking at an empty Claude REPL with no way to paste.
            // Clipboard transfer is fire-and-forget on the UI thread; failures (rare on
            // Windows) just degrade to a status hint.
            try { System.Windows.Clipboard.SetText(target.OriginalPrompt); }
            catch (Exception clipEx)
            {
                System.Diagnostics.Debug.WriteLine($"[promote] clipboard set failed: {clipEx.Message}");
            }

            // Open a Claude pane the same way the palette command does — vertical split
            // off the focused pane, then start `claude` in a fresh shell.
            var focused = _workspace.Layout.Current.Focused;
            var newPane = await _workspace.OpenSplitAsync(focused, SplitDirection.Vertical, CancellationToken.None).ConfigureAwait(true);
            var session = _workspace.Sessions[newPane];

            await PostToRendererAsync(Envelope.OpenPane(newPane)).ConfigureAwait(true);
            await PostToRendererAsync(Envelope.Layout(_workspace.Layout.Current)).ConfigureAwait(true);
            await PostToRendererAsync(Envelope.PaneBadge(newPane, "claude")).ConfigureAwait(true);

            // Brief pause so the shell prompt appears before sending the command.
            await Task.Delay(200).ConfigureAwait(true);
            await session.WriteInputAsync(System.Text.Encoding.UTF8.GetBytes("claude\r"), CancellationToken.None).ConfigureAwait(true);
            UpdateProviderBadge("Claude Code");

            // Mirror AskAgentAsync: register a root mesh session if we don't have one yet.
            if (_rootMeshSession is null)
            {
                _rootMeshSession = new PaneAgentSession(_agentTrace);
                _rootMeshSessionId = _rootMeshSession.Id;
                _mesh.RegisterRoot(_rootMeshSessionId, _rootMeshSession);
                SubscribeToMergeEvents();
                ShowAgentTrace();
            }

            // The user pastes (Ctrl+V) when Claude's REPL prompt appears. This is more
            // robust than a wall-clock delay+inject because it works on cold-start
            // machines, slow disks, and never splits multi-line prompts at \n boundaries
            // inside readline. See HIGH review notes for why timed injection was removed.
            StatusText.Text = $"⇗ {target.ShortId} → 새 Claude 패널 · prompt 클립보드 복사 · Ctrl+V 로 붙여넣기";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"패널 승격 실패: {ex.Message}";
        }
    }

    /// <summary>
    /// 🌱 — Open a prompt input dialog and spawn a grandchild sub-agent with the
    /// targeted card as its parent. <see cref="SpawnPolicy"/> still enforces depth/parallel
    /// limits and surfaces violations in the status bar.
    /// </summary>
    private void OnSubAgentSpawnChild(SubAgentSessionViewModel target)
    {
        // The parent must still be running for spawn to succeed (the topology tracker
        // deregisters on merge). Surface this clearly rather than letting the mesh throw.
        if (!target.IsRunning)
        {
            StatusText.Text = $"{target.ShortId} 은(는) 이미 종료되어 자식 spawn을 할 수 없습니다.";
            return;
        }

        var dlg = new PromptInputDialog(
            title:       $"🌱 자식 spawn ({target.AdapterName} · {target.ShortId})",
            label:       "자식 sub-agent에게 줄 prompt",
            hint:        $"이 prompt로 {target.AdapterName} 자식 에이전트가 시작됩니다 (부모와 동일 vendor). depth ≤ 3, 병렬 ≤ 4 정책이 적용됩니다.",
            initialText: null)
        {
            Owner = this,
        };
        if (dlg.ShowDialog() != true) return;

        string prompt = dlg.Prompt?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(prompt))
        {
            StatusText.Text = "Prompt가 비어 있어 spawn을 취소했습니다.";
            return;
        }

        // Fire-and-forget — the internal helper updates StatusText itself.
        // Grandchild inherits the parent card's adapter so a Codex sub-agent spawns
        // a Codex grandchild, etc. Cross-vendor spawns can be added later by exposing
        // a vendor picker inside PromptInputDialog.
        _ = SpawnSubAgentInternalAsync(target.ChildId, target.Adapter, prompt, CancellationToken.None);
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
        ShowAgentTrace();
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
            // Dispose per-pane bus subscriptions before tearing down the workspace so that
            // no in-flight pane.{id}.send message can reach a closed IControlChannel.
            foreach (var sub in _paneSubscriptions.Values)
            {
                try { await sub.DisposeAsync().ConfigureAwait(true); }
                catch { /* swallow */ }
            }
            _paneSubscriptions.Clear();
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

        // Stop the auto-pane prune timer FIRST so its tick can't race the watcher dispose.
        if (_externalTaskPruneTimer is not null)
        {
            try
            {
                _externalTaskPruneTimer.Stop();
                _externalTaskPruneTimer.Tick -= OnExternalTaskPruneTick;
            }
            catch { /* swallow */ }
            _externalTaskPruneTimer = null;
        }

        // Stop the external Task watcher early so its poll loop exits before the bus tears down.
        if (_claudeTranscriptWatcher is not null)
        {
            try { await _claudeTranscriptWatcher.DisposeAsync().ConfigureAwait(true); }
            catch { /* swallow */ }
        }

        // Tear down the agent mesh: cancel the root session first so the mesh pump exits,
        // then unsubscribe the merge listener, then dispose the mesh and bus.
        if (_rootMeshSession is not null)
        {
            try { await _rootMeshSession.CancelAsync().ConfigureAwait(true); }
            catch { /* swallow */ }
        }
        if (_mergeSubscription is not null)
        {
            try { await _mergeSubscription.DisposeAsync().ConfigureAwait(true); }
            catch { /* swallow */ }
        }
        foreach (var sub in _subAgentSubscriptions.Values)
        {
            try { await sub.DisposeAsync().ConfigureAwait(true); }
            catch { /* swallow */ }
        }
        try { await _mesh.DisposeAsync().ConfigureAwait(true); }
        catch { /* swallow */ }
        try { await _meshBus.DisposeAsync().ConfigureAwait(true); }
        catch { /* swallow */ }
    }
}
