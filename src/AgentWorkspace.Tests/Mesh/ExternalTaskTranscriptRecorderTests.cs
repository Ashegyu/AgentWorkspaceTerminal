using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Agents.Claude;
using AgentWorkspace.App.Wpf.Mesh;
using AgentWorkspace.Core.Transcripts;

namespace AgentWorkspace.Tests.Mesh;

/// <summary>
/// Behavioural tests for <see cref="ExternalTaskTranscriptRecorder"/>. Each test uses
/// an isolated temp directory so the real <c>%LOCALAPPDATA%</c> transcripts folder is
/// untouched. The recorder is exercised via a sink factory that points
/// <see cref="TranscriptSink.Open"/> at <see cref="_tempDir"/>.
/// </summary>
public sealed class ExternalTaskTranscriptRecorderTests : IDisposable
{
    private readonly string _tempDir;

    public ExternalTaskTranscriptRecorderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "external-recorder-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private ExternalTaskTranscriptRecorder NewRecorder() =>
        new((id, parent) => TranscriptSink.Open(
            sessionId:         id,
            provider:          ExternalTaskTranscriptRecorder.ProviderLabel,
            parentSessionId:   parent,
            directoryOverride: _tempDir));

    /// <summary>
    /// Reads a file that another process / sink may currently hold for writing.
    /// <c>File.ReadAllLinesAsync</c> uses default <c>FileShare.Read</c> which conflicts
    /// with a live <c>FileAccess.Write</c> handle (the writer's sharing must permit our
    /// access AND ours must permit the writer's). Opening explicitly with
    /// <c>FileShare.ReadWrite | FileShare.Delete</c> lets us read without disturbing the writer.
    /// </summary>
    private static async Task<string[]> ReadAllLinesSharedAsync(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        var content = await reader.ReadToEndAsync();
        return content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                      .Select(l => l.TrimEnd('\r'))
                      .ToArray();
    }

    private static async Task<string> ReadAllTextSharedAsync(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        return await reader.ReadToEndAsync();
    }

    /// <summary>
    /// Polls <paramref name="path"/> until the predicate accepts the parsed line array or
    /// the deadline expires. Replaces fixed <c>Task.Delay</c> waits — those are flaky on
    /// busy CI under antivirus scanning. Deadline-based polling is self-timing: success
    /// is observed as soon as it happens, failure raises a TimeoutException with diagnostics.
    /// </summary>
    private static async Task<string[]> WaitForLinesAsync(
        string path,
        Func<string[], bool> predicate,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(3));
        string[] lines = Array.Empty<string>();
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(path))
                {
                    lines = await ReadAllLinesSharedAsync(path);
                    if (predicate(lines)) return lines;
                }
            }
            catch (IOException) { /* file mid-flush — try again */ }
            await Task.Delay(10);
        }
        throw new TimeoutException(
            $"WaitForLinesAsync deadline exceeded for {path}. Last seen {lines.Length} lines.");
    }

    private static async Task<string> WaitForTextAsync(
        string path,
        Func<string, bool> predicate,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(3));
        string text = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(path))
                {
                    text = await ReadAllTextSharedAsync(path);
                    if (predicate(text)) return text;
                }
            }
            catch (IOException) { /* file mid-flush — try again */ }
            await Task.Delay(10);
        }
        throw new TimeoutException(
            $"WaitForTextAsync deadline exceeded for {path}. Last content length: {text.Length}.");
    }

    private static TaskInvocation Inv(string toolUseId, string prompt, string subType = "general-purpose") =>
        new(toolUseId, subType, prompt, DateTimeOffset.UtcNow);

    private static TaskResult Res(string toolUseId, string output, bool isError = false) =>
        new(toolUseId, output, isError, DateTimeOffset.UtcNow);

    // ── happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task TaskStarted_OpensSink_AndWritesUserMessage()
    {
        await using var rec = NewRecorder();
        var childId = AgentSessionId.New();
        await rec.OnTaskStartedAsync(Inv("toolu_1", "scan src/"), childId, rootSessionId: null);

        Assert.Equal(1, rec.OpenSinkCount);

        var path = Path.Combine(_tempDir, $"{childId}.jsonl");

        // Deadline-poll for the user-message line to land. The session_start header is
        // written by TranscriptSink.Open synchronously, but the user-message Append flows
        // through async StreamWriter even with AutoFlush — observation latency is normally
        // sub-millisecond but can stretch on a busy CI runner under AV scanning.
        var lines = await WaitForLinesAsync(path, ls => ls.Length >= 2);

        using var header = JsonDocument.Parse(lines[0]);
        Assert.Equal("session_start", header.RootElement.GetProperty("type").GetString());

        using var msg = JsonDocument.Parse(lines[1]);
        Assert.Equal("message", msg.RootElement.GetProperty("type").GetString());
        Assert.Equal("user",    msg.RootElement.GetProperty("role").GetString());
        var text = msg.RootElement.GetProperty("text").GetString() ?? "";
        Assert.Contains("subagent_type=general-purpose", text);
        Assert.Contains("scan src/", text);
    }

    [Fact]
    public async Task TaskCompleted_WritesAssistantMessage_DoneEvent_AndClosesSink()
    {
        await using var rec = NewRecorder();
        var childId = AgentSessionId.New();

        await rec.OnTaskStartedAsync(Inv("toolu_2", "find logs"), childId, rootSessionId: null);
        await rec.OnTaskCompletedAsync(Res("toolu_2", "found 3 log files: app.log, err.log, sys.log"));

        Assert.Equal(0, rec.OpenSinkCount);

        var path = Path.Combine(_tempDir, $"{childId}.jsonl");
        var lines = await ReadAllLinesSharedAsync(path);

        // Expect: session_start, user message, assistant message, done event.
        Assert.Equal(4, lines.Length);
        using var done = JsonDocument.Parse(lines[3]);
        Assert.Equal("done", done.RootElement.GetProperty("type").GetString());
        Assert.Equal(0,      done.RootElement.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task TaskCompleted_WithIsError_WritesErrorEvent()
    {
        await using var rec = NewRecorder();
        var childId = AgentSessionId.New();

        await rec.OnTaskStartedAsync(Inv("toolu_3", "broken thing"), childId, rootSessionId: null);
        await rec.OnTaskCompletedAsync(Res("toolu_3", "permission denied", isError: true));

        var path = Path.Combine(_tempDir, $"{childId}.jsonl");
        var lines = await ReadAllLinesSharedAsync(path);

        Assert.Equal(4, lines.Length);
        using var err = JsonDocument.Parse(lines[3]);
        Assert.Equal("error", err.RootElement.GetProperty("type").GetString());
    }

    // ── lineage ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task TaskStarted_RecordsParentSessionIdInHeader()
    {
        await using var rec = NewRecorder();
        var childId  = AgentSessionId.New();
        var rootId   = AgentSessionId.New();

        await rec.OnTaskStartedAsync(Inv("toolu_4", "nested"), childId, rootId);

        var path = Path.Combine(_tempDir, $"{childId}.jsonl");
        var firstLine = (await ReadAllLinesSharedAsync(path))[0];
        using var header = JsonDocument.Parse(firstLine);
        Assert.Equal(rootId.ToString(), header.RootElement.GetProperty("parent_session_id").GetString());
    }

    // ── redaction ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task PersistedPrompt_IsRedacted()
    {
        // TranscriptSink applies redaction internally — verify the recorder doesn't bypass it.
        await using var rec = NewRecorder();
        var childId = AgentSessionId.New();

        await rec.OnTaskStartedAsync(
            Inv("toolu_5", "deploy with OPENAI_API_KEY=sk-foobar123"),
            childId,
            rootSessionId: null);

        var path = Path.Combine(_tempDir, $"{childId}.jsonl");
        // Deadline-poll until the redacted user message lands — eliminates fixed-delay flake.
        var content = await WaitForTextAsync(path, t => t.Contains("OPENAI_API_KEY=[REDACTED]"));
        Assert.DoesNotContain("sk-foobar123", content);
    }

    [Fact]
    public async Task PersistedOutput_IsRedacted()
    {
        await using var rec = NewRecorder();
        var childId = AgentSessionId.New();

        await rec.OnTaskStartedAsync(Inv("toolu_6", "scan"), childId, rootSessionId: null);
        await rec.OnTaskCompletedAsync(Res("toolu_6", "found at C:\\Users\\alice\\config.json"));

        var path = Path.Combine(_tempDir, $"{childId}.jsonl");
        var content = await ReadAllTextSharedAsync(path);
        Assert.Contains(@"C:\\Users\\[USER]", content); // JSON-escaped backslash
        Assert.DoesNotContain("alice", content);
    }

    // ── dedup / orphan / disposal ─────────────────────────────────────────────

    [Fact]
    public async Task DuplicateStart_DoesNotLeakSink()
    {
        await using var rec = NewRecorder();
        var childId = AgentSessionId.New();

        await rec.OnTaskStartedAsync(Inv("toolu_dup", "first"), childId, rootSessionId: null);
        await rec.OnTaskStartedAsync(Inv("toolu_dup", "second"), childId, rootSessionId: null);

        Assert.Equal(1, rec.OpenSinkCount);
    }

    [Fact]
    public async Task OrphanCompletion_NoStartObserved_IsNoOp()
    {
        await using var rec = NewRecorder();
        // No matching OnTaskStartedAsync — completion arrives orphaned.
        await rec.OnTaskCompletedAsync(Res("toolu_orphan", "stale result"));
        Assert.Equal(0, rec.OpenSinkCount);
        // No file should exist for this id.
        Assert.Empty(Directory.GetFiles(_tempDir));
    }

    [Fact]
    public async Task Dispose_ClosesOpenSinks()
    {
        var rec = NewRecorder();
        var childId = AgentSessionId.New();
        await rec.OnTaskStartedAsync(Inv("toolu_open", "abandoned"), childId, rootSessionId: null);
        Assert.Equal(1, rec.OpenSinkCount);

        await rec.DisposeAsync();
        Assert.Equal(0, rec.OpenSinkCount);

        // Disposed sink should release the file lock — we can re-open it.
        var path = Path.Combine(_tempDir, $"{childId}.jsonl");
        using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.True(fs.Length > 0);
    }

    [Fact]
    public async Task Dispose_IsIdempotent()
    {
        var rec = NewRecorder();
        await rec.DisposeAsync();
        await rec.DisposeAsync(); // second call should not throw
    }

    [Fact]
    public async Task NullTask_Throws()
    {
        await using var rec = NewRecorder();
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await rec.OnTaskStartedAsync(null!, AgentSessionId.New(), null));
    }

    [Fact]
    public async Task ProviderLabel_AppearsInSessionStartHeader()
    {
        // Pin the provider label so analytics consumers depending on the constant
        // notice via failing test if it ever drifts.
        await using var rec = NewRecorder();
        var childId = AgentSessionId.New();
        await rec.OnTaskStartedAsync(Inv("toolu_provider", "irrelevant"), childId, rootSessionId: null);

        var path = Path.Combine(_tempDir, $"{childId}.jsonl");
        var lines = await WaitForLinesAsync(path, ls => ls.Length >= 1);
        using var header = JsonDocument.Parse(lines[0]);
        Assert.Equal(
            ExternalTaskTranscriptRecorder.ProviderLabel,
            header.RootElement.GetProperty("provider").GetString());
    }
}
