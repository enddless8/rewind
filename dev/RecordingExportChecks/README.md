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
