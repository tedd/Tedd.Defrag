```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
AMD Ryzen 9 5950X 3.40GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 11.0.100-preview.7.26381.103
  [Host] : .NET 11.0.0 (11.0.0-preview.7.26381.103, 11.0.26.38203), X64 RyuJIT x86-64-v3

Job=NET11-InProcess  Toolchain=InProcessEmitToolchain  IterationCount=3
LaunchCount=1  WarmupCount=3

```
| Method            | Mixed | Mean        | Error       | StdDev      | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------ |------ |------------:|------------:|------------:|------:|--------:|----------:|------------:|
| **ScalarWords**       | **False** |    **232.7 μs** |    **286.4 μs** |    **15.70 μs** |  **1.00** |    **0.08** |      **80 B** |        **1.00** |
| SimdUniformBlocks | False |    117.8 μs |    168.2 μs |     9.22 μs |  0.51 |    0.05 |     129 B |        1.61 |
|                   |       |             |             |             |       |         |           |             |
| **ScalarWords**       | **True**  | **64,104.8 μs** |  **6,526.2 μs** |   **357.72 μs** |  **1.00** |    **0.01** |         **-** |          **NA** |
| SimdUniformBlocks | True  | 89,944.3 μs | 90,753.4 μs | 4,974.50 μs |  1.40 |    0.07 |         - |          NA |
