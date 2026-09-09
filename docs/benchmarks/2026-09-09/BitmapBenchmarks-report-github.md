```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
AMD Ryzen 9 5950X 3.40GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 11.0.100-preview.7.26381.103
  [Host] : .NET 11.0.0 (11.0.0-preview.7.26381.103, 11.0.26.38203), X64 RyuJIT x86-64-v3

Job=NET11-InProcess  Toolchain=InProcessEmitToolchain  IterationCount=3
LaunchCount=1  WarmupCount=3

```
| Method           | Bytes   | Mean           | Error           | StdDev        | Ratio | RatioSD | Allocated | Alloc Ratio |
|----------------- |-------- |---------------:|----------------:|--------------:|------:|--------:|----------:|------------:|
| **ArchiveBitLoop**   | **4096**    |    **15,752.8 ns** |     **9,830.40 ns** |     **538.84 ns** | **1.001** |    **0.04** |         **-** |          **NA** |
| HardwarePopcount | 4096    |       341.5 ns |        83.38 ns |       4.57 ns | 0.022 |    0.00 |         - |          NA |
| Avx2NibbleLookup | 4096    |       117.3 ns |        62.55 ns |       3.43 ns | 0.007 |    0.00 |         - |          NA |
|                  |         |                |                 |               |       |         |           |             |
| **ArchiveBitLoop**   | **1048576** | **3,909,903.3 ns** | **5,349,229.38 ns** | **293,209.31 ns** | **1.004** |    **0.09** |         **-** |          **NA** |
| HardwarePopcount | 1048576 |    68,437.1 ns |     4,048.05 ns |     221.89 ns | 0.018 |    0.00 |         - |          NA |
| Avx2NibbleLookup | 1048576 |    31,059.5 ns |    35,582.63 ns |   1,950.40 ns | 0.008 |    0.00 |         - |          NA |
