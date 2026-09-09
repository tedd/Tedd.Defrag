```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
AMD Ryzen 9 5950X 3.40GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 11.0.100-preview.7.26381.103
  [Host] : .NET 11.0.0 (11.0.0-preview.7.26381.103, 11.0.26.38203), X64 RyuJIT x86-64-v3

Job=NET11-InProcess  Toolchain=InProcessEmitToolchain  IterationCount=3
LaunchCount=1  WarmupCount=3

```
| Method             | Mean     | Error    | StdDev   | Allocated |
|------------------- |---------:|---------:|---------:|----------:|
| Aggregate8192Cells | 73.88 μs | 10.90 μs | 0.597 μs |         - |
