using System;
using System.Buffers;

namespace AgentWorkspace.Abstractions.Pty;

/// <summary>
/// One unit of pseudo-terminal output as observed by the runtime.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Data"/> is rented from <see cref="ArrayPool{Byte}.Shared"/>. The receiver of the
/// chunk is responsible for either consuming the bytes synchronously inside the read loop, or for
/// returning the buffer once it has been copied somewhere stable. The runtime exposes chunks
/// through an <see cref="System.Collections.Generic.IAsyncEnumerable{T}"/>; for simple consumers,
/// awaiting and copying inside the <c>foreach</c> body is sufficient.
/// </para>
/// <para>
/// <see cref="SequenceId"/> is a monotonically increasing counter scoped to the producing pane and
/// is intended for diagnostics and frame ordering. <see cref="Timestamp"/> is captured from
/// <see cref="DateTimeOffset.UtcNow"/> at read time.
/// </para>
/// </remarks>
public readonly record struct PtyChunk(
    ReadOnlyMemory<byte> Data,
    long SequenceId,
    DateTimeOffset Timestamp);
