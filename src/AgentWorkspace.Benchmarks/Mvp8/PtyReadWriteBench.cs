using System;
using System.Buffers;
using System.Threading;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Pty;
using AgentWorkspace.App.Wpf;
using BenchmarkDotNet.Attributes;

namespace AgentWorkspace.Benchmarks.Mvp8;

/// <summary>
/// ADR-008 #2 — ConPTY read → client write p95 ≤ 5 ms.
/// Measures one full host-side cycle of the read loop in
/// <c>PseudoConsoleProcess.ReadAsync</c>: rent buffer → produce
/// <see cref="PtyChunk"/> → wrap into wire <c>Envelope.Output</c> JSON line →
/// return buffer. The actual NamedPipe write is IO-bound and is not modelled here;
/// the 5 ms ceiling assumes the host transform itself stays well below 1 ms so the
/// remaining budget belongs to pipe + dispatch.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class PtyReadWriteBench
{
    private readonly PaneId _paneId = PaneId.New();
    private long _seq;

    private byte[] _src64    = new byte[64];
    private byte[] _src8K    = new byte[8 * 1024];
    private byte[] _src64K   = new byte[64 * 1024];

    [GlobalSetup]
    public void Setup()
    {
        new Random(42).NextBytes(_src64);
        new Random(43).NextBytes(_src8K);
        new Random(44).NextBytes(_src64K);
    }

    [Benchmark(Description = "PTY read→write cycle  64 B (ADR-008 #2)")]
    public string Cycle_64B() => OneCycle(_src64);

    [Benchmark(Description = "PTY read→write cycle  8 KB (ADR-008 #2)")]
    public string Cycle_8KB() => OneCycle(_src8K);

    [Benchmark(Description = "PTY read→write cycle 64 KB (ADR-008 #2)")]
    public string Cycle_64KB() => OneCycle(_src64K);

    private string OneCycle(byte[] src)
    {
        // Mirrors PseudoConsoleProcess.ReadAsync: rent → fill → wrap → return.
        var rented = ArrayPool<byte>.Shared.Rent(src.Length);
        try
        {
            Buffer.BlockCopy(src, 0, rented, 0, src.Length);
            var seq   = Interlocked.Increment(ref _seq);
            var chunk = new PtyChunk(
                new ReadOnlyMemory<byte>(rented, 0, src.Length),
                seq,
                DateTimeOffset.UtcNow);
            return Envelope.Output(_paneId, chunk.Data.Span);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
