using System.Collections.Generic;
using System.Text;
using BenchmarkDotNet.Attributes;

namespace AgentWorkspace.Benchmarks;

/// <summary>
/// Hot-path quoting for ConPTY child spawns. Build is called once per pane start, but it also
/// shows up indirectly in env-block construction; so allocation here matters.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class CommandLineBench
{
    private static readonly string[] Args = { "/d", "/c", "ping -n 1 127.0.0.1" };
    private static readonly Dictionary<string, string> Env = new()
    {
        ["PATH"] = @"C:\Windows\System32;C:\Program Files\PowerShell\7",
        ["USERPROFILE"] = @"C:\Users\dev",
        ["TEMP"] = @"C:\Users\dev\AppData\Local\Temp",
        ["TMP"] = @"C:\Users\dev\AppData\Local\Temp",
        ["HOME"] = @"C:\Users\dev",
        ["LANG"] = "ko_KR.UTF-8",
        ["TERM"] = "xterm-256color",
    };

    [Benchmark(Description = "CommandLine.Build('cmd', 3 args)")]
    public string BuildSimple()
    {
        return AgentWorkspace.ConPTY.Native.CommandLine.Build("cmd.exe", Args);
    }

    [Benchmark(Description = "CommandLine.BuildEnvironmentBlock(7 keys)")]
    public string BuildEnv()
    {
        return AgentWorkspace.ConPTY.Native.CommandLine.BuildEnvironmentBlock(Env);
    }

    [Benchmark(Description = "AppendArgument hot loop ×100", Baseline = false)]
    public int AppendHot()
    {
        var sb = new StringBuilder(2048);
        for (int i = 0; i < 100; i++)
        {
            AgentWorkspace.ConPTY.Native.CommandLine.AppendArgument(sb, "with space and \"quote\"");
            sb.Append(' ');
        }
        return sb.Length;
    }
}
