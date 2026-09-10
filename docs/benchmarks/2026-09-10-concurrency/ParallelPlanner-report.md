```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
AMD Ryzen 9 5950X 3.40GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 11.0.100-preview.7.26381.103
  [Host] : .NET 11.0.0 (11.0.0-preview.7.26381.103, 11.0.26.38203), X64 RyuJIT x86-64-v3

Job=NET11-InProcess  Toolchain=InProcessEmitToolchain  IterationCount=3
LaunchCount=1  WarmupCount=3

```
| Method      | Files | Mean        | Error       | StdDev      | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------ |------ |------------:|------------:|------------:|------:|--------:|-------:|----------:|------------:|
| **Serial**      | **640**   |    **644.3 μs** |    **308.4 μs** |    **16.90 μs** |  **1.00** |    **0.03** | **2.9297** |  **48.81 KB** |        **1.00** |
| FourWorkers | 640   |    636.2 μs |    914.7 μs |    50.14 μs |  0.99 |    0.07 | 2.9297 |  48.81 KB |        1.00 |
|             |       |             |             |             |       |         |        |           |             |
| **Serial**      | **32768** | **30,097.7 μs** | **26,518.4 μs** | **1,453.56 μs** |  **1.00** |    **0.06** |      **-** |  **48.98 KB** |        **1.00** |
| FourWorkers | 32768 | 19,713.4 μs |  8,004.2 μs |   438.74 μs |  0.66 |    0.03 |      - |  50.12 KB |        1.02 |
