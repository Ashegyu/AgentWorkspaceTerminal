using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AgentWorkspace.Daemon.Channels;

/// <summary>
/// Day 15 control-channel handshake. The full gRPC stack arrives Day 18; for now we just need a
/// stable, length-prefixed frame so daemon and client agree on the bearer-token contract.
///
/// Frame layout: [magic 4B "AWT1"] [op 1B] [payloadLen u16 BE] [payload].
/// Op codes:
///   0x01 = client → server  HELLO   (payload: ASCII bearer token)
///   0x02 = server → client  WELCOME (payload: ASCII server-version, e.g. "awtd/0.1")
///   0x03 = server → client  REJECT  (payload: ASCII reason)
/// </summary>
internal static class HandshakeProtocol
{
    public static ReadOnlySpan<byte> Magic => "AWT1"u8;

    public const byte OpHello = 0x01;
    public const byte OpWelcome = 0x02;
    public const byte OpReject = 0x03;

    public const string ServerVersion = "awtd/0.1";
    public const string RejectReasonBadToken = "bad-token";
    public const string RejectReasonBadFrame = "bad-frame";

    public const int HeaderSize = 4 + 1 + 2;
    public const int MaxPayloadSize = 4 * 1024;

    public static async Task WriteFrameAsync(
        Stream stream,
        byte op,
        ReadOnlyMemory<byte> payload,
        CancellationToken ct)
    {
        if (payload.Length > MaxPayloadSize)
        {
            throw new ArgumentOutOfRangeException(nameof(payload),
                $"Frame payload must be ≤ {MaxPayloadSize} bytes.");
        }

        var header = new byte[HeaderSize];
        Magic.CopyTo(header.AsSpan(0, 4));
        header[4] = op;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(5, 2), (ushort)payload.Length);

        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        if (!payload.IsEmpty)
        {
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        }
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static Task WriteStringFrameAsync(Stream stream, byte op, string text, CancellationToken ct) =>
        WriteFrameAsync(stream, op, Encoding.ASCII.GetBytes(text), ct);

    public static async Task<HandshakeFrame> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        await ReadExactAsync(stream, header, ct).ConfigureAwait(false);

        if (!header.AsSpan(0, 4).SequenceEqual(Magic))
        {
            throw new InvalidDataException("Bad handshake magic.");
        }

        var op = header[4];
        var payloadLen = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(5, 2));

        if (payloadLen > MaxPayloadSize)
        {
            throw new InvalidDataException($"Handshake payload {payloadLen}B exceeds max {MaxPayloadSize}B.");
        }

        var payload = payloadLen == 0 ? Array.Empty<byte>() : new byte[payloadLen];
        if (payloadLen > 0)
        {
            await ReadExactAsync(stream, payload, ct).ConfigureAwait(false);
        }

        return new HandshakeFrame(op, payload);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                throw new EndOfStreamException(
                    $"Stream closed after {read}/{buffer.Length} bytes during handshake.");
            }
            read += n;
        }
    }
}

internal readonly record struct HandshakeFrame(byte Op, byte[] Payload)
{
    public string PayloadAsAscii() => Encoding.ASCII.GetString(Payload);
}
