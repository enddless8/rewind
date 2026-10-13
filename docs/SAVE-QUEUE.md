# SaveClip acceptance and FIFO export

`Recorder.AcceptSave()` freezes the command QPC before async scheduling. Its return confirms admission and media ownership reservations; `Completion` confirms validated atomic MKV publication. `ExportStarted` reports entry into the single export worker. Compatibility `Save()` awaits `Completion`. The WPF `saving` guard covers admission only; completion observers remain tracked so shutdown joins them before compression disposal.

Admission is serialized in call order. Under `files`, a request freezes the Rewind epoch, source directories and origins, configured window, last successful cutoff, encoder/backend and closed segment hard links. Bounded PCM leases retain immutable processed audio packets across rollover, mute changes, OFF/ON and producer disposal. Future lease packets are restricted to the original QPC bounds; no export takes a new audio snapshot.

Snapshot ownership completion runs independently of FIFO export. It acquires only the closed tail intersecting the original accepted boundary in the frozen source epochs. Maintenance retains these segments before ring deletion. Closure waits release `files` and are bounded to 12 seconds; missing accepted tails explicitly fail instead of publishing truncated moments. No capture semaphore spans WAV generation, FFmpeg or publication.

Each request owns a candidate interval from `max(accepted - BufferSeconds, epoch, successful cutoff at admission)` to its accepted boundary. FIFO predecessor results choose the effective lower cutoff *within this owned candidate*, without consulting live capture. Successful publication consumes that request's original acceptance boundary. Failure/cancellation leaves the predecessor successful cutoff intact, so the next frozen request can include the failed interval within its originally configured rolling window. An epoch change prevents old completions from consuming new history. Presentation may use a provisional accepted boundary; it never commits the successful cutoff.

There are at most eight outstanding requests (one exporting plus up to seven waiting), one export worker, 2 GiB conservative video reservations and 512 MiB PCM lease reservations. The admission backlog is separately bounded to eight callers before snapshots are allocated. Video budgets count hard links per request and include a 64 MiB closing-segment allowance. PCM budgets count referenced bytes conservatively; only the active exporter additionally allocates joined PCM, bounded by its reserved interval. Unfinished recovery staging is counted against new video reservations across subsequent requests/restarts; retained originals are preserved. Admission checks free disk in both the buffer and output locations for retained sources, WAVs and output. Count/resource/disk overload rejects explicitly; no pending job is evicted. Large windows or many audio tracks can exhaust these budgets before eight jobs are admitted. Limits are internal constants/test seams, not settings or runtime feature flags.

Cancellation tokens apply to pending ownership work and active media child processes; cancelled cached NVDEC probes are discarded. Shutdown closes admission, stops shared capture and joins accepted exports and UI observers. New requests after disposal are rejected before accessing disposed synchronization. Graceful exit drains accepted work and can take as long as the remaining exports; it does not cancel all jobs automatically. PCM joining/WAV generation complete before the next cancellation check. Existing recovery behavior retains a validated MKV if publication fails. Accepted PCM leases are memory ownership, not a new durable crash journal: process/power loss before WAV output is not automatically recoverable. That existing limitation is not represented as a saved clip.

`AcceptedSave.Stage`, `PreparationStarted` and `ExportStarted` distinguish genuine FIFO waiting from preparation and export. The first job with an idle worker shows **Подготовка клипа** while its accepted video tail closes and ownership completes. A job behind an unfinished predecessor shows **Клип в очереди**, even if its independent snapshot preparation has already finished. Dequeue claims the worker and ends that queue wait. Exporting shows **Сохранение клипа**; only successful atomic publication reports completion. Presentation reads the current atomic stage at admission, so a request already dequeued before its UI continuation runs is not falsely labeled queued. Superseded zero-duration transitions need not render, while a real preparation wait stays visible. Export failure removes its pending overlay and the command reports the error. The Save button is released after admission, and a full queue disables it until capacity becomes available. `OutstandingSaves`, `PreparingSaves`, `ProcessingSaves` and `SaveQueueFull` drive the main-window summary.

Opt-in `REWIND_SAVE_TIMINGS_FILE` instrumentation uses `Stopwatch.GetTimestamp()` throughout, including command receipt, the actual request freeze, admission/metadata wait, tail closure, video/PCM ownership, worker availability/dequeue, FFmpeg export start, publication, and notification emission/application/first WPF render/compositor flush. Capture trace distinguishes the producer opening the next segment from the reader observing finalized segment-list ownership. Source QPC ends are arrival-estimated; producer stderr receipt is an observation of encoder/mux completion, not an internal GPU flush timer. JSON append order can differ from event order; sort by monotonic ticks. Diagnostics preserve the accepted window and are off unless explicitly enabled.

Video timestamps are rescaled to AVTB microseconds and trimmed with explicit inclusive `start_pts` / exclusive `end_pts` on the frozen concat timeline. This avoids millisecond rounding dropping an otherwise eligible boundary frame. `trim duration` can include a frame beyond a fractional requested end because duration starts at the first kept frame. Passthrough output and an explicit encoder time base prevent CFR resampling from duplicating frames. Audio generation, offsets, track routing and codecs retain their previous behavior. Packet-copy remains conditional; NVDEC/software fallback remains active; Hybrid remains disabled.

Integration results and reproducible commands are in `artifacts/save-queue-integration-20261013/REPORT.md`. Run the standalone harness in a fresh directory under `artifacts`; it checks all private runtime files for byte preservation and redirects fault diagnostics to test-owned output. `REWIND_TEST_DECODER` can select an existing full FFmpeg binary with libdav1d for independent synthetic output validation. `save-queue-live` remains a separate opt-in live capture suite. These artifacts are ignored and are not distribution files.

A sole idle AV1 job whose frozen lower boundary is provably inside a closed GOP
may preflight NVDEC using its already owned closed sample during tail preparation.
The exporter reuses that same guarded capability task. Possibly packet-copy-eligible
jobs and requests with predecessors keep their lazy checks. Preparation and terminal
cleanup join the preflight before encoding, releasing staging or advancing FIFO;
cancellation reaps its native child and discards the cancelled task. No frame boundary,
codec option, validation or audio routing changes. This overlaps a measured setup
cost; it does not make the full video re-encode faster. First-save latency measurements
and limitations are in `artifacts/save-export-profile-20261013/REPORT.md`.

For GUI manual testing, run `scripts/publish-manual-test.ps1`; the ignored runnable
self-contained Release executable is `artifacts/rewind-manual-test/Rewind.exe`.
It includes the x64 .NET Desktop Runtime and bundled FFmpeg/ffprobe, stages and
smoke-checks the build, checks for running instances before replacement, and preserves
previous builds. Development and harness outputs remain separate.

The manual publisher uses `.tools/ffmpeg-full` (or `-MediaDirectory`) and verifies
libdav1d, AV1 CUVID and NVENC availability. The tested cache contains Gyan FFmpeg
9.0.2 full and same-version ffprobe. This keeps NVDEC for export while replacing
the essentials build's slow libaom decoder used internally for per-segment stream
analysis. Paired exports preserve packet hashes, PTS/DTS, decoded frames and AAC;
the encoder settings and exact trim are unchanged. Ordinary development builds
still resolve their local media tools, so use this tested bundle when comparing
manual latency. Generated tools and runtime binaries are ignored.
