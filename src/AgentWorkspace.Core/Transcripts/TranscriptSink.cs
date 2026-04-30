using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Abstractions.Redaction;
using AgentWorkspace.Core.Redaction;

namespace AgentWorkspace.Core.Transcripts;

/// <summary>
/// Appends <see cref="AgentEvent"/> instances as newline-delimited JSON to
/// <c>%LOCALAPPDATA%\AgentWorkspace\transcripts\{sessionId}.jsonl</c>.
/// Free-form text fields are passed through <see cref="IRedactionEngine"/> before
/// serialization so transcripts on disk never carry secrets listed in DESIGN.md §9.3.
/// Not thread-safe: callers must not invoke <see cref="AppendAsync"/> concurrently.
/// </summary>
public sealed class TranscriptSink : IAsyncDisposable
{
    private readonly StreamWriter _writer;
    private readonly IRedactionEngine _redaction;

    private TranscriptSink(StreamWriter writer, IRedactionEngine redaction)
    {
        _writer    = writer;
        _redaction = redaction;
    }

    public static TranscriptSink Open(AgentSessionId sessionId, IRedactionEngine? redaction = null)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentWorkspace", "transcripts");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{sessionId}.jsonl");
        var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
            bufferSize: 4096, useAsync: true);
        return new TranscriptSink(
            new StreamWriter(fs, Encoding.UTF8) { AutoFlush = true },
            redaction ?? new RegexRedactionEngine());
    }

    public ValueTask AppendAsync(AgentEvent evt, CancellationToken cancellationToken = default)
    {
        var line = Serialize(evt, _redaction);
        return new ValueTask(_writer.WriteLineAsync(line.AsMemory(), cancellationToken));
    }

    public ValueTask DisposeAsync() => _writer.DisposeAsync();

    internal static string Serialize(AgentEvent evt, IRedactionEngine r)
    {
        var ts = DateTimeOffset.UtcNow.ToString("O");
        return evt switch
        {
            AgentMessageEvent m => Js(new
            {
                type = "message",
                role = m.Role,
                text = r.Redact(m.Text),
                ts,
            }),
            ActionRequestEvent a => Js(new
            {
                type        = "action",
                id          = a.ActionId,
                actionType  = a.Type,
                description = r.Redact(a.Description),
                ts,
            }),
            AgentDoneEvent d => Js(new
            {
                type     = "done",
                exitCode = d.ExitCode,
                summary  = d.Summary is null ? null : r.Redact(d.Summary),
                ts,
            }),
            AgentErrorEvent e => Js(new
            {
                type    = "error",
                message = r.Redact(e.Message),
                ts,
            }),
            _ => Js(new { type = "unknown", ts }),
        };
    }

    private static string Js(object obj) => JsonSerializer.Serialize(obj);
}
