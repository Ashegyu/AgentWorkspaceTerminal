using System;
using System.Collections.Generic;
using System.Threading;
using AgentWorkspace.Abstractions.Ids;

namespace AgentWorkspace.Abstractions.Channels;

/// <summary>
/// Streaming surface that delivers raw pane bytes from the daemon to a client. MVP-1~2 wires
/// this in-process; MVP-3 swaps in a Named Pipe streaming implementation (ADR-003).
/// </summary>
/// <remarks>
/// Each call to <see cref="SubscribeAsync"/> opens a *new* subscription. The implementation owns
/// the lifetime of the byte buffers backing each <see cref="PaneFrame"/>; subscribers must not
/// retain spans past the iteration step.
/// </remarks>
public interface IDataChannel : IAsyncDisposable
{
    IAsyncEnumerable<PaneFrame> SubscribeAsync(PaneId pane, CancellationToken cancellationToken);
}

/// <summary>
/// A frame of bytes emitted by a pane.
/// <see cref="Bytes"/> may reference an array-pool rented buffer; consumers should copy if they
/// need to retain the data.
/// </summary>
public readonly record struct PaneFrame(PaneId Pane, ReadOnlyMemory<byte> Bytes, long Sequence);
