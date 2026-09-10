# Performance decisions and measurements

Measured 2026-09-09 on AMD Ryzen 9 5950X, 16 physical / 32 logical cores, Windows 11, SDK 11.0.100-preview.7.26381.103, x64 RyuJIT. These are short synthetic measurements from a shared development machine. They do not establish disk throughput or application latency improvements.

BenchmarkDotNet 0.15.8's SDK validator does not recognize .NET 11. The harness therefore uses its **InProcessEmitToolchain**, with three warmups and three measurement iterations, executing the actual .NET 11 host. This is a compatibility workaround; it lacks normal subprocess isolation. Runtime, variance and allocation results are retained in each report.

| Hypothesis / fixture | Baseline | Candidate | Observed result |
|---|---:|---:|---|
| Count allocated bits, 1 MiB | Bit loop: 3.91 ms | POPCNT: 68.44 µs; AVX2: 31.06 µs | AVX2 about 2.2× faster than POPCNT on this machine; all report 0 B allocated |
| First-fit lookup, 100,000 intervals; only last interval fits | Linear: 43.82 µs | Augmented treap: 47.13 ns | Large improvement for this deliberately adverse linear-search fixture; not representative of every lookup |
| 640-stream planner, same-run comparison | Archive v1: 614.6 µs / 244,194 B | Pooled v2: 526.4 µs / 28,292 B | About 88.4% less managed allocation; timing difference is preliminary |
| Aggregate 8,192 display cells | Current dispatch: 73.88 µs | 0 B allocated | This is a later-run measurement, not a controlled same-run speedup |

The aggregator dispatches to AVX2 only for at least 64 bytes per aligned cell range; smaller inputs use the scalar range path. Tests compare bit counts across unaligned starts and tails. AVX2 has a portable POPCNT/scalar fallback.

Frozen versions are in `src/Tedd.Defrag.Archive`, including the original planner and interval tree under `Tedd.Defrag.Archive.V1`. Do not optimize archive implementations in place. New candidates should add a comparison, preserve correctness tests, and retain a dated report before replacing an existing algorithm.

The planner still allocates its returned plan and some candidate bookkeeping. The scanner allocates retained metadata and strings. JSON snapshots, reports and journal records allocate. **The application is not allocation-free end to end.** Hot bitmap kernels and map aggregation are allocation-free after callers supply storage; planner scratch arrays are reused.

## GPU decision

No GPU planner was added. The CPU kernels measured tens of microseconds for a 1 MiB bitmap, while planning also requires pointer-heavy interval searches and live filesystem validation. A GPU path would add submission/synchronization and transfer work whose crossover has not been measured here. Claiming a speedup would be unsupported.

GPU offloading remains a candidate for very large batches already resident on a device or broad scoring passes. It should be tested against the existing CPU/AVX2 baselines with transfer costs included. The native MAUI map already renders through its platform drawing surface; this is distinct from GPU-based layout planning.

## Reproduce

```powershell
./scripts/Benchmark.ps1
./scripts/Benchmark.ps1 -Filter '*MapAndPlannerBenchmarks*'
```

Reports: [bitmap kernels](benchmarks/2026-09-09/BitmapBenchmarks-report-github.md), [free-space index](benchmarks/2026-09-09/FreeSpaceBenchmarks-report-github.md), [planner allocation comparison](benchmarks/2026-09-09/MapAndPlannerBenchmarks-report-github.md), [final map aggregation](benchmarks/2026-09-09/MapAggregation-final-report.md).

Further native measurements should use disposable HDD/SSD-backed VHD fixtures and include scan records/second, peak commit, actual moved bytes, failed/stale moves, foreground p95/p99 I/O latency, cold/warm filesystem workloads, snapshot growth and cancellation latency. Logical relocation bytes are not a NAND-wear measurement.
