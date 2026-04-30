using System;
using System.Collections.Generic;
using System.Text.Json;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Abstractions.Policy;

namespace AgentWorkspace.Core.Policy;

/// <summary>
/// Translates an <see cref="ActionRequestEvent"/> (generic agent-side action description)
/// into a <see cref="ProposedAction"/> that the policy engine can reason about.
///
/// Mapping is best-effort: it understands the standard Claude Code tool surface
/// (`Bash`, `Read`, `Write`, `Edit`, `MultiEdit`, `WebFetch`, `WebSearch`, `Glob`, `Grep`).
/// Returns <see langword="null"/> for unknown tool types — callers should treat that as
/// "no policy translation available; default to AskUser".
/// </summary>
public static class ActionRequestPolicyMapper
{
    public static ProposedAction? ToProposedAction(ActionRequestEvent evt)
    {
        var input = evt.Input;
        var type  = evt.Type;

        return NormalizeName(type) switch
        {
            "bash" or "shell"     => MapBash(input),
            "read"                => MapRead(input),
            "write"               => MapWrite(input),
            "edit" or "multiedit" => MapEdit(input),
            "webfetch"            => MapWebFetch(input),
            "websearch"           => MapWebSearch(input),
            _ => null,
        };
    }

    // ── individual tool mappers ──────────────────────────────────────────────

    private static ProposedAction? MapBash(JsonElement? input)
    {
        var cmd = TryGetString(input, "command");
        if (cmd is null) return null;

        // Use a single-element argv carrying the full command line. The blacklist matches
        // ExecuteCommand.CommandLine, which falls through to Cmd when Args is empty.
        return new ExecuteCommand(cmd, Args: []);
    }

    private static ProposedAction? MapRead(JsonElement? input)
    {
        var path = TryGetString(input, "file_path") ?? TryGetString(input, "path");
        return path is null ? null : new ReadFile(path);
    }

    private static ProposedAction? MapWrite(JsonElement? input)
    {
        var path = TryGetString(input, "file_path") ?? TryGetString(input, "path");
        if (path is null) return null;

        var content = TryGetString(input, "content") ?? "";
        return new WriteFile(path, content.Length, FileWriteMode.Overwrite);
    }

    private static ProposedAction? MapEdit(JsonElement? input)
    {
        var path = TryGetString(input, "file_path") ?? TryGetString(input, "path");
        if (path is null) return null;

        // Use replacement text length as a proxy for the resulting content size.
        var newStr = TryGetString(input, "new_string") ?? "";
        return new WriteFile(path, newStr.Length, FileWriteMode.Overwrite);
    }

    private static ProposedAction? MapWebFetch(JsonElement? input)
    {
        var url = TryGetString(input, "url");
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        return new NetworkCall(uri, "GET");
    }

    private static ProposedAction? MapWebSearch(JsonElement? input)
    {
        // Web search is a network call, but the URL is the search engine itself; we don't
        // have a meaningful target. Surface as a generic outbound call.
        _ = input;
        return new NetworkCall(new Uri("https://search/"), "GET");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string NormalizeName(string name) => name.ToLowerInvariant();

    private static string? TryGetString(JsonElement? input, string property)
    {
        if (input is not { ValueKind: JsonValueKind.Object } el) return null;
        if (!el.TryGetProperty(property, out var prop))           return null;
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }
}
