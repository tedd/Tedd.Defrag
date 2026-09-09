```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
AMD Ryzen 9 5950X 3.40GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 11.0.100-preview.7.26381.103
  [Host] : .NET 11.0.0 (11.0.0-preview.7.26381.103, 11.0.26.38203), X64 RyuJIT x86-64-v3

Job=NET11-InProcess  Toolchain=InProcessEmitToolchain  IterationCount=3
LaunchCount=1  WarmupCount=3

```
| Method         | Intervals | Mean         | Error        | StdDev       | Ratio | RatioSD | Allocated | Alloc Ratio |
|--------------- |---------- |-------------:|-------------:|-------------:|------:|--------:|----------:|------------:|
| **ArchiveLinear**  | **1000**      |    **360.85 ns** |    **206.15 ns** |    **11.300 ns** |  **1.00** |    **0.04** |         **-** |          **NA** |
| AugmentedIndex | 1000      |     33.11 ns |     83.02 ns |     4.550 ns |  0.09 |    0.01 |         - |          NA |
|                |           |              |              |              |       |         |           |             |
| **ArchiveLinear**  | **100000**    | **43,823.42 ns** | **28,626.77 ns** | **1,569.130 ns** | **1.001** |    **0.04** |         **-** |          **NA** |
| AugmentedIndex | 100000    |     47.13 ns |    151.31 ns |     8.294 ns | 0.001 |    0.00 |         - |          NA |
