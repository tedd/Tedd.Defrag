# Tedd Defrag

A Windows-first .NET 11 NTFS optimizer with a native **.NET MAUI** dashboard, **Tedd.TUI** terminal interface, and persistent, isolated workers. Seventeen projects separate storage access, planning, visualization, scheduling, execution, updating, clients, and measurements.

[Project site](https://tedd.github.io/Tedd.Defrag/) · [Download the latest release](https://github.com/tedd/Tedd.Defrag/releases/latest) · [Source code](https://github.com/tedd/Tedd.Defrag)

**Status: engineering preview.** Read-only native analysis has been exercised on C:, including a partial scan under a 1 GiB memory cap. Native NTFS relocation is implemented but has not been validated on a disposable volume. Do not treat this as a production-qualified disk utility. Early-boot execution and registry hive replacement are deliberately unavailable.

## Run

Requires Windows 10 1809 or later and the SDK pinned in `global.json`: **11.0.100-preview.7.26381.103**. Install the `maui-windows` workload if needed. .NET 11 is a preview dependency.

```powershell
dotnet workload install maui-windows
dotnet build Tedd.Defrag.slnx -c Release
dotnet test tests/Tedd.Defrag.Tests -c Release
dotnet run --project src/Tedd.Defrag.Desktop
dotnet run --project src/Tedd.Defrag.Cli -- tui C:
```

The dashboard requests administrator access at startup, lists real volumes, and initially selects the system NTFS volume or the first available NTFS volume. Its map remains empty until you choose **Analyze** or **Preview**. Opening the application does not submit a job. If no supported volume is available, disk-operation controls remain disabled. Preview is the CLI default; actual changes require `--execute`. Closing either interface detaches it from the job.

Simulation mode and the `--demo` option have been removed. Legacy simulation requests are rejected and their schedules are disabled, so they cannot become real disk operations. Synthetic volume fixtures are compiled only into tests and benchmarks.

Publish a versioned, self-contained Windows distribution. The Desktop, CLI, and isolated worker are each single-file ReadyToRun executables and are placed together in one ZIP:

```powershell
./scripts/Publish.ps1 -Runtime win-x64 -Version 0.1.0
# artifacts/dist/Tedd.Defrag-win-x64.zip
# artifacts/dist/Tedd.Defrag-win-x64.zip.sha256
```

Extract the ZIP and run either `Tedd.Defrag.Desktop.exe` or `Tedd.Defrag.Cli.exe`; no .NET installation is required. Keep all three executables together because the separately elevated worker isolates privileged disk operations. Release builds check GitHub for a newer version at startup. With consent, they download the matching architecture, verify its published SHA-256 checksum, stop an idle worker, atomically replace the extracted application directory, and restart. Active jobs must finish or be cancelled first. `TEDD_DEFRAG_WORKER` can point to another built worker executable.

## Capabilities

| Area | Implementation |
|---|---|
| NTFS scan | Batched raw MFT reads; validated update-sequence fixups, signed runlists, names and streams; MFT extension mapping; allocation bitmap from Windows |
| Placement | Minimum-write, files-only, pack, pack + defrag, alphabetical, size, creation/modification time, extension, directory locality, shrink boundary |
| Constraints | Recursive path/glob exclusions, selected objects only, file size and fragment-count filters, optional relocation-byte/time budgets, no supporting moves of unrelated files |
| Maintenance | Windows ReTRIM, slab consolidation, automatic optimization; capability reporting; bounded virtual-disk pre-zeroing with delete-on-close files |
| Metadata | Movable MFT data and directory-index targets through supported filesystem APIs; incomplete or unsupported streams remain constrained |
| Visualization | Layered allocation/fragmentation/metadata/exclusion/activity counts; bounded drawing surface; zoom, cell inspection, live progress and JSON reports |
| Jobs | Persistent queue/history, pause/resume/cancel, per-volume exclusion, shared-storage arbitration, conservative unknown topology, manual resource groups |
| Scheduling | Weekly/day-of-week local-time schedules, same-day missed-run coalescing, overlap suppression, optional elevated per-user logon task |
| Interfaces | MAUI dashboard, Tedd.TUI terminal, scriptable JSON/NDJSON CLI; optional out-of-process Explorer classic context menu |

Ordering and packing are best-effort preferences using existing free space. They are not global optimality guarantees. `Partial` is a valid outcome when constraints, budgets, unsupported streams, or fragmentation remain. Directory locality is a placement preference, not a measured application speed claim.

The raw scanner does not recursively traverse directories. It deduplicates file records by identity, observes named streams, and marks unsupported/incomplete records explicitly. Ordinary files with extension attributes, sparse/compressed/encrypted streams, reparse points, hard-linked identities and unresolved paths are conservatively excluded from custom relocation. Their allocated clusters are still occupied in the bitmap. The MFT data stream's own extension mapping is assembled separately.

## Resource controls

| Control | Semantics |
|---|---|
| Memory | Optional Windows job-object **committed-memory** limit for the worker and its descendants; `0` leaves memory unlimited. The scanner stops before exhausting a configured cap. |
| CPU | Windows job-object hard rate limit; percentage of machine CPU scheduling capacity, subject to any enclosing job restrictions. |
| Affinity | Optional hexadecimal logical-processor mask, e.g. `0xF0`. Supported on a single processor group. No guarantee of L1/L2/L3 cache isolation or kernel-thread placement. |
| Bandwidth | Optional cooperative average pacing of **custom relocation and zeroing payload**; `0` disables pacing and nonzero values have no application maximum. It does not cap raw scan reads, total physical traffic, Windows optimizer traffic, or SSD write amplification. |
| Priority | Windows background processing mode in the isolated worker; the broker and interfaces retain normal responsiveness. |
| Idle/power | Sustained inactivity in the worker's interactive session; other active sessions with unknown activity pause execution. AC power can be required. No new custom moves while paused; an in-flight filesystem request can finish. |
| Concurrency | Limits simultaneous volumes and jobs sharing discovered resources. A per-volume cross-process mutex remains mandatory, including with the shared-storage override. |

Quiet, Balanced and Performance presets are editable. Resource settings are captured when a job is submitted, not applied retroactively to running work. Leaving all cores eligible is the default. Affinity can reduce competition but SMT siblings and kernel work may still share caches.

## CLI examples

```powershell
Tedd.Defrag.Cli.exe volumes --json
Tedd.Defrag.Cli.exe analyze D: --json
Tedd.Defrag.Cli.exe optimize D: --policy MinimumWrite --budget-mib 1024 --wait
Tedd.Defrag.Cli.exe defrag --path 'D:\Data\archive.bin' --execute --wait
Tedd.Defrag.Cli.exe optimize D: --exclude 'D:\VMs' --exclude '*\cache\*' --execute --wait
Tedd.Defrag.Cli.exe optimize D: --cpu 20 --memory 512 --io 16 --affinity 0xF0 --wait
Tedd.Defrag.Cli.exe trim D: --execute --wait
Tedd.Defrag.Cli.exe jobs list --json
Tedd.Defrag.Cli.exe jobs pause <id>
Tedd.Defrag.Cli.exe jobs resume <id>
Tedd.Defrag.Cli.exe jobs cancel <id>
Tedd.Defrag.Cli.exe jobs watch <id> --events
Tedd.Defrag.Cli.exe schedule add --name nightly --volume D: --days Sunday --at 02:00 --execute --idle-only
Tedd.Defrag.Cli.exe settings --parallel 3 --shared --per-device 2
```

Write budget, time limit, process-memory cap, and relocation bandwidth default to `0`, meaning unlimited. File defragmentation defaults to streams with at least 20 fragments; `--min-file-mib` and `--max-file-mib` optionally restrict file size.

`--allow-ssd` explicitly permits custom relocation on SSD/unknown media. TRIM support is probed independently of seek penalty. Windows maintenance refuses relocation exclusions it cannot enforce. Its progress text comes from Windows and is not parsed into invented percentages; the map is rescanned afterward. Pausing external optimization stops its process; submit a fresh job to continue.

Exit codes: **0** completed, **1** failed/interrupted, **2** arguments, **3** partial, **4** unsupported, **130** cancelled/detached. Ctrl+C while watching detaches; use `jobs cancel` to cancel the actual job.

## Scheduling and Explorer

`scripts/Install-ScheduledWorker.ps1` registers a per-user elevated logon task. This is an interactive-session broker, **not a SYSTEM service** and not pre-logon or offline defragmentation. It must be running for its schedules to become eligible. There is no wake-from-sleep or missed-previous-day catch-up. Pre-zeroing cannot be scheduled.

`scripts/Install-ExplorerMenu.ps1` registers single-selection **Analyze** and **Preview** commands without loading .NET into Explorer. On Windows 11 these appear under **Show more options**. It does not implement a modern `IExplorerCommand` shell extension or multi-selection. Both scripts have a `-Remove` option and are never run automatically.

Job data lives in `%LOCALAPPDATA%\Tedd.Defrag`. The named-pipe endpoint is restricted to the current user. The broker receives job intent, not caller-provided cluster addresses. Do not deploy it as a privileged multi-user service. Deployment and security limitations are described in [architecture](docs/architecture.md).

## Validation and performance

The automated suite covers bitmap/SIMD parity, randomized interval reservations, relocation invariants, selection/exclusions, raw versus filesystem-restored MFT records, volume geometry, torn records, malformed runlists, map coverage, scheduling fairness, and concurrent snapshot publication.

The MFT bootstrap correction was verified against both raw and `FSCTL_GET_NTFS_FILE_RECORD` representations of C:'s record 0. A subsequent read-only analysis processed 543,352 records and produced 348,029 stream layouts before returning `Partial` under the 1,024 MiB cap. Its allocation bitmap covers the entire volume; file coverage is explicitly limited. This validates analysis and cap handling on that volume, not relocation safety.

Run `scripts/Test-NativeVhd.ps1` from an elevated shell with the Hyper-V PowerShell module. It creates its own disposable VHDX, generates interleaved test data, verifies SHA-256 contents after actual relocation, and runs a filesystem check. **This native test was prepared but not executed here.** A single successful fixture is still insufficient to qualify system-volume use; concurrent file changes, worker termination, compressed/sparse files and snapshot-heavy workloads require a larger validation campaign.

BenchmarkDotNet reports and frozen prior implementations are checked in. The first measured pooling change cut planner allocations approximately **88%**, from 244 KB to 28 KB on a 640-stream fixture. This is a synthetic planning measurement, not a disk-throughput claim. See [performance notes and results](docs/performance.md).

```powershell
./scripts/Benchmark.ps1
```

Native early-boot execution, offline hive compaction, host-side VHD compaction, GPU planning, complete virtual-storage topology resolution, and production certification remain outside this preview. No registry keys/hives, boot configuration, snapshots, encryption settings or filesystem repair settings are changed by ordinary jobs.
