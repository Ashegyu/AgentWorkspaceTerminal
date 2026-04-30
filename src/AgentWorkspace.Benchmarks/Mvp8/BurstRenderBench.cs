using System;
using System.Buffers;
using System.Threading;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Pty;
using AgentWorkspace.App.Wpf;
using BenchmarkDotNet.Attributes;

namespace AgentWorkspace.Benchmarks.Mvp8;

/// <summary>
/// ADR-008 #5 — 1 MB burst render ≤ 250 ms.
/// Simulates a build log / file dump arriving in a single tight burst by chopping
/// a pre-filled 1 MB buffer into N PTY-sized chunks, running each through the
/// same host-side cycle as <see cref="PtyReadWriteBench"/>. Two chunk sizes are
/// measured because chunk size dominates total wall time:
///   • 8 KB × 128 cycles  (typical ReadAsync rent size)
///   • 64 KB × 16 cycles  (large-burst tile, fewer envelope/JSON costs)
/// Wall clock for one bench iteration ≈ end-to-end host-side time to publish 1 MB.
/// JSON wire write to the pipe is IO-bound and is not modelled here.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class BurstRenderBench
{
    private const int Total = 1024 * 1024;

    private readonly PaneId _paneId = PaneId.New();
    private long _seq;
    private byte[] _src = new byte[Total];

    [GlobalSetup]
    public void Setup() => new Random(57).NextBytes(_src);

    [Benchmark(Description = "Burst 1MB → 128× 8KB cycles (ADR-008 #5)")]
    public string Burst_8KB_Chunks() => RunBurst(chunkSize: 8 * 1024);

    [Benchmark(Description = "Burst 1MB → 16× 64KB cycles (ADR-008 #5)")]
    public string Burst_64KB_Chunks() => RunBurst(chunkSize: 64 * 1024);

    private string RunBurst(int chunkSize)
    {
        var chunks = Total / chunkSize;
        var last   = "";
        for (var c = 0; c < chunks; c++)
        {
            var rented = ArrayPool<byte>.Shared.Rent(chunkSize);
            try
            {
                Buffer.BlockCopy(_src, c * chunkSize, rented, 0, chunkSize);
                var seq   = Interlocked.Increment(ref _seq);
                var chunk = new PtyChunk(
                    new ReadOnlyMemory<byte>(rented, 0, chunkSize),
                    seq,
                    DateTimeOffset.UtcNow);
                last = Envelope.Output(_paneId, chunk.Data.Span);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
        return last;
    }
}
