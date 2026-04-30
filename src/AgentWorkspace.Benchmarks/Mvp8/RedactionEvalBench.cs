using AgentWorkspace.Core.Redaction;
using BenchmarkDotNet.Attributes;

namespace AgentWorkspace.Benchmarks.Mvp8;

/// <summary>
/// ADR-008 #9 — Redaction hot path under the default 14-rule rule set.
/// Measures <see cref="RegexRedactionEngine.Redact"/> against
/// <see cref="RegexRedactionEngine.DefaultRules"/> (14 regex substitutions) on three
/// representative payload shapes:
///   • Plain log line — every rule misses, exercises full chain.
///   • Single-token hit — one OPENAI_API_KEY assignment.
///   • Bulk text — 1KB of repeated log lines, every rule misses (chain × N).
/// As with PolicyEvalBench the ADR has no hard ceiling; the median is captured in
/// baseline.json so CI flags >1.5× regressions.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class RedactionEvalBench
{
    private readonly RegexRedactionEngine _engine = new();

    private const string PlainLine =
        "[2026-04-30T10:00:00Z] dotnet build completed in 4.2s — 0 errors, 0 warnings.";

    private const string TokenLine =
        "config: OPENAI_API_KEY=sk-not-a-real-secret-1234567890ABCDEFGHIJ // never log me";

    private static readonly string BulkPlain = string.Concat(
        System.Linq.Enumerable.Repeat(PlainLine + "\n", 1024 / PlainLine.Length + 1));

    [Benchmark(Description = "Redact — 14-rule miss on a single log line (worst per-line)")]
    public string Redact_PlainLine() => _engine.Redact(PlainLine);

    [Benchmark(Description = "Redact — single OPENAI_API_KEY token hit")]
    public string Redact_TokenHit() => _engine.Redact(TokenLine);

    [Benchmark(Description = "Redact — ~1KB bulk plain text, all 14 rules miss")]
    public string Redact_Bulk1Kb() => _engine.Redact(BulkPlain);
}
