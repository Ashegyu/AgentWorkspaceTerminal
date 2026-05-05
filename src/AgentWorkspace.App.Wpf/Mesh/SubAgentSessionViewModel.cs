using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Windows.Input;
using System.Windows.Threading;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.App.Wpf.AgentTrace;

namespace AgentWorkspace.App.Wpf.Mesh;

/// <summary>Lifecycle status of a sub-agent card.</summary>
public enum SubAgentStatus { Running, Merged, Error }

/// <summary>
/// ViewModel for a single sub-agent card shown in the trace panel.
/// Owns its own <see cref="AgentTraceViewModel"/> so events stream live into the card body.
/// <para>
/// Thread-safe: all mutating properties marshal to the WPF dispatcher before raising
/// <see cref="PropertyChanged"/>, so bus handlers can update the card from background threads.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SubAgentSessionViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly Dispatcher _dispatcher;
    private SubAgentStatus _status = SubAgentStatus.Running;
    private int _exitCode;
    private bool _isExpanded = true;
    private bool _isFocused;
    private string _mergedSummary = string.Empty;

    /// <summary>Initialises the VM on the calling thread's dispatcher (UI thread).</summary>
    public SubAgentSessionViewModel(
        AgentSessionId childId,
        string originalPrompt,
        IAgentAdapter adapter,
        Action<SubAgentSessionViewModel>? onFocus           = null,
        Action<SubAgentSessionViewModel>? onPromoteToPane   = null,
        Action<SubAgentSessionViewModel>? onSpawnChild      = null,
        bool isExternal                                     = false,
        string? externalSubAgentType                        = null)
        : this(childId, originalPrompt, adapter, Dispatcher.CurrentDispatcher, onFocus, onPromoteToPane, onSpawnChild, isExternal, externalSubAgentType)
    { }

    /// <param name="childId">Identity of the child agent session.</param>
    /// <param name="originalPrompt">
    ///   The prompt this sub-agent was spawned with. Surfaced in the "패널로 승격" gesture
    ///   so the new pane starts with the same prompt.
    /// </param>
    /// <param name="adapter">
    ///   The adapter used to spawn this sub-agent. Used by the 🌱 grandchild gesture so
    ///   spawned children inherit their parent's vendor (Claude/Codex/Gemini).
    /// </param>
    /// <param name="dispatcher">WPF dispatcher; injectable for unit tests.</param>
    /// <param name="onFocus">Callback when the user clicks the focus button on this card.</param>
    /// <param name="onPromoteToPane">Callback when the user wants to promote this sub-agent into an interactive Claude pane.</param>
    /// <param name="onSpawnChild">Callback when the user wants to spawn a grandchild from this sub-agent.</param>
    /// <param name="isExternal">True for cards observed via Claude transcript tailing (not mesh-spawned).</param>
    /// <param name="externalSubAgentType">For external cards: Claude's <c>subagent_type</c> label.</param>
    internal SubAgentSessionViewModel(
        AgentSessionId childId,
        string originalPrompt,
        IAgentAdapter adapter,
        Dispatcher dispatcher,
        Action<SubAgentSessionViewModel>? onFocus           = null,
        Action<SubAgentSessionViewModel>? onPromoteToPane   = null,
        Action<SubAgentSessionViewModel>? onSpawnChild      = null,
        bool isExternal                                     = false,
        string? externalSubAgentType                        = null)
    {
        ChildId              = childId;
        OriginalPrompt       = originalPrompt ?? string.Empty;
        Adapter              = adapter ?? throw new ArgumentNullException(nameof(adapter));
        IsExternal           = isExternal;
        ExternalSubAgentType = externalSubAgentType;
        _dispatcher          = dispatcher;
        Trace                = new AgentTraceViewModel();

        FocusCommand         = new RelayCommand(() => onFocus?.Invoke(this));
        PromoteToPaneCommand = new RelayCommand(() => onPromoteToPane?.Invoke(this));
        // Grandchild spawn is meaningless for external (Claude-CLI-managed) sub-agents
        // because they aren't registered in our AgentMesh. Disable the command instead
        // of failing later — the XAML button is hidden via IsExternal data trigger anyway.
        SpawnChildCommand    = new RelayCommand(
            () => onSpawnChild?.Invoke(this),
            canExecute: () => !IsExternal);
    }

    // ── identity ───────────────────────────────────────────────────────────────

    public AgentSessionId      ChildId        { get; }
    public string              ShortId        => ChildId.ToString()[..8] + "…";
    public AgentTraceViewModel Trace          { get; }

    /// <summary>Original spawn prompt — used when promoting this sub-agent into a Claude pane.</summary>
    public string              OriginalPrompt { get; }

    /// <summary>
    /// Adapter that spawned this sub-agent (Claude/Codex/Gemini/etc.). Grandchild spawns
    /// from the 🌱 button reuse this so the new child inherits the same vendor.
    /// </summary>
    public IAgentAdapter       Adapter        { get; }

    /// <summary>Display name of the adapter (e.g. "Claude Code", "Codex", "Gemini").</summary>
    public string              AdapterName    => Adapter.Name;

    /// <summary>
    /// True for sub-agents spawned by the user's interactive Claude CLI (observed via
    /// transcript tailing) rather than spawned through our AgentMesh. External cards
    /// are visibility-only — we don't own them and can't cancel/spawn-children of them.
    /// </summary>
    public bool                IsExternal           { get; }

    /// <summary>
    /// For external cards: Claude's <c>subagent_type</c> label, e.g. <c>"general-purpose"</c>.
    /// Null for internal (mesh-spawned) cards.
    /// </summary>
    public string?             ExternalSubAgentType { get; }

    /// <summary>Header source label — "(외부)" for external, adapter name for internal.</summary>
    public string              SourceLabel
        => IsExternal
            ? $"외부 · {ExternalSubAgentType ?? "?"}"
            : AdapterName;

    // ── commands ───────────────────────────────────────────────────────────────

    public ICommand FocusCommand         { get; }
    public ICommand PromoteToPaneCommand { get; }
    public ICommand SpawnChildCommand    { get; }

    // ── bindable state ─────────────────────────────────────────────────────────

    public SubAgentStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            RunOnUi(() =>
            {
                _status = value;
                Notify();
                Notify(nameof(StatusLabel));
                Notify(nameof(IsRunning));
                Notify(nameof(IsCompleted));
            });
        }
    }

    public int ExitCode
    {
        get => _exitCode;
        set
        {
            if (_exitCode == value) return;
            RunOnUi(() =>
            {
                _exitCode = value;
                Notify();
                Notify(nameof(StatusLabel));
            });
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            RunOnUi(() =>
            {
                _isExpanded = value;
                Notify();
            });
        }
    }

    /// <summary>True when this card is the user's current focus (highlighted border).</summary>
    public bool IsFocused
    {
        get => _isFocused;
        set
        {
            if (_isFocused == value) return;
            RunOnUi(() =>
            {
                _isFocused = value;
                Notify();
            });
        }
    }

    /// <summary>
    /// Redacted single-line summary shown under the header once the sub-agent merges.
    /// Set by the merge handler on <c>MergedPayload.RedactedSummary</c>
    /// (defined in <c>AgentWorkspace.Core.Mesh</c>).
    /// </summary>
    public string MergedSummary
    {
        get => _mergedSummary;
        set
        {
            if (_mergedSummary == value) return;
            RunOnUi(() =>
            {
                _mergedSummary = value ?? string.Empty;
                Notify();
                Notify(nameof(HasMergedSummary));
                Notify(nameof(MergedSummaryPreview));
            });
        }
    }

    /// <summary>True while the sub-agent is still running (not yet merged or errored).</summary>
    public bool IsRunning   => _status == SubAgentStatus.Running;

    /// <summary>True once the sub-agent has merged or errored.</summary>
    public bool IsCompleted => _status != SubAgentStatus.Running;

    /// <summary>Whether to render the inline merged-summary preview (header sub-line).</summary>
    public bool HasMergedSummary => !string.IsNullOrWhiteSpace(_mergedSummary);

    /// <summary>One-line preview of the merged summary (first ~120 chars, single line).</summary>
    public string MergedSummaryPreview
    {
        get
        {
            if (string.IsNullOrEmpty(_mergedSummary)) return string.Empty;
            // Collapse whitespace + truncate.
            var oneLine = _mergedSummary.Replace('\r', ' ').Replace('\n', ' ');
            const int maxLen = 120;
            return oneLine.Length <= maxLen ? oneLine : oneLine[..maxLen] + "…";
        }
    }

    /// <summary>Human-readable status label shown in the card header.</summary>
    public string StatusLabel => _status switch
    {
        SubAgentStatus.Running => "🔄 실행 중",
        SubAgentStatus.Merged  => $"✓ 병합됨  ·  종료코드 {ExitCode}",
        SubAgentStatus.Error   => "✗ 오류",
        _                      => "알 수 없음",
    };

    // ── helpers ────────────────────────────────────────────────────────────────

    private void RunOnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
            action();
        else
            _dispatcher.Invoke(action);
    }

    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// Resolve referenced symbol from the AgentMesh assembly without adding a new project ref.
// MergedPayload is defined in AgentWorkspace.Core.Mesh.AgentMesh.cs and is already referenced
// transitively through MainWindow.xaml.cs — this comment exists so future readers of this
// VM know where the contract for MergedSummary originates.
