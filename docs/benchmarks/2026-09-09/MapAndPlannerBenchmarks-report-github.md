```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
AMD Ryzen 9 5950X 3.40GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 11.0.100-preview.7.26381.103
  [Host] : .NET 11.0.0 (11.0.0-preview.7.26381.103, 11.0.26.38203), X64 RyuJIT x86-64-v3

Job=NET11-InProcess  Toolchain=InProcessEmitToolchain  IterationCount=3
LaunchCount=1  WarmupCount=3

```
| Method             | Mean     | Error     | StdDev   | Gen0    | Gen1    | Gen2    | Allocated |
|------------------- |---------:|----------:|---------:|--------:|--------:|--------:|----------:|
| Aggregate8192Cells | 119.5 μs |  27.24 μs |  1.49 μs |       - |       - |       - |         - |
| Plan640Streams     | 526.4 μs | 170.79 μs |  9.36 μs |  0.9766 |       - |       - |   28292 B |
| ArchivePlannerV1   | 614.6 μs | 386.57 μs | 21.19 μs | 49.8047 | 49.8047 | 49.8047 |  244194 B |
