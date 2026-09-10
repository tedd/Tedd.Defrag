# Performance decisions and measurements

Measured 2026-09-09 on AMD Ryzen 9 5950X, 16 physical / 32 logical cores, Windows 11, SDK 11.0.100-preview.7.26381.103, x64 RyuJIT. These are short synthetic measurements from a shared development machine. They do not establish disk throughput or application latency improvements.

BenchmarkDotNet 0.15.8's SDK validator does not recognize .NET 11. The harness therefore uses its **InProcessEmitToolchain**, with three warmups and three measurement iterations, executing the actual .NET 11 host. This is a compatibility workaround; it lacks normal subprocess isolation. Runtime, variance and allocation results are retained in each report.

| Hypothesis / fixture | Baseline | Candidate | Observed result |
|---|---:|---:|---|
| Count allocated bits, 1 MiB | Bit loop: 3.91 ms | POPCNT: 68.44 µs; AVX2: 31.06 µs | AVX2 about 2.2× faster than POPCNT on this machine; all report 0 B allocated |
| First-fit lookup, 100,000 intervals; only last interval fits | Linear: 43.82 µs | Augmented treap: 47.13 ns | Large improvement for this deliberately adverse linear-search fixture; not representative of every lookup |
| 640-stream planner, same-run comparison | Archive v1: 614.6 µs / 244,194 B | Pooled v2: 526.4 µs / 28,292 B | About 88.4% less managed allocation; timing difference is preliminary |
| Aggregate 8,192 display cells | Current dispatch: 73.88 µs | 0 B allocated | This is a later-run measurement, not a controlled same-run speedup |

The aggregator dispatches to AVX2 only for at least 64 bytes per aligned cell range; smaller inputs use the scalar range path. Tests compare bit counts across unaligned starts and tails. AVX2 has a portable POPCNT/scalar fallback. These historical measurements predate the concurrency and free-range changes below.

Frozen versions are in `src/Tedd.Defrag.Archive`, including the original planner and interval tree under `Tedd.Defrag.Archive.V1`. Do not optimize archive implementations in place. New candidates should add a comparison, preserve correctness tests, and retain a dated report before replacing an existing algorithm.

The planner still allocates its returned plan and some candidate bookkeeping. The scanner allocates retained metadata and strings. JSON snapshots, reports and journal records allocate. **The application is not allocation-free end to end.** Hot bitmap kernels and map aggregation are allocation-free after callers supply storage; planner scratch arrays are reused.

## GPU decision

No GPU planner was added. The CPU kernels measured tens of microseconds for a 1 MiB bitmap, while planning also requires pointer-heavy interval searches and live filesystem validation. A GPU path would add submission/synchronization and transfer work whose crossover has not been measured here. Claiming a speedup would be unsupported.

GPU offloading remains a candidate for very large batches already resident on a device or broad scoring passes. It should be tested against the existing CPU/AVX2 baselines with transfer costs included. The native MAUI map already renders through its platform drawing surface; this is distinct from GPU-based layout planning.

## Bounded concurrency (2026-09-10)

MFT workers each own a volume handle and aligned 1 MiB buffer. They read and validate disjoint record batches, while the coordinator consumes results in MFT order. At most one batch per worker is retained or running. Paths resolve serially after parsing; memory-cap stops drain the bounded pending batches. Record parsing remains scalar because its variable attributes, runlists, fixups and string decoding are branch-heavy; no SIMD speedup is claimed for it.

The planner uses SIMD equality checks to skip uniform 32-byte or 16-byte bitmap blocks, with scalar processing for mixed blocks and tails. It excludes the MFT growth reservation during the first index walk. Large inventories sort independent partitions and merge deterministically with the original tie-breaker. Small inventories avoid worker startup. The free-space index and final destination reservations stay serial so there is one owner of each placement decision.

The relocation scheduler allows one outstanding move per file identity and up to the requested number of independent files. A file retains its lane until its sequence finishes or fails. Separate synchronous volume handles allow requests to overlap without sharing a synchronous handle. NTFS and the storage stack may still serialize requests; additional queue depth can also increase HDD seeks. The default Performance preset uses sixteen; Balanced and Quiet use one. MFT, directory-index and directory-locality operations stay serial. Source space is not reused until the entire batch completes, and ambiguous results force a fresh bitmap before replanning. Journals, layout mutation and progress publication have one coordinator. Cancellation stops dispatch and accounts for all in-flight results before handles close.

`--scan-workers`, `--planning-workers` and `--move-queue` expose these limits in the CLI and corresponding desktop settings. Worker activity and process thread counts are separate measurements. Relocation in-flight counts include identity/extent validation, the filesystem move and result verification. Physical device queue depth is not measured. The interface reports implemented processing paths; GPU design decisions are documented above.

Tests exercise pipeline ordering/bounds/disposal, overlapping file identities, per-file sequencing across streams, failure isolation, draining on cancellation, byte budgets, SIMD range parity and identical serial/parallel plans. Native validation requires an elevated shell: `scripts/Test-NativeVhd.ps1 -WorkerPath <worker.exe> -ClientPath <cli.exe> -ScanWorkers 4 -MoveQueueDepth 16`. The current non-elevated development session did not run this new native queue configuration; the earlier native fixtures above do not validate it.

Reproduce the CPU comparisons with `scripts/Benchmark.ps1 -Filter '*ParallelPlannerBenchmarks*'` and `scripts/Benchmark.ps1 -Filter '*FreeRangeSimdBenchmarks*'`. These synthetic tests cannot establish raw-volume read throughput, relocation speed, or foreground latency.

Final same-run measurements on the machine described above:

| Fixture | Serial / previous walk | Current path | Interpretation |
|---|---:|---:|---|
| Alphabetical planning, 32,768 streams | 27.36 ms, 48.99 KiB allocated | Four workers: 16.55 ms, 50.14 KiB | About 40% less elapsed time in this fixture |
| Free ranges, 1 MiB with alternating uniform regions | 290.1 µs | 142.8 µs | SIMD block skipping about 2.0× faster |
| Free ranges, 1 MiB random bitmap | 75.94 ms | 31.97 ms | Scalar trailing-zero transition walk about 2.4× faster; this is not a SIMD gain on mixed bytes |

The first SIMD candidate regressed on random data (64.10 → 89.94 ms). Moving mixed-word processing into a trailing-zero transition loop removed repeated per-cluster dispatch and produced the final result. The 640-stream planner fixture uses one worker in **both** configurations; its differing timings demonstrate in-process/tiering/order sensitivity, not a concurrency benefit. Short runs and shared-machine effects limit all timing conclusions. Allocation figures include iterator state; the new free-range walk is not allocation-free.

Reports: [final planner](benchmarks/2026-09-10-concurrency/ParallelPlanner-final-report.md), [final bitmap walk](benchmarks/2026-09-10-concurrency/FreeRangeSimd-final-report.md), [initial planner](benchmarks/2026-09-10-concurrency/ParallelPlanner-report.md), [initial bitmap regression](benchmarks/2026-09-10-concurrency/FreeRangeSimd-initial-report.md).

## Reproduce

```powershell
./scripts/Benchmark.ps1
./scripts/Benchmark.ps1 -Filter '*MapAndPlannerBenchmarks*'
```

Reports: [bitmap kernels](benchmarks/2026-09-09/BitmapBenchmarks-report-github.md), [free-space index](benchmarks/2026-09-09/FreeSpaceBenchmarks-report-github.md), [planner allocation comparison](benchmarks/2026-09-09/MapAndPlannerBenchmarks-report-github.md), [final map aggregation](benchmarks/2026-09-09/MapAggregation-final-report.md).

Further native measurements should use disposable HDD/SSD-backed VHD fixtures and include scan records/second, peak commit, actual moved bytes, failed/stale moves, foreground p95/p99 I/O latency, cold/warm filesystem workloads, snapshot growth and cancellation latency. Logical relocation bytes are not a NAND-wear measurement.
