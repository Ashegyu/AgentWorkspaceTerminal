// AgentWorkspace.Benchmarks
//
// Entry point for BenchmarkDotNet. The runner picks up [Benchmark]-annotated classes from this
// assembly. With no args it lists them; pass a filter to run a subset.
//
// Examples:
//   dotnet run -c Release --project src/AgentWorkspace.Benchmarks -- --filter '*'
//   dotnet run -c Release --project src/AgentWorkspace.Benchmarks -- --filter '*Layout*'
//   dotnet run -c Release --project src/AgentWorkspace.Benchmarks -- --job short --filter '*'

using System.Reflection;
using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(Assembly.GetExecutingAssembly()).Run(args);
