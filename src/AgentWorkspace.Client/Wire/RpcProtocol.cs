using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AgentWorkspace.Client.Wire;

/// <summary>
/// Day-17 RPC frame protocol. Extends the Day-15 handshake (op codes 0x01..0x03 unchanged) with
/// request/response/push frames carrying JSON payloads. Day 18 will replace the codec with gRPC
/// while leaving the dispatcher / handler logic intact.
/// </summary>
/// <remarks>
/// Frame layout:
/// <code>
/// [magic 4B "AWT2"] [op 1B] [requestId u32 BE] [payloadLen u32 BE] [payload]
/// </code>
/// <para>
/// requestId is 0 for handshake and push frames; non-zero for RPC request/response correlation.
/// payload is UTF-8 JSON for every defined op code; raw byte payloads (e.g. PaneFramePush) are
/// also expressed as JSON with a base64 field. This keeps a single codec; the cost is paid back
/// when the gRPC swap on Day 18 replaces the wire format wholesale.
/// </para>
/// </remarks>
public static class RpcProtocol
{
    /// <summary>Wire magic for AWT v2 (RPC-capable).</summary>
    public static ReadOnlySpan<byte> Magic => "AWT2"u8;

    /// <summary>Server-advertised version string returned in <see cref="OpWelcome"/>.</summary>
    public const string ServerVersion = "awtd/0.2";

    /// <summary>Op codes — handshake (Day 15 layout, ported to AWT2 magic).</summary>
    public const byte OpHello = 0x01;
    public const byte OpWelcome = 0x02;
    public const byte OpReject = 0x03;

    /// <summary>Op codes — RPC request/response (paired by requestId).</summary>
    public const byte OpRequest = 0x10;
    public const byte OpResponse = 0x11;

    /// <summary>Op codes — server-pushed events (requestId == 0).</summary>
    public const byte OpPaneFramePush = 0x20;
    public const byte OpPaneExitedPush = 0x21;

    /// <summary>Reject reasons used during handshake.</summary>
    public const string RejectReasonBadFrame = "bad-frame";
    public const string RejectReasonBadToken = "bad-token";

    /// <summary>Frame header is fixed-size: magic(4) + op(1) + requestId(4) + payloadLen(4) = 13.</summary>
    public const int HeaderSize = 4 + 1 + 4 + 4;

    /// <summary>
    /// Maximum payload size per frame. Pane output frames are coalesced upstream to fit; gRPC will
    /// remove the limit on Day 18.
    /// </summary>
    public const int MaxPayloadSize = 4 * 1024 * 1024; // 4 MiB

    /// <summary>Writes a single framed message to <paramref name="stream"/>.</summary>
    public static async Task WriteFrameAsync(
        Stream stream,
        byte op,
        uint requestId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (payload.Length > MaxPayloadSize)
        {
            throw new ArgumentOutOfRangeException(nameof(payload),
                $"Frame payload must be ≤ {MaxPayloadSize} bytes (got {payload.Length}).");
        }

        var header = new byte[HeaderSize];
        Magic.CopyTo(header.AsSpan(0, 4));
        header[4] = op;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(5, 4), requestId);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(9, 4), (uint)payload.Length);

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (!payload.IsEmpty)
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Convenience overload for ASCII string payloads (used by handshake frames).</summary>
    public static Task WriteStringFrameAsync(
        Stream stream,
        byte op,
        uint requestId,
        string text,
        CancellationToken cancellationToken)
        => WriteFrameAsync(stream, op, requestId, Encoding.UTF8.GetBytes(text), cancellationToken);

    /// <summary>Reads a single framed message from <paramref name="stream"/>.</summary>
    public static async Task<RpcFrame> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = new byte[HeaderSize];
        await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false);

        if (!header.AsSpan(0, 4).SequenceEqual(Magic))
        {
            throw new InvalidDataException("Bad RPC frame magic.");
        }

        byte op = header[4];
        uint requestId = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(5, 4));
        uint payloadLen = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(9, 4));

        if (payloadLen > MaxPayloadSize)
        {
            throw new InvalidDataException($"RPC payload {payloadLen}B exceeds max {MaxPayloadSize}B.");
        }

        byte[] payload = payloadLen == 0 ? Array.Empty<byte>() : new byte[payloadLen];
        if (payloadLen > 0)
        {
            await ReadExactAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        }

        return new RpcFrame(op, requestId, payload);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                throw new EndOfStreamException(
                    $"Stream closed after {read}/{buffer.Length} bytes during RPC read.");
            }
            read += n;
        }
    }
}

/// <summary>
/// One decoded frame off the wire. <see cref="Payload"/> is owned by the caller.
/// </summary>
public readonly record struct RpcFrame(byte Op, uint RequestId, byte[] Payload)
{
    public string PayloadAsUtf8() => Encoding.UTF8.GetString(Payload);
}
