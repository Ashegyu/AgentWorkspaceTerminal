using System;
using System.Threading;
using System.Threading.Tasks;

namespace AgentWorkspace.App.Wpf.CommandPalette;

/// <summary>
/// One executable entry shown in the Command Palette. Kept deliberately small for MVP-1 — five
/// concrete commands. Future MVPs add commands like "New Pane", "Run Workflow…", etc.
/// </summary>
/// <param name="Title">Short label shown in the palette list.</param>
/// <param name="Subtitle">Right-aligned hint; usually the keybinding or scope.</param>
/// <param name="Search">Lower-cased haystack used by the substring filter.</param>
/// <param name="Invoke">Action executed when the user picks this entry.</param>
public sealed record CommandEntry(
    string Title,
    string Subtitle,
    string Search,
    Func<CancellationToken, ValueTask> Invoke);
