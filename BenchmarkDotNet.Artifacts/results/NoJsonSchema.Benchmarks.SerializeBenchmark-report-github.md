```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.7.4 (24G517) [Darwin 24.6.0]
Apple M4, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.100
  [Host]   : .NET 10.0.0 (10.0.25.52411), Arm64 RyuJIT AdvSIMD
  ShortRun : .NET 10.0.0 (10.0.25.52411), Arm64 RyuJIT AdvSIMD

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method         | Mean     | Error    | StdDev  | Ratio | Gen0   | Allocated | Alloc Ratio |
|--------------- |---------:|---------:|--------:|------:|-------:|----------:|------------:|
| NoJsonSchema   | 211.1 ns | 20.15 ns | 1.10 ns |  0.78 | 0.0677 |     568 B |        1.00 |
| SystemTextJson | 271.4 ns | 28.25 ns | 1.55 ns |  1.00 | 0.0677 |     568 B |        1.00 |
