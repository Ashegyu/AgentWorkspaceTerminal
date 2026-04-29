using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Agents;

namespace AgentWorkspace.Core.Transcripts;

/// <summary>
/// Appends <see cref="AgentEvent"/> instances as newline-delimited JSON to
/// <c>%LOCALAPPDATA%\AgentWorkspace\transcripts\{sessionId}.jsonl</c>.
/// Not thread-safe: callers must not invoke <see cref="AppendAsync"/> concurrently.
/// </summary>
public sealed class TranscriptSink : IAsyncDisposable
{
    private readonly StreamWriter _writer;

    private TranscriptSink(StreamWriter writer) => _writer = writer;

    public static TranscriptSink Open(AgentSessionId sessionId)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentWorkspace", "transcripts");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{sessionId}.jsonl");
        var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
            bufferSize: 4096, useAsync: true);
        return new TranscriptSink(new StreamWriter(fs, Encoding.UTF8) { AutoFlush = true });
    }

    public ValueTask AppendAsync(AgentEvent evt, CancellationToken cancellationToken = default)
    {
        var line = Serialize(evt);
        return new ValueTask(_writer.WriteLineAsync(line.AsMemory(), cancellationToken));
    }

    public ValueTask DisposeAsync() => _writer.DisposeAsync();

    private static string Serialize(AgentEvent evt)
    {
        var ts = DateTimeOffset.UtcNow.ToString("O");
        return evt switch
        {
            AgentMessageEvent m => Js(new
            {
                type = "message",
                role = m.Role,
                text = m.Text,
                ts,
            }),
            ActionRequestEvent a => Js(new
            {
                type = "action",
                id = a.ActionId,
                actionType = a.Type,
                description = a.Description,
                ts,
            }),
            AgentDoneEvent d => Js(new
            {
                type = "done",
                exitCode = d.ExitCode,
                summary = d.Summary,
                ts,
            }),
            AgentErrorEvent e => Js(new
            {
                type = "error",
                message = e.Message,
                ts,
            }),
            _ => Js(new { type = "unknown", ts }),
        };
    }

    private static string Js(object obj) => JsonSerializer.Serialize(obj);
}
