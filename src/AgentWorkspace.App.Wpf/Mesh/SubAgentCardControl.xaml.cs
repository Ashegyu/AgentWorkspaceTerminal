using System.Runtime.Versioning;
using System.Windows.Controls;

namespace AgentWorkspace.App.Wpf.Mesh;

/// <summary>
/// Code-behind for <see cref="SubAgentCardControl"/>.
/// All visual logic is handled in XAML bindings; no imperative code is needed here.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class SubAgentCardControl : UserControl
{
    public SubAgentCardControl()
    {
        InitializeComponent();
    }
}
