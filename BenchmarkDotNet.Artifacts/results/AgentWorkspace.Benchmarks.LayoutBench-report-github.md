```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8246/25H2/2025Update/HudsonValley2)
Intel Core i7-10700K CPU 3.80GHz (Max: 3.79GHz), 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.103
  [Host]     : .NET 10.0.3 (10.0.3, 10.0.326.7603), X64 RyuJIT x86-64-v3
  Job-MJLQTR : .NET 10.0.3 (10.0.3, 10.0.326.7603), X64 RyuJIT x86-64-v3
  ShortRun   : .NET 10.0.3 (10.0.3, 10.0.326.7603), X64 RyuJIT x86-64-v3

InvocationCount=1  UnrollFactor=1  WarmupCount=3  

```
| Method                            | Job        | IterationCount | LaunchCount | InitialPaneCount | Mean      | Error      | StdDev    | Allocated |
|---------------------------------- |----------- |--------------- |------------ |----------------- |----------:|-----------:|----------:|----------:|
| **&#39;Split focused pane horizontally&#39;** | **Job-MJLQTR** | **5**              | **Default**     | **1**                |  **3.550 μs** |  **1.7097 μs** | **0.2646 μs** |     **304 B** |
| &#39;Split focused pane horizontally&#39; | ShortRun   | 3              | 1           | 1                |  4.033 μs |  7.3731 μs | 0.4041 μs |     304 B |
| **&#39;Split focused pane horizontally&#39;** | **Job-MJLQTR** | **5**              | **Default**     | **4**                |  **6.660 μs** |  **5.6893 μs** | **1.4775 μs** |     **496 B** |
| &#39;Split focused pane horizontally&#39; | ShortRun   | 3              | 1           | 4                | 12.567 μs | 50.0402 μs | 2.7429 μs |     496 B |
| **&#39;Split focused pane horizontally&#39;** | **Job-MJLQTR** | **5**              | **Default**     | **16**               | **11.360 μs** |  **0.8865 μs** | **0.2302 μs** |    **1264 B** |
| &#39;Split focused pane horizontally&#39; | ShortRun   | 3              | 1           | 16               | 12.767 μs | 24.7694 μs | 1.3577 μs |    1264 B |
