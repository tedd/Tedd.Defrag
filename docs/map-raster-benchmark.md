# Map raster experiment — 2026-09-23

The desktop map normally displays 8,192 sampled cells. This experiment compares three equivalent CPU rasterizers for a 1,280 × 276 pixel, 32-bit frame: individual pixel writes, per-row `Span<uint>.Fill`, and row-oriented fills. All methods clear the frame and render every cell. `MapRasterizerTests` verifies identical pixels at 8,192 and 65,536 cells and at smaller dimensions.

Hypothesis: row span fills reduce raster CPU time and managed allocation while `Tedd.WriteableBitmap.Maui` replaces thousands of GraphicsView drawing calls with one published native frame. Reject a specialized SIMD path unless a representative native-buffer benchmark shows a repeatable gain beyond noise. Preserve map hit testing, range selection, hover, panning, zoom, and highlighting.

Environment: commit `81c6262` plus the changes in this experiment; Windows 11 25H2, AMD Ryzen 9 5950X, .NET 11 preview 7 x64, RyuJIT x86-64-v3, AVX2, concurrent workstation GC. BenchmarkDotNet 0.15.8 used its in-process emit toolchain. The native benchmark includes bitmap lease acquisition and frame publication, with no attached view or GPU presentation.

| Workload | Scalar pixels | Span fill | Row-oriented |
| --- | ---: | ---: | ---: |
| Managed 8,192 cells, 3 iterations | 216.4 µs | 164.5 µs | 238.5 µs |
| Managed 65,536 cells, 3 iterations | 756.3 µs | 763.7 µs | 407.9 µs |
| Native bitmap 8,192 cells, 8 iterations | 240.3 µs | 236.9 µs | 246.9 µs |

The native-frame differences at the target 8,192 cells overlap measurement uncertainty. No explicit SIMD implementation is retained. The desktop uses row spans because they are simple, allocation-free during frame generation, and fastest in the managed-buffer pilot; the native result does not establish a speed advantage over scalar writes. The 65,536-cell crossover is outside the desktop default and the three-iteration pilot is insufficient to justify an adaptive branch.

The navigator was measured separately because it paints narrow vertical strips. Filling one column at a time costs 175.3 µs at 1,280 × 42 and 771.6 µs at 2,560 × 84. Writing complete rows costs 115.7 µs and 458.1 µs respectively, with eight iterations per case and no managed allocation. The retained navigator uses row writes. Both candidates generate the same per-pixel colors; viewport shading and markers are excluded from these measurements.

Commands:

```powershell
dotnet run -c Release --no-build --project benchmarks/Tedd.Defrag.Benchmarks/Tedd.Defrag.Benchmarks.csproj -- --filter '*MapRasterBenchmarks*' --artifacts artifacts/benchmarks/map-raster
dotnet run -c Release --no-build --project benchmarks/Tedd.Defrag.Benchmarks/Tedd.Defrag.Benchmarks.csproj -- --filter '*NativeMapRasterBenchmarks*' --iterationCount 8 --warmupCount 4 --artifacts artifacts/benchmarks/native-map-raster
dotnet run -c Release --no-build --project benchmarks/Tedd.Defrag.Benchmarks/Tedd.Defrag.Benchmarks.csproj -- --filter '*OverviewRasterBenchmarks*' --iterationCount 8 --warmupCount 4 --artifacts artifacts/benchmarks/overview-raster
```

The generated BenchmarkDotNet reports are under `artifacts/benchmarks/` locally and are ignored by Git. These numbers exclude color preparation, overlays, MAUI dispatch, CPU-to-GPU upload, GPU rendering, and pointer handling. No measured application frame-rate or private-byte improvement follows from these CPU results. The prior GraphicsView path was not timed under an equivalent on-screen workload.
