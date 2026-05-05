using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Agents.Claude;

namespace AgentWorkspace.Tests.Agents;

/// <summary>
/// Behavioural tests for <see cref="ClaudeTranscriptWatcher"/>.
/// <para>
/// Each test creates a temporary directory, writes synthetic JSONL lines that match
/// (or deliberately deviate from) the Claude session transcript schema, and asserts
/// that <c>TaskStarted</c> / <c>TaskCompleted</c> fire exactly when expected.
/// </para>
/// <para>
/// The watcher polls every 1 s by default; tests use a 100 ms cadence to keep total
/// runtime under a few seconds. <see cref="TaskCompletionSource{TResult}"/> with timeout
/// is the synchronisation primitive for asserting on async events.
/// </para>
/// </summary>
public sealed class ClaudeTranscriptWatcherTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly TimeSpan _pollFast = TimeSpan.FromMilliseconds(100);
    private readonly TimeSpan _waitTimeout = TimeSpan.FromSeconds(5);

    public ClaudeTranscriptWatcherTests()
    {
        // Each test gets an isolated directory under %TEMP% to avoid cross-test pollution
        // when the test runner parallelises within the same class.
        _tempRoot = Path.Combine(Path.GetTempPath(), "claude-watcher-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private string CreateSessionFile(string fileName = "session.jsonl")
    {
        // Mimic Claude's per-cwd subdirectory structure so the recursive enumerator picks it up.
        var subdir = Path.Combine(_tempRoot, "project-cwd-encoded");
        Directory.CreateDirectory(subdir);
        var path = Path.Combine(subdir, fileName);
        File.WriteAllText(path, string.Empty); // create empty file
        return path;
    }

    private static void AppendLine(string path, string line)
    {
        // Append + flush so the watcher sees a complete line.
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        fs.Write(bytes, 0, bytes.Length);
        fs.Flush();
    }

    private static string TaskUseLine(string toolUseId, string subAgentType, string prompt) =>
        "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\""
        + toolUseId + "\",\"name\":\"Task\",\"input\":{\"subagent_type\":\""
        + subAgentType + "\",\"prompt\":\"" + prompt + "\"}}]}}";

    private static string TaskResultLine(string toolUseId, string output, bool isError = false) =>
        "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\""
        + toolUseId + "\",\"content\":[{\"type\":\"text\",\"text\":\""
        + output + "\"}],\"is_error\":" + (isError ? "true" : "false") + "}]}}";

    private static string NonTaskUseLine(string toolUseId) =>
        "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\""
        + toolUseId + "\",\"name\":\"Bash\",\"input\":{\"command\":\"ls\"}}]}}";

    // ── routing tests ────────────────────────────────────────────────────────────

    [Fact]
    public async Task TaskStarted_FiresForTaskToolUseLine()
    {
        var path = CreateSessionFile();
        await using var watcher = new ClaudeTranscriptWatcher(_tempRoot, _pollFast);

        var tcs = new TaskCompletionSource<TaskInvocation>();
        watcher.TaskStarted += (_, t) => tcs.TrySetResult(t);
        await watcher.StartAsync();

        AppendLine(path, TaskUseLine("toolu_001", "code-reviewer", "review the diff"));

        var task = await tcs.Task.WaitAsync(_waitTimeout);
        Assert.Equal("toolu_001",     task.ToolUseId);
        Assert.Equal("code-reviewer", task.SubAgentType);
        Assert.Equal("review the diff", task.Prompt);
    }

    [Fact]
    public async Task TaskStarted_DoesNotFireForNonTaskToolUse()
    {
        var path = CreateSessionFile();
        await using var watcher = new ClaudeTranscriptWatcher(_tempRoot, _pollFast);

        int started = 0;
        watcher.TaskStarted += (_, _) => Interlocked.Increment(ref started);
        await watcher.StartAsync();

        AppendLine(path, NonTaskUseLine("toolu_bash_01"));
        // Wait for at least 3 poll cycles so we're confident no event will fire.
        await Task.Delay(400);

        Assert.Equal(0, started);
    }

    [Fact]
    public async Task TaskCompleted_FiresForMatchingToolResult()
    {
        var path = CreateSessionFile();
        await using var watcher = new ClaudeTranscriptWatcher(_tempRoot, _pollFast);

        var startedTcs   = new TaskCompletionSource<TaskInvocation>();
        var completedTcs = new TaskCompletionSource<TaskResult>();
        watcher.TaskStarted   += (_, t) => startedTcs.TrySetResult(t);
        watcher.TaskCompleted += (_, r) => completedTcs.TrySetResult(r);
        await watcher.StartAsync();

        AppendLine(path, TaskUseLine("toolu_002", "general-purpose", "list files"));
        await startedTcs.Task.WaitAsync(_waitTimeout);

        AppendLine(path, TaskResultLine("toolu_002", "Found 5 files."));
        var result = await completedTcs.Task.WaitAsync(_waitTimeout);

        Assert.Equal("toolu_002",     result.ToolUseId);
        Assert.Equal("Found 5 files.", result.Output);
        Assert.False(result.IsError);
    }

    [Fact]
    public async Task TaskCompleted_PreservesIsErrorFlag()
    {
        var path = CreateSessionFile();
        await using var watcher = new ClaudeTranscriptWatcher(_tempRoot, _pollFast);

        var completedTcs = new TaskCompletionSource<TaskResult>();
        watcher.TaskCompleted += (_, r) => completedTcs.TrySetResult(r);
        await watcher.StartAsync();

        AppendLine(path, TaskUseLine("toolu_err", "general-purpose", "do impossible thing"));
        AppendLine(path, TaskResultLine("toolu_err", "subagent failed", isError: true));

        var result = await completedTcs.Task.WaitAsync(_waitTimeout);
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task TaskCompleted_OrphanResultIsIgnored()
    {
        // tool_result without a preceding tool_use should not fire (could be a Bash result, etc.)
        var path = CreateSessionFile();
        await using var watcher = new ClaudeTranscriptWatcher(_tempRoot, _pollFast);

        int completed = 0;
        watcher.TaskCompleted += (_, _) => Interlocked.Increment(ref completed);
        await watcher.StartAsync();

        AppendLine(path, TaskResultLine("toolu_orphan", "stale data"));
        await Task.Delay(400);

        Assert.Equal(0, completed);
    }

    // ── dedup / partial-line / rotation ───────────────────────────────────────────

    [Fact]
    public async Task TaskStarted_DuplicateToolUseIdFiresOnlyOnce()
    {
        var path = CreateSessionFile();
        await using var watcher = new ClaudeTranscriptWatcher(_tempRoot, _pollFast);

        int started = 0;
        watcher.TaskStarted += (_, _) => Interlocked.Increment(ref started);
        await watcher.StartAsync();

        var line = TaskUseLine("toolu_dup", "general-purpose", "first run");
        AppendLine(path, line);
        await Task.Delay(300);
        AppendLine(path, line); // identical line written again
        await Task.Delay(300);

        Assert.Equal(1, started);
    }

    [Fact]
    public async Task TaskStarted_PartialLineHeldUntilNewline()
    {
        // Simulate Claude mid-flush: write a tool_use line in two halves with no newline
        // until the second write. The watcher MUST NOT emit on the first poll cycle.
        var path = CreateSessionFile();
        await using var watcher = new ClaudeTranscriptWatcher(_tempRoot, _pollFast);

        var tcs = new TaskCompletionSource<TaskInvocation>();
        watcher.TaskStarted += (_, t) => tcs.TrySetResult(t);
        await watcher.StartAsync();

        var line = TaskUseLine("toolu_partial", "general-purpose", "partial flush test");
        var halfPoint = line.Length / 2;
        var firstHalf  = line[..halfPoint];
        var secondHalf = line[halfPoint..];

        // Write first half WITHOUT a newline.
        using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            var bytes = Encoding.UTF8.GetBytes(firstHalf);
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush();
        }
        // Allow several poll cycles — must not fire yet.
        await Task.Delay(400);
        Assert.False(tcs.Task.IsCompleted);

        // Write the rest plus newline. Now the watcher should emit.
        using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            var bytes = Encoding.UTF8.GetBytes(secondHalf + "\n");
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush();
        }

        var task = await tcs.Task.WaitAsync(_waitTimeout);
        Assert.Equal("toolu_partial", task.ToolUseId);
    }

    [Fact]
    public async Task MultipleSessionFiles_BothTailed()
    {
        // Realistic Claude flow: each session has its own .jsonl in a separate subdir.
        // The watcher's recursive enumeration should tail every file independently.
        var path1 = CreateSessionFile("session-1.jsonl");
        await using var watcher = new ClaudeTranscriptWatcher(_tempRoot, _pollFast);

        var firstTcs  = new TaskCompletionSource<TaskInvocation>();
        var secondTcs = new TaskCompletionSource<TaskInvocation>();
        watcher.TaskStarted += (_, t) =>
        {
            if (t.ToolUseId == "toolu_a") firstTcs.TrySetResult(t);
            else if (t.ToolUseId == "toolu_b") secondTcs.TrySetResult(t);
        };
        await watcher.StartAsync();

        AppendLine(path1, TaskUseLine("toolu_a", "general-purpose", "from session 1"));
        await firstTcs.Task.WaitAsync(_waitTimeout);

        // Different subdir, different session file — mirrors a new Claude session.
        var subdir2 = Path.Combine(_tempRoot, "another-cwd-encoded");
        Directory.CreateDirectory(subdir2);
        var path2 = Path.Combine(subdir2, "session-2.jsonl");
        File.WriteAllText(path2, string.Empty);
        AppendLine(path2, TaskUseLine("toolu_b", "code-reviewer", "from session 2"));

        var second = await secondTcs.Task.WaitAsync(_waitTimeout);
        Assert.Equal("toolu_b", second.ToolUseId);
    }

    // ── schema fallback ──────────────────────────────────────────────────────────

    [Fact]
    public async Task TaskStarted_FiresEvenWhenInputFieldsAreMissing()
    {
        // Schema drift: future Claude version omits input.subagent_type / input.prompt.
        // Watcher should still surface the Task with default fallbacks rather than dropping it silently.
        var path = CreateSessionFile();
        await using var watcher = new ClaudeTranscriptWatcher(_tempRoot, _pollFast);

        var tcs = new TaskCompletionSource<TaskInvocation>();
        watcher.TaskStarted += (_, t) => tcs.TrySetResult(t);
        await watcher.StartAsync();

        // Valid tool_use with name=Task but input is an empty object.
        var line = "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"toolu_drift\",\"name\":\"Task\",\"input\":{}}]}}";
        AppendLine(path, line);

        var task = await tcs.Task.WaitAsync(_waitTimeout);
        Assert.Equal("toolu_drift",     task.ToolUseId);
        Assert.Equal("general-purpose", task.SubAgentType); // default
        Assert.Equal(string.Empty,      task.Prompt);       // default
    }

    [Fact]
    public async Task MalformedJsonLines_AreSkippedSilently()
    {
        var path = CreateSessionFile();
        await using var watcher = new ClaudeTranscriptWatcher(_tempRoot, _pollFast);

        int started = 0;
        var tcs = new TaskCompletionSource<TaskInvocation>();
        watcher.TaskStarted += (_, t) =>
        {
            Interlocked.Increment(ref started);
            tcs.TrySetResult(t);
        };
        await watcher.StartAsync();

        // Malformed line, then valid line. Malformed line should not block valid one.
        AppendLine(path, "{not valid json at all");
        AppendLine(path, TaskUseLine("toolu_valid", "general-purpose", "after garbage"));

        await tcs.Task.WaitAsync(_waitTimeout);
        Assert.Equal(1, started);
    }

    // ── lifecycle ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DoubleDispose_DoesNotThrow()
    {
        var watcher = new ClaudeTranscriptWatcher(_tempRoot, _pollFast);
        await watcher.StartAsync();
        await watcher.DisposeAsync();
        // Second dispose must be a no-op, not throw.
        await watcher.DisposeAsync();
    }

    [Fact]
    public async Task NoEventsAfterDispose()
    {
        var path = CreateSessionFile();
        var watcher = new ClaudeTranscriptWatcher(_tempRoot, _pollFast);
        int started = 0;
        watcher.TaskStarted += (_, _) => Interlocked.Increment(ref started);
        await watcher.StartAsync();
        await watcher.DisposeAsync();

        AppendLine(path, TaskUseLine("toolu_after_dispose", "general-purpose", "post-dispose"));
        await Task.Delay(400);

        Assert.Equal(0, started);
    }

    // ── memory hardening (U1) ─────────────────────────────────────────────────────

    [Fact]
    public async Task SeenEntries_PrunedAfterRetentionWindow()
    {
        // Use a tiny retention window so we don't have to wait an hour for the test.
        var path = CreateSessionFile();
        await using var watcher = new ClaudeTranscriptWatcher(
            transcriptRoot: _tempRoot,
            pollInterval:   _pollFast,
            seenRetention:  TimeSpan.FromMilliseconds(50));

        var startedTcs   = new TaskCompletionSource<TaskInvocation>();
        var completedTcs = new TaskCompletionSource<TaskResult>();
        watcher.TaskStarted   += (_, t) => startedTcs.TrySetResult(t);
        watcher.TaskCompleted += (_, r) => completedTcs.TrySetResult(r);
        await watcher.StartAsync();

        AppendLine(path, TaskUseLine("toolu_age", "general-purpose", "tracked"));
        await startedTcs.Task.WaitAsync(_waitTimeout);
        AppendLine(path, TaskResultLine("toolu_age", "done"));
        await completedTcs.Task.WaitAsync(_waitTimeout);

        Assert.Equal(1, watcher.SeenStartsCount);
        Assert.Equal(1, watcher.SeenCompletionsCount);

        // Wait past the retention window, then trigger sweep manually so the test isn't
        // dependent on hitting the 60-poll periodic boundary.
        await Task.Delay(150);
        watcher.PruneOldSeenEntries();

        Assert.Equal(0, watcher.SeenStartsCount);
        Assert.Equal(0, watcher.SeenCompletionsCount);
    }

    [Fact]
    public async Task SeenEntries_RecentEntriesNotPruned()
    {
        var path = CreateSessionFile();
        await using var watcher = new ClaudeTranscriptWatcher(
            transcriptRoot: _tempRoot,
            pollInterval:   _pollFast,
            seenRetention:  TimeSpan.FromMinutes(1)); // generous retention

        var tcs = new TaskCompletionSource<TaskInvocation>();
        watcher.TaskStarted += (_, t) => tcs.TrySetResult(t);
        await watcher.StartAsync();

        AppendLine(path, TaskUseLine("toolu_recent", "general-purpose", "tracked"));
        await tcs.Task.WaitAsync(_waitTimeout);

        // Sweep immediately — entry is fresh, should NOT be pruned.
        watcher.PruneOldSeenEntries();
        Assert.Equal(1, watcher.SeenStartsCount);
    }

    [Fact]
    public async Task NonExistentTranscriptRoot_DoesNotThrow()
    {
        // Watching a directory that doesn't exist yet must be safe — Claude may not have
        // created its projects directory until the first session.
        var nonExistent = Path.Combine(Path.GetTempPath(), "definitely-not-real-" + Guid.NewGuid().ToString("N"));
        await using var watcher = new ClaudeTranscriptWatcher(nonExistent, _pollFast);
        await watcher.StartAsync();
        await Task.Delay(300);
        // Just reaching here without exception is the assertion.
    }
}
