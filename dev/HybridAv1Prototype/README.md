# Изолированный AV1 hybrid-прототип (Phase 3A)

Этот development-проект не входит в solution/build/publish приложения и нигде не вызывается из `SaveClip`, `Recorder.Save` или production export strategy. Assembly name `FidelityChecks` использует уже существующий доступ к internal test seams. Production source не меняется.

Зависимости: Windows, существующие .NET/FFmpeg из `.tools`, NVIDIA AV1 NVDEC/NVENC, Python 3.11+ и NumPy. Исходные production snapshots берутся из ignored `artifacts/gop-phase2/benchmark/snapshot-{15,30,60,120}-A-copy-1`. Они необходимы для повторения offline matrix; в Git не включаются. Native fixtures создаются независимо.

Запуск из корня репозитория:

```powershell
.tools/dotnet/dotnet.exe build dev/HybridAv1Prototype/HybridAv1Prototype.csproj -c Release
python dev/HybridAv1Prototype/run_checks.py
python dev/HybridAv1Prototype/benchmark.py
python dev/HybridAv1Prototype/verify_matrix.py
python dev/HybridAv1Prototype/actual_phase2_benchmark.py
python dev/HybridAv1Prototype/extra_checks.py
.tools/dotnet/dotnet.exe dev/HybridAv1Prototype/bin/Release/net10.0-windows/FidelityChecks.dll regress artifacts/hybrid-phase3a/regressions
.tools/dotnet/dotnet.exe dev/HybridAv1Prototype/bin/Release/net10.0-windows/FidelityChecks.dll live artifacts/hybrid-phase3a/live
python dev/HybridAv1Prototype/verify_live.py artifacts/hybrid-phase3a/live
$python = (Get-Command python).Source
.tools/dotnet/dotnet.exe dev/HybridAv1Prototype/bin/Release/net10.0-windows/FidelityChecks.dll concurrent artifacts/hybrid-phase3a/concurrent $python
```

`live` создаёт собственный Recorder, включает обычную запись параллельно Rewind, выполняет production Save после наполнения 30-секундного буфера и планирует ещё один через 15 секунд от accepted QPC. Реальная acceptance может быть позже из-за Windows scheduling и измеряется отдельно. Existing command seam сохраняет immutable owned segments/WAV, исходные args и фактический rewind epoch (initial `-Infinity` sentinel сохраняется как строка JSON). Hybrid в эти Save не подставляется. Output/buffer/retained snapshots находятся только в artifacts; пользовательские settings/queue проверяются byte-for-byte. Для live capture нужен разблокированный рабочий стол.

`concurrent` дополнительно запускает standalone Python hybrid process на первом accepted snapshot **до остановки producer/recording**, затем проверяет оба состояния. Этот процесс создаёт только unpublished artifact; production Save уже завершён и его strategy не меняется. Independent reference comparisons выполняются после native run и не задерживают запланированную вторую acceptance.

## Правила прототипа

* Frozen Snapshot/Plan; accepted/history/previous-cutoff bounds, file identity, CFR packet grid проверяются до обработки. Epoch и PCM относятся к тому же retained snapshot, capture state не перечитывается.
* Неизменённый `Av1SegmentIndex` проверяет Matroska, каждый OBU_FRAME и shown KEY. Требуются одинаковые **все байты av1C/sequence header**, DefaultDuration, TimeScale и Video/Colour properties. Дополнительно `trace_headers` доказывает single operating point, отсутствие timing/decoder model и frame IDs. Другие формы отклоняются.
* Если начало попало внутрь GOP, декодируется только соответствующий сегмент от предыдущего KEY; сохраняются ровно выбранные кадры до следующего proven KEY. Новый GOP начинается shown KEY с тем же sequence header. Текущий producer начинает каждый сегмент с KEY, поэтому packet extraction внутри большого сегмента специально не реализована.
* Raw NUT intermediate раздельно измеряет decode и encode. P010 переводится без изменения samples в planar 10-bit для корректного NUT tag и обратно в P010 перед NVENC. Исходные color metadata переносятся явно и затем сверяются. Это исследовательский overhead, не предлагаемая production упаковка.
* Tail prefix копируется по точному packet count только в проверенном one-shown-frame, low-delay subset. Hidden/show-existing/unknown syntax запрещены. Если выбранных кадров меньше, чем требует legacy F6/CFR rounding, план отклоняется — последний кадр не дублируется ради eligibility.
* `setts` нормализует packet PTS/DTS по единой CFR-сетке; применим только после proof «один пакет = один shown frame, без reorder». Payload SHA-256 остаются исходными. Это устраняет двойное округление при concat промежуточного head.
* Audio WAV уже содержит production PCM selection/gain/mute/routing. AAC/mix/limiter соответствует reference; prototype не меняет microphone/system/Discord processing. Fixture WAV trimming служит только созданию детерминированных входов.
* Полное декодирование всех streams, exact packet hashes/PTS и independent raw-frame/AAC comparisons обязательны. Boundary pixels lossy: проверяются frame-ID fixtures, content alignment и PSNR. Static adjacent frames явно отмечаются как неоднозначные при content matching.
* Все outputs называются `unpublished.mkv` в ignored artifacts. Здесь нет atomic publication, UI success, history commit/rollback или новой production fallback strategy. Отказ не меняет production state. Производственные NVDEC/software fallback остаются неизменными и отдельно проходят regressions.

Offline timings не являются latency `SaveClip`. Full-NVDEC контроль дополнительно проходит full-decode validation для честного сравнения гарантий; production NVDEC path использует свой прежний probe, поэтому processing/validation timings приводятся отдельно.

`benchmark.py` содержит aligned copy-control прототипа с дополнительным tail intermediate и общей CFR-нормализацией. `actual_phase2_benchmark.py` отдельно запускает именно committed `RewindPacketCopy.Inspect/Run` из приложения с неизменённой validation/fallback архитектурой на тех же snapshots. В отчёте эти группы не смешиваются. `verify_matrix.py` декодирует каждый из 48 timed outputs, сравнивает counts, video/AAC payloads и PTS с первым independently verified run. Byte-equivalence измеряется, а не предполагается.

Пока не поддерживаются: произвольные encoder/metadata changes, temporal/spatial layers, decoder-model timing, frame IDs, hidden/show-existing frames, AV1 Main12/другие профили, video BlockGroup/lacing, неоднозначное legacy CFR rounding, KEY внутри несегментированного файла. Отказ является ожидаемым результатом исследования, а не основанием ослаблять validation.
