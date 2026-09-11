# Tedd Defrag

A Windows-first .NET 11 NTFS optimizer with ReFS analysis and Windows-managed maintenance, a native **.NET MAUI** dashboard, **Tedd.TUI** terminal interface, and isolated execution workers. Sixteen projects separate storage access, planning, visualization, execution, updating, clients, and measurements.

[Project site](https://tedd.github.io/Tedd.Defrag/) · [Download the latest release](https://github.com/tedd/Tedd.Defrag/releases/latest) · [Source code](https://github.com/tedd/Tedd.Defrag)

**Status: engineering preview.** Read-only native analysis has been exercised on C:, including a partial scan under a 1 GiB memory cap. Native NTFS relocation has passed two disposable-volume fixtures through the broker, with content hashes and filesystem checks verified. These fixtures do not establish production qualification. Early-boot execution and registry hive replacement are deliberately unavailable.

## Run

Requires Windows 10 1809 or later and the SDK pinned in `global.json`: **11.0.100-preview.7.26381.103**. Install the `maui-windows` workload if needed. .NET 11 is a preview dependency.

```powershell
dotnet workload install maui-windows
dotnet build Tedd.Defrag.slnx -c Release
dotnet test tests/Tedd.Defrag.Tests -c Release
dotnet run --project src/Tedd.Defrag.Desktop
dotnet run --project src/Tedd.Defrag.Cli -- tui C:
```

The dashboard requests administrator access when starting its worker, lists real volumes, and initially selects the system volume or the first available supported volume. NTFS and ReFS are supported; the method selector offers operations for the selected filesystem. Its map remains empty until you choose **Analyze** or **Preview**. Opening the application does not submit a job. If no supported volume is available, disk-operation controls remain disabled. Preview is the CLI default; actual changes require `--execute`. Closing the desktop application cancels its queued and running jobs and stops the worker. Closing the terminal interface detaches from a submitted job.

Synthetic volume fixtures are compiled only into tests and benchmarks.

Before submitting work, the client confirms the worker build. Active or queued jobs must finish or be cancelled before a different worker build is started. Development worker discovery matches the client build and prefers its Debug/Release configuration. `worker status --json` reports the broker build and executable path; `worker start` starts or refreshes an idle broker without submitting a disk job. Job reports include `WorkerBuild` to identify the code that actually executed them. `scripts/Test-WorkerStartup.ps1 -WorkerPath <exe>` verifies volume discovery and dependency loading without relocation; publishing runs this check for the host architecture.

Publish versioned, self-contained Windows distributions. The Desktop, CLI, and isolated worker are each single-file ReadyToRun executables. WiX packages the same payload as an MSI, an EXE bootstrapper, and a stand-alone ZIP:

```powershell
./scripts/Publish.ps1 -Runtime win-x64 -Version 0.1.0
# artifacts/dist/Tedd.Defrag-win-x64.zip
# artifacts/dist/Tedd.Defrag-win-x64.zip.sha256
# artifacts/dist/Tedd.Defrag-win-x64.msi
# artifacts/dist/Tedd.Defrag-Setup-win-x64.exe
```

For a conventional installation, download the architecture-matched `Tedd.Defrag-Setup-win-*.exe` or MSI. Both install under Program Files, add a Start menu shortcut, and register an uninstaller in Windows **Installed apps**. The EXE is appropriate for interactive installation; the MSI supports standard Windows Installer deployment and removal. Packages are not currently code-signed, so Windows may identify the publisher as unknown. No .NET installation is required.

For stand-alone use, extract `Tedd.Defrag-win-*.zip` and run either `Tedd.Defrag.Desktop.exe` or `Tedd.Defrag.Cli.exe`. Keep all three executables together because the separately elevated worker isolates privileged disk operations. The ZIP does not register an uninstaller; remove its extracted directory to uninstall it.

Release builds check GitHub Releases for a newer stable version at startup. With consent, installed copies download and run the architecture-matched verified EXE installer; portable copies download the matching ZIP and atomically replace their extracted application directory. Every automatic update verifies the release asset against its published SHA-256 checksum before execution or extraction. Active jobs must finish or be cancelled first. `TEDD_DEFRAG_WORKER` can point to another built worker executable.

## Capabilities

| Area | Implementation |
|---|---|
| NTFS scan | Bounded parallel raw MFT reads and record parsing; independent handles and 1 MiB buffers; ordered inventory assembly; validated update-sequence fixups, signed runlists, names and streams; allocation bitmap from Windows |
| ReFS scan | Windows allocation bitmap and file extent queries; directory traversal of accessible unnamed streams; partial file coverage with explicit warnings |
| NTFS placement | Minimum-write, files-only, pack, pack + defrag, alphabetical, size, creation/modification time, extension, directory locality, shrink boundary |
| Constraints | Recursive path/glob exclusions, selected objects only, file size and fragment-count filters, optional relocation-byte/time budgets, no supporting moves of unrelated files |
| Maintenance | Windows ReTRIM, slab consolidation, automatic optimization and whole-volume Windows defrag on NTFS/ReFS where supported; bounded NTFS virtual-disk pre-zeroing with delete-on-close files |
| Metadata | Movable MFT data and directory-index targets through supported filesystem APIs; incomplete or unsupported streams remain constrained |
| Visualization | Layered allocation/fragmentation/metadata/exclusion/activity counts; bounded drawing surface; zoom, cell inspection, live progress and JSON reports |
| Jobs | Queue and persistent history, pause/resume/cancel, per-volume exclusion, shared-storage arbitration, conservative unknown topology, manual resource groups |
| Interfaces | MAUI dashboard, Tedd.TUI terminal, scriptable JSON/NDJSON CLI; optional out-of-process Explorer classic context menu |

Ordering and packing are best-effort preferences using existing free space. They are not global optimality guarantees. `Partial` is a valid outcome when constraints, budgets, unsupported streams, or fragmentation remain. Directory locality is a placement preference, not a measured application speed claim.

The NTFS raw scanner does not recursively traverse directories. It deduplicates file records by identity, observes named streams, and marks unsupported/incomplete records explicitly. Ordinary files with extension attributes, sparse/compressed/encrypted streams, reparse points, hard-linked identities and unresolved paths are conservatively excluded from custom relocation. Their allocated clusters are still occupied in the bitmap. The MFT data stream's own extension mapping is assembled separately.

ReFS analysis enumerates accessible unnamed file streams, queries their extents, and displays the volume allocation bitmap. Reparse targets, named streams and filesystem metadata are outside file coverage; analysis reports `Partial`, and fragmentation counts describe observed streams. ReFS scans run serially and honor cancellation and memory limits. Custom placement, selected-file defrag, MFT/index optimization, shrink preparation and pre-zeroing require NTFS.

For ReFS, use **ReTRIM**, **Windows automatic**, **Slab consolidation**, or **Windows defrag**. [Windows defrag](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/defrag) determines availability for the filesystem version, media and volume state; an unsupported request is reported as a failure with Windows diagnostics. Maintenance can proceed when ReFS allocation scanning is unavailable. Windows defrag applies to the whole volume and cannot enforce selected paths, exclusions, custom fragment thresholds, file-size filters or relocation-byte budgets. Its preview identifies the command without estimating moves or submitting writes. A successful maintenance operation can still return `Partial` when the subsequent analysis has incomplete file coverage.

## Resource controls

| Control | Semantics |
|---|---|
| Memory | Optional Windows job-object **committed-memory** limit for the worker and its descendants; `0` leaves memory unlimited. The scanner stops before exhausting a configured cap. |
| CPU | Windows job-object hard rate limit; percentage of machine processor capacity, subject to any enclosing job restrictions. |
| Affinity | Optional hexadecimal logical-processor mask, e.g. `0xF0`. Supported on a single processor group. No guarantee of L1/L2/L3 cache isolation or kernel-thread placement. |
| Bandwidth | Optional cooperative average pacing of **custom relocation and zeroing payload**; `0` disables pacing and nonzero values have no application maximum. It does not cap raw scan reads, total physical traffic, Windows optimizer traffic, or SSD write amplification. |
| Priority | Windows background processing mode in the isolated worker; the broker and interfaces retain normal responsiveness. |
| Idle/power | Sustained inactivity in the worker's interactive session; other active sessions with unknown activity pause execution. AC power can be required. No new custom moves while paused; an in-flight filesystem request can finish. |
| Concurrency | Separate limits for volumes/shared resources, MFT workers (0–32), planner sort workers (0–32), and outstanding file moves (1–16). A per-volume cross-process mutex remains mandatory. |

Quiet, Balanced and Performance presets are editable. Performance is the application default: a 100% CPU ceiling, automatic scan/planner worker selection, up to 16 independent file moves, foreground priority, and no power, memory, or relocation-bandwidth restriction. Resource settings are captured when a job is submitted, not applied retroactively to running work. Leaving all cores eligible is the default. Affinity can reduce competition but SMT siblings and kernel work may still share caches.

Performance automatically selects MFT and planner workers and uses a move queue depth of sixteen. Balanced and Quiet retain one outstanding move. Zero scan/planner workers means automatic selection based on CPU policy; scan concurrency is additionally bounded by memory headroom. Planner inventories below 8,192 streams use one sorting worker. Free-space enumeration skips uniform bitmap blocks with 256-bit or 128-bit SIMD where available. Sorting can run across partitions, followed by a deterministic merge; destination reservations and ancestry resolution remain serial. MFT record validation is scalar. GPU compute is not used.

Relocation overlaps independent file identities, retaining the original move sequence within each file, including named streams. Each worker borrows an independent volume handle. Metadata operations use queue depth one. Journaling and layout mutation stay on the coordinator; cancellation drains submitted requests, and failed moves trigger a bitmap refresh before another batch can recycle freed space. Deeper queues permit more outstanding work but can increase seeks on HDDs; they do not establish device saturation or higher throughput.

The desktop opens **Performance details** when a job starts; the same button reopens it. The popup presents elapsed time, process use, job totals, phase progress, throughput, workers, and requests in aligned tables. These are application observations, not physical device queue measurements. The Overview recommendation identifies HDD, SSD, or unknown media; quantifies fragmented streams and the active fragment threshold; names the worst reported streams; and proposes maintenance from the observed layout. HDDs and SSDs can both receive selectable steps for fragmented MFT data, fragmented directory indexes, and eligible files at the selected threshold, followed by ReTRIM when supported. Every displayed operation is a direct selector; each step is then previewed and run separately. Logical contiguity can reduce filesystem extent processing and host I/O request overhead even when an SSD privately remaps logical blocks. Metadata fragmentation is assessed independently of the ordinary-file threshold. Unknown media delegates to Windows automatic maintenance. The same telemetry is stored in JSON snapshots and reports. Historical reports without telemetry remain readable.

## CLI examples

```powershell
Tedd.Defrag.Cli.exe volumes --json
Tedd.Defrag.Cli.exe analyze D: --json
Tedd.Defrag.Cli.exe optimize D: --policy MinimumWrite --budget-mib 1024 --wait
Tedd.Defrag.Cli.exe defrag --path 'D:\Data\archive.bin' --execute --wait
Tedd.Defrag.Cli.exe optimize D: --exclude 'D:\VMs' --exclude '*\cache\*' --execute --wait
Tedd.Defrag.Cli.exe optimize D: --cpu 20 --memory 512 --io 16 --affinity 0xF0 --wait
Tedd.Defrag.Cli.exe optimize D: --preset performance --scan-workers 0 --planning-workers 0 --move-queue 16 --wait
Tedd.Defrag.Cli.exe trim D: --execute --wait
Tedd.Defrag.Cli.exe optimize D: --policy WindowsDefrag --execute --wait
Tedd.Defrag.Cli.exe jobs list --json
Tedd.Defrag.Cli.exe jobs pause <id>
Tedd.Defrag.Cli.exe jobs resume <id>
Tedd.Defrag.Cli.exe jobs cancel <id>
Tedd.Defrag.Cli.exe jobs watch <id> --events
Tedd.Defrag.Cli.exe settings --parallel 3 --shared --per-device 2
```

Write budget, time limit, process-memory cap, and relocation bandwidth default to `0`, meaning unlimited. File defragmentation defaults to streams with at least 20 fragments; `--min-file-mib` and `--max-file-mib` optionally restrict file size.

On ReFS, `defrag D:` defaults to whole-volume `WindowsDefrag`, and `optimize D:` defaults to `Automatic`. On NTFS, both default to `MinimumWrite`. Explicit `--policy` selections take precedence.

`--allow-ssd` explicitly permits custom relocation or Windows defrag on SSD/unknown media. TRIM support is probed independently of seek penalty. Windows maintenance refuses relocation exclusions it cannot enforce. Its progress text comes from Windows and is not parsed into invented percentages; the map is rescanned afterward. Windows output is saved in the job directory's `maintenance.log`. Pausing external optimization stops its process; submit a fresh job to continue.

Exit codes: **0** completed, **1** failed/interrupted, **2** arguments, **3** partial, **4** unsupported, **130** cancelled/detached. Ctrl+C while watching detaches; use `jobs cancel` to cancel the actual job.

## Explorer

`scripts/Install-ExplorerMenu.ps1` registers single-selection **Analyze** and **Preview** commands without loading .NET into Explorer. On Windows 11 these appear under **Show more options**. It does not implement a modern `IExplorerCommand` shell extension or multi-selection. The script has a `-Remove` option and is never run automatically.

Job data lives in `%LOCALAPPDATA%\Tedd.Defrag`. The named-pipe endpoint is restricted to the current user. The broker receives job intent, not caller-provided cluster addresses. Do not deploy it as a privileged multi-user service. Deployment and security limitations are described in [architecture](docs/architecture.md).

## Validation and performance

The automated suite covers bitmap/SIMD parity, randomized interval reservations, relocation invariants, selection/exclusions, raw versus filesystem-restored MFT records, volume geometry, torn records, malformed runlists, map coverage, resource-arbitration fairness, and concurrent snapshot publication.

Run `scripts/Test-NativeVhd.ps1` from an elevated shell with the Hyper-V PowerShell module. It creates its own disposable VHDX, generates interleaved test data, verifies SHA-256 contents after actual relocation, and runs a filesystem check. Supply `-ClientPath <cli-exe>` with `-WorkerPath <worker-exe>` to exercise broker submission, dispatch, execution, and polling; `-Operation Pack` selects the packing fixture. On September 10, 2026, full broker tests moved 88 MiB with MinimumWrite and 68.3 MiB with Pack, with zero failed moves, unchanged file hashes, and clean filesystem checks. Both returned partial results because fixture exclusions and placement constraints remained. These fixtures are insufficient to qualify system-volume use; concurrent file changes, worker termination, compressed/sparse files and snapshot-heavy workloads require a larger validation campaign.

BenchmarkDotNet reports and comparison implementations are checked in. The planner measurement covers allocation and execution time on a 640-stream fixture; it is not a disk-throughput claim. See [performance notes and results](docs/performance.md).

```powershell
./scripts/Benchmark.ps1
```

Native early-boot execution, offline hive compaction, host-side VHD compaction, GPU planning, complete virtual-storage topology resolution, and production certification remain outside this preview. No registry keys/hives, boot configuration, snapshots, encryption settings or filesystem repair settings are changed by ordinary jobs.
