using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
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

    /// <summary>Initialises the VM on the calling thread's dispatcher (UI thread).</summary>
    public SubAgentSessionViewModel(AgentSessionId childId)
        : this(childId, Dispatcher.CurrentDispatcher) { }

    /// <param name="childId">Identity of the child agent session.</param>
    /// <param name="dispatcher">WPF dispatcher; injectable for unit tests.</param>
    internal SubAgentSessionViewModel(AgentSessionId childId, Dispatcher dispatcher)
    {
        ChildId     = childId;
        _dispatcher = dispatcher;
        Trace       = new AgentTraceViewModel();
    }

    // ── identity ───────────────────────────────────────────────────────────────

    public AgentSessionId      ChildId { get; }
    public string              ShortId => ChildId.ToString()[..8] + "…";
    public AgentTraceViewModel Trace   { get; }

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

    /// <summary>True while the sub-agent is still running (not yet merged or errored).</summary>
    public bool IsRunning => _status == SubAgentStatus.Running;

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
