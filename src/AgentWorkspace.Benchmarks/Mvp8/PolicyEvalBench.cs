using AgentWorkspace.Abstractions.Policy;
using AgentWorkspace.Core.Policy;
using BenchmarkDotNet.Attributes;

namespace AgentWorkspace.Benchmarks.Mvp8;

/// <summary>
/// ADR-008 #8 — PolicyEngine hot path under the default 50-rule blacklist.
/// Measures three representative shapes of <see cref="ExecuteCommand"/> evaluation
/// against <see cref="Blacklists.SafeDev"/> (50 regex rules):
///   • Worst-case miss — input falls through all 50 rules to level-based fallback.
///   • Early hit       — first blacklist rule matches (recursive root rm).
///   • Late hit        — last blacklist rule matches (twine upload).
/// PolicyEngine has no documented numeric ceiling in ADR-008; the baseline.json metric
/// <c>policyEval50RuleNs</c> is captured here so CI can flag any regression > 1.5×
/// of the worst-case (miss) median.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class PolicyEvalBench
{
    private readonly PolicyEngine _engine = new();
    private readonly PolicyContext _ctx   = new(
        WorkspaceRoot: @"C:\workspace",
        Level:         PolicyLevel.SafeDev,
        AgentName:     "BenchAgent");

    // Long-ish line with no blacklist hit — exercises every rule's regex.
    private readonly ExecuteCommand _miss = new(
        Cmd:  "dotnet",
        Args: new[] { "build", "src/AgentWorkspace.Benchmarks/AgentWorkspace.Benchmarks.csproj", "-c", "Release" });

    // Hits the very first rule (recursive delete of /).
    private readonly ExecuteCommand _earlyHit = new(
        Cmd:  "rm",
        Args: new[] { "-rf", "/" });

    // Hits the last rule (twine upload).
    private readonly ExecuteCommand _lateHit = new(
        Cmd:  "twine",
        Args: new[] { "upload", "dist/*" });

    [Benchmark(Description = "PolicyEngine.Evaluate — 50-rule miss (worst case)")]
    public PolicyDecision Evaluate_Miss()
        => _engine.EvaluateAsync(_miss, _ctx).GetAwaiter().GetResult();

    [Benchmark(Description = "PolicyEngine.Evaluate — first rule hit")]
    public PolicyDecision Evaluate_EarlyHit()
        => _engine.EvaluateAsync(_earlyHit, _ctx).GetAwaiter().GetResult();

    [Benchmark(Description = "PolicyEngine.Evaluate — last rule hit")]
    public PolicyDecision Evaluate_LateHit()
        => _engine.EvaluateAsync(_lateHit, _ctx).GetAwaiter().GetResult();
}
