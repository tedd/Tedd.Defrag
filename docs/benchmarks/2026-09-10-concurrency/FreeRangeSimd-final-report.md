```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
AMD Ryzen 9 5950X 3.40GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 11.0.100-preview.7.26381.103
  [Host] : .NET 11.0.0 (11.0.0-preview.7.26381.103, 11.0.26.38203), X64 RyuJIT x86-64-v3

Job=NET11-InProcess  Toolchain=InProcessEmitToolchain  IterationCount=3
LaunchCount=1  WarmupCount=3

```
| Method            | Mixed | Mean        | Error        | StdDev      | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------ |------ |------------:|-------------:|------------:|------:|--------:|----------:|------------:|
| **ScalarWords**       | **False** |    **290.1 μs** |    **164.52 μs** |     **9.02 μs** |  **1.00** |    **0.04** |      **80 B** |        **1.00** |
| SimdUniformBlocks | False |    142.8 μs |     82.17 μs |     4.50 μs |  0.49 |    0.02 |     145 B |        1.81 |
|                   |       |             |              |             |       |         |           |             |
| **ScalarWords**       | **True**  | **75,938.9 μs** | **38,522.06 μs** | **2,111.52 μs** |  **1.00** |    **0.03** |         **-** |          **NA** |
| SimdUniformBlocks | True  | 31,972.7 μs |  7,281.61 μs |   399.13 μs |  0.42 |    0.01 |         - |          NA |
