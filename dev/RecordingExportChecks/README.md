# Проверки финализации обычной записи

Standalone development harness; приложение не ссылается на этот проект. Existing `FidelityChecks` internal seam используется для проверки настоящего `RecordingSession.FinalizeFiles`, а не mock save. Все источники, outputs и отдельная compression queue находятся в ignored `artifacts/unified-stage1`. Пользовательские settings/queue не изменяются. Не запускайте native suites одновременно с пользовательским Rewind.

Требования: Windows, NVIDIA AV1 NVDEC/NVENC, локальные `.tools/dotnet` и production FFmpeg, Python + NumPy. Для независимой проверки нужен прежний laboratory full FFmpeg с libdav1d; production binary не заменяется.

```powershell
.tools/dotnet/dotnet.exe build dev/RecordingExportChecks/RecordingExportChecks.csproj -c Release
python dev/RecordingExportChecks/checks.py
python dev/RecordingExportChecks/extra_checks.py
python dev/RecordingExportChecks/format_checks.py
python dev/RecordingExportChecks/timestamp_checks.py
python dev/RecordingExportChecks/checks.py benchmark
python dev/RecordingExportChecks/verify_benchmarks.py
.tools/dotnet/dotnet.exe dev/RecordingExportChecks/bin/Release/net10.0-windows/FidelityChecks.dll regress artifacts/unified-stage1/regressions
.tools/dotnet/dotnet.exe dev/RecordingExportChecks/bin/Release/net10.0-windows/FidelityChecks.dll recording artifacts/unified-stage1/recording-regressions
.tools/dotnet/dotnet.exe dev/RecordingExportChecks/bin/Release/net10.0-windows/FidelityChecks.dll edges artifacts/unified-stage1/recording-edges
.tools/dotnet/dotnet.exe dev/RecordingExportChecks/bin/Release/net10.0-windows/FidelityChecks.dll recovery artifacts/unified-stage1/recovery
.tools/dotnet/dotnet.exe dev/RecordingExportChecks/bin/Release/net10.0-windows/FidelityChecks.dll process artifacts/unified-stage1/process
.tools/dotnet/dotnet.exe dev/RecordingExportChecks/bin/Release/net10.0-windows/FidelityChecks.dll phase1-media artifacts/unified-stage1/phase1-media
.tools/dotnet/dotnet.exe dev/RecordingExportChecks/bin/Release/net10.0-windows/FidelityChecks.dll phase2-media artifacts/unified-stage1/phase2-media
.tools/dotnet/dotnet.exe dev/RecordingExportChecks/bin/Release/net10.0-windows/FidelityChecks.dll live artifacts/unified-stage1/live
```

Fixture commands требуют новый output directory и сохраняют прежние результаты. Для повторного запуска задайте `REWIND_RECORDING_TEST_ROOT` внутри repository `artifacts` либо используйте отдельные CLI-каталоги. Источники `checks.py`: frame-ID fixtures из Phase 3A и retained production segments из `live-final/snapshot-0`. Длинные 60/300/600-second offline fixtures повторяют эти исходные сегменты через отдельные hard links; это не 10 минут новой живой записи. PCM — разные детерминированные тоны с периодом intentional microphone silence. Legacy baseline вызывает исходный command через принудительный отказ от copy, используя тот же owned manifest/PCM. Его extra planning overhead виден в timing trace.

Проверки: payload SHA-256, независимый libdav1d decode/framemd5, native packet PTS/DTS, exact frame counts, frame-ID alignment lossy head и decoded AAC samples/PTS. `format_checks.py` отдельно проверяет 1080p60 и Main10. `timestamp_checks.py` проверяет реальные повреждённые MKV timestamps, contiguous prefix после gap и неоднозначное двойное округление fractional concat duration. `extra_checks.py` использует настоящую exclusive file lock и seam ENOSPC без заполнения диска; recovery output всегда вне session directory. Процессные tests действительно прерывают/отменяют FFmpeg и проверяют reaping.

Native `live` создаёт отдельный DXGI Recorder, conventional recording и Rewind; выполняет Save во время записи и одновременно со Stop, удерживает snapshot для независимого анализа, измеряет публикацию/реальный WPF render overlay и выполняет MP4 compression с собственной queue. Success означает опубликованный MKV; MP4 проверяется отдельно. UI harness создаёт только тестовую сцену/production overlay, без изменения дизайна.

`verify_live.py` проверяет сохранённый конкретный 902-frame run в `live-eligible`, создавая отдельный legacy snapshot. Это анализатор сохранённого эксперимента, не универсальный assert для произвольной длительности. Повторный запуск не перезаписывает его `legacy` directory. Native live измеряет production Recorder/overlay в отдельном WPF harness, а не ручной клик в главном окне. Его Start/Stop scheduling повышает воспроизводимость copy-eligible случая, не меняя принятые QPC boundaries; доля eligible случаев этим тестом не измеряется.

Production изменения ограничены recording finalization. `RewindPacketCopy`, `RewindVideoExport`, AV1 index и Phase 3A prototype не меняются. См. ignored `artifacts/unified-stage1/report.md` для результатов и ограничений.

## Lifecycle and test isolation regression

`concurrency` exercises the production Recorder with Stage 1 scheduling: gated
Stop, continuing Rewind/Save/overlay, serialized next Start, and eight concurrent
DisposeAsync callers sharing cleanup before repeated disposal. The standalone
harness redirects ClipFiles fault diagnostics to `<test-root>/diagnostics` before
running any suite. It checks that settings.json, compression-queue.json and the
user errors.log stay unchanged. MainWindow is not constructed by these tests;
its captured Stop owner and ReferenceEquals cleanup guard are reviewed separately.

```powershell
.tools/dotnet/dotnet.exe dev/RecordingExportChecks/bin/Release/net10.0-windows/FidelityChecks.dll concurrency artifacts/lifecycle-checks-new
```

## Unified Export Stage 2

Stage 2 измеряет те же retained 60/300/600-second источники, затем сравнивает
настоящий `FinalizeFiles` с committed Stage 1 (`finalize … stage1`). Это offline
экспорт сохранённых сегментов, не Stop-to-playable живой десятиминутной записи.
Экспериментальный bounded scheduler для eligible записей от 45 секунд запускает до трёх
независимых AAC jobs и один video-copy job, затем timestamp-preserving remux.
Граф аудио, AAC quality, полная validation и retained-source fallback сохранены.
Недостаточный запас свободного места оставляет Stage 1 combined scheduling.
Дополнительные intermediates создаются только в unique attempt-owned directory.
Он выключен по умолчанию: полное сравнение десятиминутных finalizers не подтвердило
выигрыш total latency. CLI `finalize … parallel` / `parallel-<fault>` / benchmark /
parallel checks включают candidate явно; default, `fast`, `stage1` и обычное
приложение сохраняют Stage 1 scheduling. Историческое имя `fast` в сохранённых
Stage 2 benchmark artifacts означает candidate; benchmark передаёт `parallel` явно.

```powershell
python dev/RecordingExportChecks/profile_long.py 60 3
python dev/RecordingExportChecks/profile_long.py 300 3
python dev/RecordingExportChecks/profile_long.py 600 3
python dev/RecordingExportChecks/profile_long.py parallel 60 3
python dev/RecordingExportChecks/profile_long.py parallel 300 3
python dev/RecordingExportChecks/profile_long.py parallel 600 3
python dev/RecordingExportChecks/profile_long.py disk
python dev/RecordingExportChecks/benchmark_long.py
python dev/RecordingExportChecks/benchmark_long.py verify
$env:REWIND_RECORDING_TEST_ROOT = "$PWD/artifacts/unified-stage2/parallel-tests-new"
python dev/RecordingExportChecks/parallel_checks.py
.tools/dotnet/dotnet.exe dev/RecordingExportChecks/bin/Release/net10.0-windows/FidelityChecks.dll concurrency artifacts/unified-stage2/concurrency-new
.tools/dotnet/dotnet.exe dev/RecordingExportChecks/bin/Release/net10.0-windows/FidelityChecks.dll live-long artifacts/unified-stage2/live
.tools/dotnet/dotnet.exe dev/RecordingExportChecks/bin/Release/net10.0-windows/FidelityChecks.dll live-long-candidate artifacts/unified-stage2/live-candidate
python dev/RecordingExportChecks/verify_stage2_live.py
python dev/RecordingExportChecks/verify_stage2_live.py live-candidate
python dev/RecordingExportChecks/verify_stage2_live.py snapshot
.tools/dotnet/dotnet.exe dev/RecordingExportChecks/bin/Release/net10.0-windows/FidelityChecks.dll process-profile artifacts/unified-stage2/process-plumbing-new
.tools/dotnet/dotnet.exe dev/RecordingExportChecks/bin/Release/net10.0-windows/FidelityChecks.dll process-profile artifacts/unified-stage2/process-periods-new periods
```

Benchmarks последовательные: не запускайте capture suites/другие CPU/GPU tests
одновременно с измерением задержки. Профилируется wall time отдельно от суммарного
process CPU time и process I/O; warm cache disk reads не означают raw SSD throughput.
Completed benchmark results переиспользуются. Для нового измерения используйте
новое test-owned имя/каталог; не удаляйте сохранённые источники или runtime data.
`parallel_checks.py` проверяет System/Game routing, fractional trim, exact AAC
payload/sample/PTS/priming/padding equality, worker failure/corruption, cancellation,
exclusive output lock, ENOSPC seam, full-decoding fallback и cleanup после join.
`concurrency` использует искусственную gate только для ownership/lifecycle checks,
не для performance numbers. Проверяет реальные Recorder/Save/overlay и повторный
Dispose, без запуска MainWindow или пользовательской compression queue.
`live-long` измеряет настоящие QPC Stop, publication, WPF render и отдельное MP4
compression, сохраняет snapshot для `verify_stage2_live.py`. Harness перенаправляет
ClipFiles diagnostics в test root; production logging path не меняется.

`ProcessProfile` анализирует ready `300-r2-stage1` output после paired benchmark;
все варианты полностью декодируют 8970 video frames и audio. Сравнивает physical
process lifetime с await elapsed, input pipe, job, readers и stats period, без
изменений production MediaTools. `finalize … inspect-copy` сохраняет собственный
unpublished attempt и native indices внутри своего test root для диагностики
rejection. `verify_stage2_live.py snapshot` проверяет frozen real fractional Stop
в `corrected-live-snapshot`: copied payload/pixel hashes, exact frame count/PTS,
AAC samples/priming/padding и lossy head content. Candidate video-only worker
использует exact plan frame cap: отдельный `-t` способен выдать extra tail packet;
перед publication всё равно нужны прежние packet proof и full decode.

## Compression queue retention regression

`compression-queue` requires a new empty directory under `artifacts`. It uses
explicit owned queue files and isolated diagnostics, and checks hashes of the
actual private runtime files before and after. No MainWindow is constructed.
Fixtures cover 1,000 completed entries, mixed states, 10,000 concurrent terminal
transitions with snapshots, collectible pruned records, persistence/reload,
real NVENC failure/retry, shutdown cancellation, interrupted-job recovery and
byte preservation of original MKV/MP4 files. Cancellation in the production
worker remains Pending for retry; synthetic terminal Cancelled entries are also
retained within the same 50-entry window. Tests need Windows/NVENC and FFmpeg.
An optional third argument selects a separate FFmpeg binary for full decoding.

```powershell
.tools/dotnet/dotnet.exe build dev/RecordingExportChecks/RecordingExportChecks.csproj -c Release --nologo
.tools/dotnet/dotnet.exe dev/RecordingExportChecks/bin/Release/net10.0-windows/FidelityChecks.dll compression-queue artifacts/compression-queue-new
```

## Non-blocking SaveClip queue

`save-queue` uses generated, isolated segmented AV1/PCM fixtures. It covers bounded
FIFO admission, exact source-frame selection/PTS, failed and cancelled predecessors,
active-process cancellation, cancelled NVDEC probe recovery, overload, video/PCM/disk
budgets, eight simultaneous admissions, rollover, gaps, epochs and shutdown. Sources
and queue state are exclusively under the supplied new `artifacts` directory. It
reads hashes of all private runtime files, never writes them. Independent
libdav1d decoding uses `REWIND_TEST_DECODER` if set, otherwise the existing full
FFmpeg decoder lab under
`artifacts/save-profile/decoder-lab/ffmpeg-9.0.2-full_build/bin/ffmpeg.exe`.
The fixture directory must be new and empty. Admission-backlog limits, pending
cancellation, output-volume free space, Stop and eight concurrent disposals are
also covered without starting desktop capture.

`save-queue-live` captures the actual desktop at 1080p60 with isolated output/buffer,
accepts four saves at +0/+1/+2/+3 seconds, preserves owned sources for paired checks,
and exercises conventional recording and screenshots. Run capture/latency suites
sequentially with desktop access. The live test reports diagnostics without claiming
that queueing speeds up FFmpeg or guarantees sub-100ms on other machines. An optional
third argument to `save-queue` selects one fixture mode for defect reproduction.

```powershell
.tools/dotnet/dotnet.exe dev/RecordingExportChecks/bin/Debug/net10.0-windows/FidelityChecks.dll save-queue artifacts/save-queue-new
.tools/dotnet/dotnet.exe dev/RecordingExportChecks/bin/Debug/net10.0-windows/FidelityChecks.dll save-queue-live artifacts/save-queue-live-new
```

`save-latency-live` measures the first save with an idle worker and two more saves
during its real export, using isolated 1080p60 desktop capture and production WPF
overlays. It issues the first request near the middle of a two-second capture
segment after warming a 30-second buffer. Monotonic stage/notification traces and
status assertions are written to the new test directory. This is a programmatic
SaveClip command, not a physical hotkey press or a constructed MainWindow. Stop
other capture/encode workloads for clean performance measurements. Persisted user
settings and compression queue are hashed; a separate active user's rolling buffer
is mutable and is not compared by this live harness. The ordinary `save-queue`
regression retains its full recursive private-file preservation check and also
tests delayed preparation, cancellation and state transitions.

```powershell
.tools/dotnet/dotnet.exe dev/RecordingExportChecks/bin/Release/net10.0-windows/FidelityChecks.dll save-latency-live artifacts/save-latency-new
```

`save-export-live` runs only the first request with the same capture settings and
real notifications. It retains the already owned snapshot and exact FFmpeg arguments
for offline comparisons; its test-only retention time has separate trace marks.
Run fresh processes sequentially to include the session's first capability probe.
`profile_save_export.py <retained-run> <new-artifacts-dir> filters` alternates three
baseline/filter-thread measurements on those identical sources, then independently
compares packet payloads, PTS/DTS, decoded video and AAC. It does not alter production
options. The default experimental buffer variants can fail and are not shipping settings.
