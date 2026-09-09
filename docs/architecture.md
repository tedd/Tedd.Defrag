# Architecture and execution contract

| Project | Responsibility |
|---|---|
| Core | Immutable requests/results, extent model, path policies, bitmap primitives |
| Ntfs | Bounds-checked read-only MFT FILE-record, attribute-list and mapping-pairs parsing |
| Windows | CsWin32-generated native APIs, handles, volume geometry/capabilities, raw scanner, retrieval pointers, moves, resource limits and activity gates |
| Planning | Augmented address-indexed free intervals, pooled scratch storage, bounded placement plans |
| Visualization | SIMD bitmap kernel, layered aggregation into caller-owned cells and shared palette |
| Maintenance | OS-managed maintenance and filesystem-owned virtual-disk preparation |
| Scheduling | Atomic resource-set arbitration with priority for older conflicting jobs; recurrence eligibility |
| Persistence | Durable requests, atomic status replacement with reader/writer retries, append-only flushed move records |
| Engine | Scan/plan/execute/reconcile state machine, cooperative control, budgets and verification |
| Worker | Persistent per-user broker; separately capped execution processes |
| Client | Framed current-user named-pipe protocol and worker discovery/launch |
| Update | GitHub release discovery, SHA-256 verification, safe extraction, atomic directory replacement and rollback |
| Desktop | Native MAUI controls and one map drawing surface; no filesystem mutation in the UI process |
| Cli | Tedd.TUI NuGet terminal host, command parsing, structured outputs and stable exit codes |
| Archive | Frozen scalar/linear baselines and the first planner/index implementation |
| Tests | Randomized invariants, parser validation, scheduling and persistence concurrency; synthetic fixtures shared with benchmarks |
| Benchmarks | BenchmarkDotNet comparisons; separate from application startup and production binaries |

The GUI and terminal submit the same `JobRequest`. The broker records intent and acquires the volume's resource set, then launches one worker per job. The process installs its memory/CPU limits before scanning. Worker completion does not depend on an interface remaining open.

Application startup discovers real volumes without starting analysis or optimization. The simulation execution path is removed; synthetic layouts are linked only into tests and benchmarks from `tests/Fixtures`. The legacy JSON `Demo` flag remains recognizable solely to reject old simulation requests before storage access. Queued legacy jobs fail validation, and legacy simulation schedules are disabled when the broker starts and are never considered due.

The current broker is a desktop-user agent with optional elevated logon startup. It is not hardened for arbitrary users submitting work to a SYSTEM identity. Current-user pipe restrictions, bounded frames, request validation and worker-side volume/path checks reduce exposure, but a formal threat model and adversarial local IPC/storage testing are still release requirements. Persistent files inherit the profile directory's access controls. Never change the service account to SYSTEM or share the job directory.

## Scanner

`FSCTL_GET_VOLUME_BITMAP` provides allocation independently of ownership. Volume geometry is decoded through CsWin32's `NTFS_VOLUME_DATA_BUFFER`; extent bounds use `TotalClusters`, including allocated clusters. The scanner bootstraps the MFT through `FSCTL_GET_NTFS_FILE_RECORD`, resolves resident or nonresident MFT attribute-list references, validates complete MFT data runs, and reads mapped records in 1 MiB batches. Filesystem-returned records have already had their update-sequence trailers restored and use a separate read-only parser entry point. Raw records validate all trailers before restoring any bytes, using NTFS's fixed 512-byte stride regardless of the volume sector size. Failed raw validation never falls back to filesystem-record parsing. Invalid/torn records and unresolved stream extensions remain unavailable for custom movement.

The scanner is neither a filesystem driver nor an alternative write implementation. Raw NTFS access is **read only**. Active-volume metadata can change while scanning, so every candidate is treated as provisional.

## Planner

Free intervals live in an array-backed randomized treap augmented with the maximum free-run length in each subtree. First-fit and address lookup are expected O(log n); pathological trees are not a worst-case bound. The arrays are pooled per worker. The MFT growth zone is removed from eligible free destinations.

Minimum-write planning tries to keep the first extent and move a fitting tail adjacent to it. Otherwise it seeks a wholly free destination within the write budget. Packing can split ranges into smaller available holes. Ordering only places eligible files into currently free regions; it does not evict blockers or guarantee complete global sorting.

A batch contains at most 1,024 moves, each at most 16 MiB. It never recycles newly vacated source space inside the same batch. This sacrifices some packing opportunities but prevents a failed earlier move from invalidating a later destination. Most policies may replan for up to 64 batches; ordering makes one bounded pass to avoid repeated reshuffling. No exclusion-constrained move is handed to the OS optimizer.

## Execution and recovery

For each custom move:

1. Check pause, cancellation, power/idle conditions and time budget.
2. Flush an intent record, including identity, VCN, observed source, destination and count.
3. Open by NTFS file identity; named streams are reopened with their stream suffix. Verify identity, current path, attributes, link count, selection and exclusions.
4. Requery retrieval pointers and verify the intended source range.
5. Submit `FSCTL_MOVE_FILE`. The filesystem arbitrates allocation races.
6. Requery retrieval pointers and verify the destination before recording success.
7. Update the logical model and periodically publish a bounded map snapshot.

Native failures can be ambiguous. The job records `reconcile-required`, prevents repeated attempts for the file and performs a final fresh scan where possible. Journals are diagnostic evidence, not an executable redo log. The broker marks interrupted jobs for reanalysis; it never replays saved LCN addresses. Cross-process volume locks and default resource locks prevent competing workers after broker failure. The optional shared-storage override depends on broker-managed per-resource counts; preserving that limit across broker crashes needs additional recovery testing.

The job object always enforces the configured CPU rate and applies committed-memory limits only when a nonzero cap is configured. With a cap, the worker refreshes the CLR memory limit after joining the job and assigns at most 60% of the process cap to its heap. Raw scanning stops at 25% of the configured cap, reserving space for path resolution and the final layout. Zero leaves memory under normal operating-system and runtime management. The worker can also use native background mode. A hard memory cap can still terminate or fail an operation; scans mark their coverage when interrupted by the memory threshold.

ReTRIM/automatic/slab operations call Windows' installed `defrag.exe` with fixed validated switches via `ProcessStartInfo.ArgumentList`. Localized output is displayed as text; success is determined from process exit and subsequent analysis. It is not converted to per-cluster activity. These operations have no exact app-controlled I/O pacing or resumable internal moves.

Virtual-disk pre-zeroing creates a uniquely named, uncompressed, non-sparse, delete-on-close temporary file through the filesystem, retaining allocations during the write phase. It retains guest free-space headroom and honors a write budget. Cancellation removes the file through handle cleanup. ReTRIM is attempted after deletion when capability is reported. Host-side reclaimed capacity remains unknown.

## Boundaries requiring further work

* Native relocation has not been exercised. Elevated read-only C: analysis and its memory-limited partial result have been verified.
* BootExecute is an NT-native execution environment, not ordinary .NET. No early-boot helper is included.
* A startup task is online operation. There is no bundled WinPE environment or offline registry replacement.
* Hard-linked, sparse/compressed/encrypted, reparse-point and ordinary extension-attribute streams are conservative non-movement cases.
* Storage Spaces, SAN, RAID and VHD backing relationships are not completely resolved. Unknown resources serialize by default; manual groups can add constraints, not establish hardware truth.
* There is no foreground-load classifier, wake trigger, multi-session activity agent, disk-latency feedback controller, or dynamic adjustment of running job limits.
* Affinity supports one processor group. CPU Sets and cache/SMT-aware core selection need hardware-specific validation.

## Native API references

* [Defragmenting files and supported streams](https://learn.microsoft.com/en-us/windows/win32/fileio/defragmenting-files)
* [FSCTL_MOVE_FILE and allocation races](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_move_file)
* [Volume allocation bitmap](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_get_volume_bitmap)
* [MFT structure](https://learn.microsoft.com/en-us/windows/win32/fileio/master-file-table)
* [Typed NTFS volume geometry](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-ntfs_volume_data_buffer)
* [Update-sequence restoration and its fixed stride](https://learn.microsoft.com/en-us/windows/win32/devnotes/multi-sector-header)
* [Refreshing managed heap limits after applying a worker cap](https://learn.microsoft.com/en-us/dotnet/api/system.gc.refreshmemorylimit)
* [Job-object CPU rate controls](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_cpu_rate_control_information)
* [Job-object committed-memory limits](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_extended_limit_information)
