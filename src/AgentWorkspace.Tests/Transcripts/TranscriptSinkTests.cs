using System;
using System.IO;
using System.Text.Json;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Core.Transcripts;

namespace AgentWorkspace.Tests.Transcripts;

/// <summary>
/// Verifies that <see cref="TranscriptSink.Open"/> writes a well-formed
/// <c>session_start</c> header containing the expected <c>provider</c> and
/// <c>model</c> fields to the JSONL file.
/// </summary>
public sealed class TranscriptSinkTests
{
    [Fact]
    public async Task Open_WritesSessionStartHeader_WithProviderAndModel()
    {
        var sessionId = AgentSessionId.New();

        // Derive the expected file path before opening so cleanup can always run.
        var filePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentWorkspace", "transcripts",
            $"{sessionId}.jsonl");

        try
        {
            // Open and dispose immediately — the header is flushed synchronously by Open()
            // so disposal is only needed to release the file handle before reading.
            var sink = TranscriptSink.Open(
                sessionId,
                provider: "Claude",
                model: "claude-opus-4-5");
            await sink.DisposeAsync();

            Assert.True(File.Exists(filePath), $"Transcript file not found: {filePath}");

            var lines = await File.ReadAllLinesAsync(filePath);
            Assert.NotEmpty(lines);

            var header = JsonDocument.Parse(lines[0]).RootElement;
            Assert.Equal("session_start",   header.GetProperty("type").GetString());
            Assert.Equal("Claude",           header.GetProperty("provider").GetString());
            Assert.Equal("claude-opus-4-5", header.GetProperty("model").GetString());
            Assert.True(
                header.TryGetProperty("ts", out var tsProp) &&
                tsProp.ValueKind == JsonValueKind.String &&
                !string.IsNullOrEmpty(tsProp.GetString()),
                "session_start header is missing a non-empty 'ts' field");
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
    }
}
