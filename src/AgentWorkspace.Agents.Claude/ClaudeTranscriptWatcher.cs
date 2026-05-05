using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AgentWorkspace.Agents.Claude;

/// <summary>
/// One Task tool invocation observed in the user's interactive Claude session
/// (i.e. the running <c>claude</c> CLI in a PTY pane spawned an internal sub-agent).
/// </summary>
/// <param name="ToolUseId">
///   Stable id assigned by Claude (e.g. <c>"toolu_01ABC..."</c>); used to correlate
///   the eventual <see cref="TaskResult"/>.
/// </param>
/// <param name="SubAgentType">
///   Claude's sub-agent type label, e.g. <c>"general-purpose"</c>, <c>"code-reviewer"</c>.
/// </param>
/// <param name="Prompt">The prompt the user's main agent gave the sub-agent.</param>
/// <param name="StartedAt">UTC time the line was observed in the transcript.</param>
public sealed record TaskInvocation(
    string ToolUseId,
    string SubAgentType,
    string Prompt,
    DateTimeOffset StartedAt);

/// <summary>Result of a previously-observed <see cref="TaskInvocation"/>.</summary>
public sealed record TaskResult(
    string ToolUseId,
    string Output,
    bool IsError,
    DateTimeOffset CompletedAt);

/// <summary>
/// Polls <c>~/.claude/projects/</c> for newly-written JSONL session lines and surfaces
/// Claude Task tool invocations so the host UI can show them as "external" sub-agent
/// cards alongside meshed sub-agents.
/// <para>
/// <b>Why polling instead of FileSystemWatcher</b>: FSW on Windows is notoriously
/// unreliable for append-only logs (missed events, buffer overruns under heavy I/O).
/// A simple fixed-cadence poll is more predictable and has no measurable cost — JSONL
/// files only grow on user actions, and seek+read of a few hundred bytes is trivial.
/// </para>
/// </summary>
public sealed class ClaudeTranscriptWatcher : IAsyncDisposable
{
    private readonly string _transcriptRoot;
    private readonly TimeSpan _pollInterval;
    private readonly ConcurrentDictionary<string, long> _filePositions = new();
    /// <summary>
    /// Tool-use ids we've already fired <see cref="TaskStarted"/> for (defensive dedup).
    /// Value is the UTC timestamp the entry was added — used by the periodic sweep so the
    /// dictionary doesn't grow unboundedly over a long-running app session.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seenStarts     = new();
    /// <summary>Tool-use ids we've already fired <see cref="TaskCompleted"/> for. Same timestamp scheme.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seenCompletions = new();
    private readonly TimeSpan _seenRetention;
    private readonly CancellationTokenSource _cts = new();
    private Task? _pollLoop;
    private int _disposed;

    /// <summary>Fired once for each new <c>tool_use</c> line whose <c>name == "Task"</c>.</summary>
    public event EventHandler<TaskInvocation>? TaskStarted;

    /// <summary>Fired once for each <c>tool_result</c> matching a previously-fired <see cref="TaskStarted"/>.</summary>
    public event EventHandler<TaskResult>? TaskCompleted;

    /// <param name="transcriptRoot">
    ///   Directory to watch; defaults to <c>%USERPROFILE%\.claude\projects</c>.
    ///   Override for tests or non-default Claude installs.
    /// </param>
    /// <param name="pollInterval">Poll cadence. Defaults to 1 second.</param>
    /// <param name="seenRetention">
    ///   How long to retain dedup entries in <c>_seenStarts</c>/<c>_seenCompletions</c>
    ///   before sweeping them. Defaults to 1 hour — long enough that a real Task can run
    ///   to completion, short enough that thousands of completed tasks over a multi-day
    ///   session don't bloat memory. Tests use a much shorter window to verify sweep.
    /// </param>
    public ClaudeTranscriptWatcher(
        string? transcriptRoot = null,
        TimeSpan? pollInterval = null,
        TimeSpan? seenRetention = null)
    {
        _transcriptRoot = transcriptRoot
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", "projects");
        _pollInterval   = pollInterval  ?? TimeSpan.FromSeconds(1);
        _seenRetention  = seenRetention ?? TimeSpan.FromHours(1);
    }

    /// <summary>Begins the background poll loop. Idempotent — second call is a no-op.</summary>
    public Task StartAsync()
    {
        if (_pollLoop is not null) return Task.CompletedTask;
        _pollLoop = Task.Run(() => PollLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await _cts.CancelAsync().ConfigureAwait(false); } catch { }
        if (_pollLoop is not null)
        {
            try { await _pollLoop.ConfigureAwait(false); } catch { }
        }
        _cts.Dispose();
    }

    /// <summary>Sweep cadence: prune <see cref="_filePositions"/> entries for deleted files every N polls.</summary>
    private const int PruneEveryNPolls = 60;
    private int _pollTick;

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (Directory.Exists(_transcriptRoot))
                {
                    var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var file in EnumerateJsonlFiles(_transcriptRoot))
                    {
                        seenFiles.Add(file);
                        ProcessFileTail(file);
                    }

                    // Stale-entry cleanup runs roughly once a minute (60 polls × 1 s default).
                    // Without this, deleted/rotated session files leave dictionary entries
                    // that grow unbounded over a long-running app.
                    if (Interlocked.Increment(ref _pollTick) % PruneEveryNPolls == 0)
                    {
                        PruneStaleFilePositions(seenFiles);
                        PruneOldSeenEntries();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[transcript-watcher] poll error: {ex.Message}");
            }

            try { await Task.Delay(_pollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Removes <see cref="_filePositions"/> entries for files that no longer exist in the
    /// watched tree. Idempotent and safe to call from the poll loop.
    /// </summary>
    private void PruneStaleFilePositions(HashSet<string> currentFiles)
    {
        foreach (var key in _filePositions.Keys)
        {
            if (!currentFiles.Contains(key))
            {
                _filePositions.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// Removes <see cref="_seenStarts"/> / <see cref="_seenCompletions"/> entries older than
    /// <see cref="_seenRetention"/>. The dedup contract still holds within the retention window
    /// (the only realistic time frame in which Claude could re-emit the same tool_use line).
    /// Beyond that, we accept the negligible risk of re-emission to bound memory.
    /// </summary>
    internal void PruneOldSeenEntries()
    {
        var cutoff = DateTimeOffset.UtcNow - _seenRetention;
        PruneOlderThan(_seenStarts,      cutoff);
        PruneOlderThan(_seenCompletions, cutoff);
    }

    private static void PruneOlderThan(ConcurrentDictionary<string, DateTimeOffset> dict, DateTimeOffset cutoff)
    {
        foreach (var (key, ts) in dict)
        {
            if (ts < cutoff)
            {
                dict.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// Test-only accessors so the dedup-set sweep can be verified without exposing the
    /// underlying dictionaries to production callers.
    /// </summary>
    internal int SeenStartsCount      => _seenStarts.Count;
    internal int SeenCompletionsCount => _seenCompletions.Count;
    internal int FilePositionsCount   => _filePositions.Count;

    /// <summary>
    /// Test-only: synchronously runs the stale-file-position sweep against the current
    /// watched tree, returning the number of entries removed. Lets tests verify
    /// deletion-driven cleanup without waiting for the 60-poll cadence.
    /// </summary>
    internal int PruneStaleFilePositionsNow()
    {
        if (!Directory.Exists(_transcriptRoot)) return 0;
        var seen = new HashSet<string>(EnumerateJsonlFiles(_transcriptRoot), StringComparer.OrdinalIgnoreCase);
        int before = _filePositions.Count;
        PruneStaleFilePositions(seen);
        return before - _filePositions.Count;
    }

    private static IEnumerable<string> EnumerateJsonlFiles(string root)
    {
        // Claude writes one file per session, organised by encoded cwd:
        //   ~/.claude/projects/<cwd-encoded>/<session-id>.jsonl
        // We don't decode the cwd — we just tail every .jsonl regardless of subdir.
        return Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories);
    }

    private void ProcessFileTail(string path)
    {
        long lastPos = _filePositions.GetOrAdd(path, _ => 0L);

        long currentSize;
        try { currentSize = new FileInfo(path).Length; }
        catch (IOException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[transcript-watcher] FileInfo failed for {path}: {ex.Message}");
            return;
        }
        catch { return; }

        // File rotated/truncated — reset our cursor to start of file.
        if (currentSize < lastPos) lastPos = 0;
        if (currentSize == lastPos) return;

        // Read at the byte level rather than via StreamReader so we can advance the cursor
        // by *exactly* the bytes consumed (complete lines only). StreamReader.ReadLine
        // buffers ahead, so its BaseStream.Position lies about how much we actually consumed,
        // and partial lines from a mid-flush would either be re-emitted on the next poll
        // (duplicate cards) or lost. The byte-level approach is small and self-contained.
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(lastPos, SeekOrigin.Begin);

            // Snapshot length again *after* opening to avoid reading bytes that arrived
            // between FileInfo.Length and the open — those will appear in a future poll.
            long readUpTo = fs.Length;
            if (readUpTo <= lastPos) return;

            int toRead = (int)Math.Min(readUpTo - lastPos, 1024 * 1024); // 1 MiB safety cap
            byte[] buf = new byte[toRead];
            int read = fs.Read(buf, 0, toRead);
            if (read <= 0) return;

            // Find the last newline in the buffer; only emit lines up to and including it.
            // Anything after the last newline is a partial line that will be re-read next tick.
            int lastNewline = -1;
            for (int i = read - 1; i >= 0; i--)
            {
                if (buf[i] == (byte)'\n') { lastNewline = i; break; }
            }
            if (lastNewline < 0)
            {
                // No complete line yet — leave position unchanged so we re-read from same spot.
                return;
            }

            int completeBytes = lastNewline + 1;
            string text = System.Text.Encoding.UTF8.GetString(buf, 0, completeBytes);
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(trimmed)) continue;
                TryHandleLine(trimmed);
            }

            _filePositions[path] = lastPos + completeBytes;
        }
        catch (IOException ex)
        {
            // File locked (AV scan, transient) — skip this poll cycle, retry next tick.
            System.Diagnostics.Debug.WriteLine($"[transcript-watcher] tail IO error on {path}: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            // Different OS user wrote this file (rare) — skip permanently by advancing cursor.
            System.Diagnostics.Debug.WriteLine($"[transcript-watcher] auth error on {path}: {ex.Message}");
            _filePositions[path] = currentSize;
        }
    }

    private void TryHandleLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            // Schema reference (v1, observed Oct 2025):
            //   {"type":"assistant", "message":{"content":[
            //     {"type":"tool_use","id":"toolu_xxx","name":"Task","input":{...}}
            //   ]}}
            //   {"type":"user", "message":{"content":[
            //     {"type":"tool_result","tool_use_id":"toolu_xxx","content":[...],"is_error":false}
            //   ]}}
            if (!root.TryGetProperty("type", out var typeProp)) return;
            string? type = typeProp.GetString();
            if (string.IsNullOrEmpty(type)) return;
            if (!root.TryGetProperty("message", out var msg)) return;
            if (!msg.TryGetProperty("content", out var content)) return;
            if (content.ValueKind != JsonValueKind.Array) return;

            foreach (var item in content.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var itp)) continue;
                string? itemType = itp.GetString();

                if (type == "assistant" && itemType == "tool_use")
                {
                    HandleToolUse(item);
                }
                else if (type == "user" && itemType == "tool_result")
                {
                    HandleToolResult(item);
                }
            }
        }
        catch (JsonException)
        {
            // Malformed line (e.g. partial flush) — ignore, will be re-read after EOF growth check.
        }
    }

    private void HandleToolUse(JsonElement item)
    {
        string name = item.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
        if (name != "Task") return;

        string id = item.TryGetProperty("id", out var ip) ? ip.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(id)) return;

        // Defensive dedup — the byte-level reader above should never re-emit the same line,
        // but if Claude rewrites a session file (rare) we don't want to spawn duplicate cards.
        if (!_seenStarts.TryAdd(id, DateTimeOffset.UtcNow)) return;

        if (!item.TryGetProperty("input", out var input))
        {
            // Schema-drift visibility: emit a debug warning so a missing input field doesn't
            // hide silently. We still fire the event with empty fields so the card appears.
            System.Diagnostics.Debug.WriteLine($"[transcript-watcher] Task tool_use missing 'input' (id={id}); schema may have changed.");
        }
        string subType = "general-purpose";
        string prompt  = "";
        if (input.ValueKind == JsonValueKind.Object)
        {
            if (input.TryGetProperty("subagent_type", out var sp)) subType = sp.GetString() ?? subType;
            if (input.TryGetProperty("prompt",        out var pp)) prompt  = pp.GetString() ?? prompt;
        }

        TaskStarted?.Invoke(this, new TaskInvocation(id, subType, prompt, DateTimeOffset.UtcNow));
    }

    private void HandleToolResult(JsonElement item)
    {
        string id = item.TryGetProperty("tool_use_id", out var ip) ? ip.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(id)) return;

        // Only fire completion for tasks we previously saw start; ignores tool_results
        // for non-Task tools and prevents duplicate completion if a session file is rewritten.
        if (!_seenStarts.ContainsKey(id)) return;
        if (!_seenCompletions.TryAdd(id, DateTimeOffset.UtcNow)) return;

        bool isError = item.TryGetProperty("is_error", out var ep) && ep.ValueKind == JsonValueKind.True;

        // tool_result content can be either a plain string or an array of text blocks —
        // flatten both shapes into a single string for the card body.
        string output = ExtractToolResultText(item);

        TaskCompleted?.Invoke(this, new TaskResult(id, output, isError, DateTimeOffset.UtcNow));
    }

    private static string ExtractToolResultText(JsonElement toolResult)
    {
        if (!toolResult.TryGetProperty("content", out var c)) return string.Empty;

        // Shape 1: "content": "string"
        if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? string.Empty;

        // Shape 2: "content": [{"type":"text","text":"..."}, ...]
        if (c.ValueKind == JsonValueKind.Array)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var part in c.EnumerateArray())
            {
                if (!part.TryGetProperty("type", out var t)) continue;
                if (t.GetString() != "text") continue;
                if (!part.TryGetProperty("text", out var txt)) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(txt.GetString());
            }
            return sb.ToString();
        }

        return string.Empty;
    }
}
