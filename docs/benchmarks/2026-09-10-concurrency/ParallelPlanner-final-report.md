```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
AMD Ryzen 9 5950X 3.40GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 11.0.100-preview.7.26381.103
  [Host] : .NET 11.0.0 (11.0.0-preview.7.26381.103, 11.0.26.38203), X64 RyuJIT x86-64-v3

Job=NET11-InProcess  Toolchain=InProcessEmitToolchain  IterationCount=3
LaunchCount=1  WarmupCount=3

```
| Method      | Files | Mean        | Error       | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------ |------ |------------:|------------:|----------:|------:|--------:|-------:|----------:|------------:|
| **Serial**      | **640**   |    **596.4 μs** |   **185.45 μs** |  **10.17 μs** |  **1.00** |    **0.02** | **2.9297** |  **48.83 KB** |        **1.00** |
| FourWorkers | 640   |    452.6 μs |    96.65 μs |   5.30 μs |  0.76 |    0.01 | 2.9297 |  48.83 KB |        1.00 |
|             |       |             |             |           |       |         |        |           |             |
| **Serial**      | **32768** | **27,358.4 μs** | **7,290.33 μs** | **399.61 μs** |  **1.00** |    **0.02** |      **-** |  **48.99 KB** |        **1.00** |
| FourWorkers | 32768 | 16,546.3 μs | 2,169.61 μs | 118.92 μs |  0.60 |    0.01 |      - |  50.14 KB |        1.02 |
