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
