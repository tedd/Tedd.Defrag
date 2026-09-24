# Map raster experiment — 2026-09-23

The desktop map normally displays 8,192 sampled cells. This experiment compares three equivalent CPU rasterizers for a 1,280 × 276 pixel, 32-bit frame: individual pixel writes, per-row `Span<uint>.Fill`, and row-oriented fills. All methods clear the frame and render every cell. `MapRasterizerTests` verifies identical pixels at 8,192 and 65,536 cells and at smaller dimensions.

Hypothesis: row span fills reduce raster CPU time and managed allocation while `Tedd.WriteableBitmap.Maui` replaces thousands of GraphicsView drawing calls with one published native frame. Reject a specialized SIMD path unless a representative native-buffer benchmark shows a repeatable gain beyond noise. Preserve map hit testing, range selection, hover, panning, zoom, and highlighting.

Environment: commit `81c6262` plus the changes in this experiment; Windows 11 25H2, AMD Ryzen 9 5950X, .NET 11 preview 7 x64, RyuJIT x86-64-v3, AVX2, concurrent workstation GC. BenchmarkDotNet 0.15.8 used its in-process emit toolchain. The native benchmark includes bitmap lease acquisition and frame publication, with no attached view or GPU presentation.

| Workload | Scalar pixels | Span fill | Row-oriented |
| --- | ---: | ---: | ---: |
| Managed 8,192 cells, 3 iterations | 216.4 µs | 164.5 µs | 238.5 µs |
| Managed 65,536 cells, 3 iterations | 756.3 µs | 763.7 µs | 407.9 µs |
| Native bitmap 8,192 cells, 8 iterations | 240.3 µs | 236.9 µs | 246.9 µs |

The native-frame differences at the target 8,192 cells overlap measurement uncertainty. This pilot did not test SIMD color selection. Row spans were the initial implementation because they are simple and allocation-free during frame generation; the native result does not establish a speed advantage over scalar writes. The three-iteration 65,536-cell pilot alone was insufficient to justify an adaptive branch.

## Bit planes and row templates

The first comparison only varied writes after cell colors had been cached. A follow-up tested the proposed bit-plane representation. Each of six exclusive semantic colors has one bit per cell; allocation mixtures retain their exact 32-bit base color. A scalar decoder and an AVX2 decoder reconstruct the same colors as `MapAggregator.Color` before the complete redraw. This setup favors bit planes because encoding is excluded from the timed operation. It does not require 32-byte-aligned input: the AVX2 path uses unaligned vector loads for base colors and broadcasts the relevant eight bits from each plane.

| 8,192-cell managed redraw | 8 iterations | 12-iteration repeat |
| --- | ---: | ---: |
| Cached colors | 214.5 µs | 190.7 µs |
| Scalar bit planes | 226.4 µs | 273.8 µs |
| AVX2 bit planes | 240.9 µs | 209.4 µs |

The fixture is seeded and keeps each semantic count within the allocated count. Workstation timing varied between runs, but neither bit-plane decoder beat cached colors in either full-redraw comparison. AVX2 beat scalar bit-plane decoding in the second run; that advantage was not repeatable in the first. The bit-plane representation also retains a base-color array for continuous allocation mixtures, so it increases stored data over the cached 32-bit colors and incurs decoding on every redraw. The existing map classifies cells only when a region changes, not on every frame. Bit-plane classification therefore does not reduce the dominant repeated raster and bitmap-publication work in this path. The generated assembly was not inspected, so the exact emitted instruction sequence is unverified.

A row template addresses the repeated raster work directly: form one pixel row of cells, then copy it to the remaining pixel rows of that map row. Differential tests cover all methods, including a padded destination stride. The native bitmap test includes lease acquisition and publication.

| 8,192-cell native redraw, 8 iterations | Mean | Standard deviation |
| --- | ---: | ---: |
| Scalar pixels | 193.27 µs | 2.66 µs |
| Row spans | 190.84 µs | 8.01 µs |
| Row template | 77.60 µs | 1.14 µs |

At the maximum 65,536 cells, the managed row-oriented method is 302.21 µs, while the row template is 324.44 µs (eight iterations each). The desktop uses the row template when map rows have more than approximately three pixel rows and row-oriented fills for denser grids. The reusable template costs four bytes per destination pixel of width and neither method allocates during a redraw. The 8,192-cell improvement is a raster-kernel result, not a measured application frame-rate or memory-use improvement.

The navigator was measured separately because it paints narrow vertical strips. Filling one column at a time costs 175.3 µs at 1,280 × 42 and 771.6 µs at 2,560 × 84. Writing complete rows costs 115.7 µs and 458.1 µs respectively, with eight iterations per case and no managed allocation. The retained navigator uses row writes. Both candidates generate the same per-pixel colors; viewport shading and markers are excluded from these measurements.

Commands:

```powershell
dotnet run -c Release --no-build --project benchmarks/Tedd.Defrag.Benchmarks/Tedd.Defrag.Benchmarks.csproj -- --filter '*MapRasterBenchmarks*' --artifacts artifacts/benchmarks/map-raster
dotnet run -c Release --no-build --project benchmarks/Tedd.Defrag.Benchmarks/Tedd.Defrag.Benchmarks.csproj -- --filter '*NativeMapRasterBenchmarks*' --iterationCount 8 --warmupCount 4 --artifacts artifacts/benchmarks/native-map-raster
dotnet run -c Release --no-build --project benchmarks/Tedd.Defrag.Benchmarks/Tedd.Defrag.Benchmarks.csproj -- --filter '*OverviewRasterBenchmarks*' --iterationCount 8 --warmupCount 4 --artifacts artifacts/benchmarks/overview-raster
dotnet run -c Release --no-build --project benchmarks/Tedd.Defrag.Benchmarks/Tedd.Defrag.Benchmarks.csproj -- --filter '*BitPlaneMapBenchmarks*' --iterationCount 8 --warmupCount 4 --artifacts artifacts/benchmarks/bitplane-map-valid-counts
dotnet run -c Release --no-build --project benchmarks/Tedd.Defrag.Benchmarks/Tedd.Defrag.Benchmarks.csproj -- --filter '*BitPlaneMapBenchmarks*' --iterationCount 12 --warmupCount 5 --artifacts artifacts/benchmarks/bitplane-map-valid-repeat
dotnet run -c Release --no-build --project benchmarks/Tedd.Defrag.Benchmarks/Tedd.Defrag.Benchmarks.csproj -- --filter '*NativeMapRasterBenchmarks*' --iterationCount 8 --warmupCount 4 --artifacts artifacts/benchmarks/native-map-row-template
dotnet run -c Release --no-build --project benchmarks/Tedd.Defrag.Benchmarks/Tedd.Defrag.Benchmarks.csproj -- --filter 'MapRasterBenchmarks*' --iterationCount 8 --warmupCount 4 --artifacts artifacts/benchmarks/map-row-template
```

The generated BenchmarkDotNet reports are under `artifacts/benchmarks/` locally and are ignored by Git. The raster figures exclude color preparation; the bit-plane figures include decoding but exclude encoding. All figures exclude overlays, MAUI dispatch, CPU-to-GPU upload, GPU rendering, and pointer handling. No measured application frame-rate or private-byte improvement follows from these CPU results. The prior GraphicsView path was not timed under an equivalent on-screen workload.

## Dirty-frame feasibility — 2026-09-24

`Tedd.WriteableBitmap.Maui` selects any free back buffer. Its write-lease contract states that a selected buffer may hold an older frame and must be filled completely before publication. The lease exposes neither a stable slot identifier nor its last frame version. Thus a cell dirty-bit map alone cannot safely update only changed pixels in the current integration. The view draws the complete `SKImage` onto its GL surface, so a CPU-side dirty map would not by itself establish a partial GPU upload.

As a safe estimate of the CPU benefit for selection and highlight changes, `OverlayRedrawBenchmarks` compares a full native-buffer row-template redraw with copying a pre-rendered base frame into the leased buffer and drawing only the selection borders. Setup checks pixel equality. Both methods include lease acquisition and publication and allocate no managed memory per frame. The cached variant requires another 1,413,120-byte base frame at 1,280 × 276 (plus any row padding).

| Selected cells | Full redraw | Copy base and overlay |
| ---: | ---: | ---: |
| 1 | 84.53 µs | 70.46 µs |
| 128 | 101.64 µs | 82.10 µs |

These are eight-iteration means on the same workstation and runtime as above. The one-cell full-redraw result varied (9.80 µs standard deviation); the two methods' 99.9% error ranges overlap. At 128 cells, the copy saved about 20 µs in this CPU-only benchmark. The extra retained frame and unchanged whole-image presentation do not justify adding this cache for the current workload without on-screen frame and memory measurements. A true partial-update design would require per-buffer version tracking in the bitmap library and evidence that GPU presentation can use dirty rectangles.

```powershell
dotnet run -c Release --no-build --project benchmarks/Tedd.Defrag.Benchmarks/Tedd.Defrag.Benchmarks.csproj -- --filter '*OverlayRedrawBenchmarks*' --iterationCount 8 --warmupCount 4 --artifacts artifacts/benchmarks/overlay-redraw
```
