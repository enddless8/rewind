using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Replay;

internal static class SaveQueueChecks
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    internal static async Task<int> Run(string root, bool live, string? onlyMode = null)
    {
        if (Directory.EnumerateFileSystemEntries(root).Any()) throw new IOException("Use a new, empty fixture directory.");
        var checks = new List<object>(); var measurements = new List<object>();
        void Check(bool ok, string name) { checks.Add(new { ok, name }); if (!ok) throw new Exception(name); }
        var baseline = PrivateFiles();
        Environment.SetEnvironmentVariable("REWIND_SAVE_TIMINGS_FILE", Path.Combine(root, "timing.jsonl"));
        try
        {
            if (live) await Live(root, Check, measurements);
            else await Fixtures(root, Check, measurements, onlyMode);
            Check(baseline.SequenceEqual(PrivateFiles()), "Actual private settings, queue and media unchanged");
            File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = true, checks, measurements }, Json));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = false, error = ex.ToString(), checks, measurements }, Json));
            throw;
        }
        finally { Environment.SetEnvironmentVariable("REWIND_SAVE_TIMINGS_FILE", null); }
    }
    private static string[] PrivateFiles()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "Replay");
        return Directory.Exists(root) ? Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(p => p)
            .Select(p => p + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)))).ToArray() : [];
    }
    private static Settings SettingsFor(string root, bool live = false) => new()
    {
        CaptureMode = "Desktop", HideDisplayBorder = true, AudioMode = "System", MicrophoneId = "off", Discord = false,
        Codec = "AV1", Height = live ? 1080 : 720, Fps = live ? 60 : 30, BufferSeconds = 30,
        Quality = 27, MaxBitrateMbps = 15, GroupByApplication = false,
        OutputDirectory = Path.Combine(root, "output"), BufferDirectory = Path.Combine(root, "buffer")
    };
    private static void Set(Recorder recorder, string name, object value) => typeof(Recorder).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(recorder, value);
    private static JsonElement Metadata(SavedClip clip) => JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.ChangeExtension(clip.Path, ".json")));
    private static async Task Decode(SavedClip clip, Action<bool, string> check)
    {
        var independent = Path.GetFullPath(Environment.GetEnvironmentVariable("REWIND_TEST_DECODER") ?? "artifacts/save-profile/decoder-lab/ffmpeg-9.0.2-full_build/bin/ffmpeg.exe");
        using var process = new Process { StartInfo = new ProcessStartInfo(independent) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var arg in new[] { "-v", "error", "-xerror", "-c:v", "libdav1d", "-i", clip.Path, "-map", "0:v", "-map", "0:a?", "-f", "null", "-" }) process.StartInfo.ArgumentList.Add(arg);
        process.Start(); var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(180));
        var decoded = (Code: process.ExitCode, Output: await output, Error: await error);
        check(decoded.Code == 0 && string.IsNullOrWhiteSpace(decoded.Error), "Independent libdav1d/software audio decode: " + Path.GetFileName(clip.Path));
    }
    private static async Task Fixtures(string root, Action<bool, string> check, List<object> measurements, string? onlyMode)
    {
        await AdmissionLifecycle(root, check);
        var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        var made = await MediaTools.Run("ffmpeg", ["-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30", "-t", "8", "-c:v", "av1_nvenc", "-g", "60", "-bf", "0", "-f", "segment", "-segment_time", "2", "-reset_timestamps", "1", "-segment_list", Path.Combine(source, "segments.csv"), Path.Combine(source, "part-%03d.mkv")]);
        check(made.Code == 0, "Deterministic segmented AV1 fixture generated");
        await PreparationState(root, source, check);
        await PreflightLifecycle(root, source, check);
        foreach (var mode in new[] { "success", "failure", "cancel", "probe-cancel", "timeout", "overload", "resource", "epoch", "rollover", "shutdown", "stop", "dispose-concurrent", "queued-cancel", "gap", "active-cancel", "video-resource", "disk", "output-disk", "middle-failure", "burst", "closure-timeout", "failed-rollover", "failed-epoch", "publish-failure" }.Where(m => onlyMode == null || onlyMode == m))
        {
            var caseRoot = Path.Combine(root, mode); var settings = SettingsFor(caseRoot);
            Directory.CreateDirectory(settings.BufferDirectory);
            var ring = Path.Combine(settings.BufferDirectory, "ring"); Directory.CreateDirectory(ring);
            foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(ring, Path.GetFileName(file)));
            await using var recorder = new Recorder(settings, null, Native.Displays().First(d => d.Primary));
            if (mode == "gap")
            {
                var list = Path.Combine(ring, "segments.csv");
                File.WriteAllLines(list, File.ReadAllLines(list).Select((line, i) =>
                {
                    var fields = line.Split(',');
                    if (i >= 1) { fields[1] = (double.Parse(fields[1], CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture); fields[2] = (double.Parse(fields[2], CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture); }
                    return string.Join(',', fields);
                }).ToArray());
            }
            var origin = Native.Clock - (mode == "closure-timeout" ? 10 : 4);
            Set(recorder, "origin", origin); Set(recorder, "videoDirectory", ring); Set(recorder, "<Encoder>k__BackingField", "av1_nvenc");
            var sourceTimeline = Recorder.ReadSegments(ring).Select(s => s with { Start = s.Start + origin, End = s.End + origin }).ToArray();
            var audio = new AudioTrack("System", false, null, null, 50, -50);
            for (var i = 0; i < 800; i++)
            {
                var data = new byte[1920];
                for (var j = 0; j < data.Length; j += 2) { var v = (short)(Math.Sin((i * 480 + j / 4) * 2 * Math.PI * 440 / 48000) * 10000); data[j] = (byte)v; data[j + 1] = (byte)(v >> 8); }
                audio.ProcessPacket(new(origin + i * .01, data), origin + i * .01);
            }
            ((List<AudioTrack>)typeof(Recorder).GetField("tracks", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(recorder)!).Add(audio);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            string? firstStaging = null, middleStaging = null; FileStream? lockedFinal = null; int active = 0, maxActive = 0;
            async Task<(int Code, string Output, string Error)> Run(string tool, IEnumerable<string> args, int timeout)
            {
                var input = args.ToArray();
                var staging = input.Select(Path.GetDirectoryName).FirstOrDefault(p => p != null && Path.GetFileName(p).StartsWith(".saving-"));
                if (mode == "middle-failure" && tool == "ffmpeg" && firstStaging != null && staging != null && staging != firstStaging)
                {
                    middleStaging ??= staging;
                    if (staging == middleStaging) return (1, "", "Injected middle export failure");
                }
                if (mode == "active-cancel" && tool == "ffmpeg" && staging != null && (firstStaging == null || staging == firstStaging))
                {
                    firstStaging = staging;
                    var countNow = Interlocked.Increment(ref active); maxActive = Math.Max(maxActive, countNow);
                    try
                    {
                        var process = MediaTools.Run(tool, new[] { "-re" }.Concat(input), timeout, bindLifetime: true);
                        entered.TrySetResult();
                        return await process;
                    }
                    finally { Interlocked.Decrement(ref active); }
                }
                if (mode == "probe-cancel" && input.Contains("copy")) return (1, "", "Force same-snapshot fallback for cancelled capability regression");
                if (tool == "ffmpeg" && staging != null)
                {
                    firstStaging ??= staging;
                    if (mode == "publish-failure" && lockedFinal == null && staging == firstStaging)
                    {
                        Directory.CreateDirectory(settings.OutputDirectory);
                        lockedFinal = new FileStream(Path.Combine(settings.OutputDirectory, Path.GetFileName(staging)[8..] + ".mkv"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                    }
                    if (staging == firstStaging)
                    {
                        entered.TrySetResult(); await release.Task;
                        if (mode is "failure" or "failed-rollover" or "failed-epoch" || mode == "probe-cancel" && input.Contains("copy")) return (1, "", "Injected first export failure");
                        if (mode == "timeout") throw new TimeoutException("Injected first timeout");
                    }
                }
                var count = Interlocked.Increment(ref active); maxActive = Math.Max(maxActive, count);
                try { return await MediaTools.Run(tool, input, timeout, bindLifetime: true); }
                finally { Interlocked.Decrement(ref active); }
            }
            recorder.PacketCopy = new(Run); recorder.VideoExport = new(run: Run);
            if (mode == "probe-cancel")
            {
                int probe = 0;
                recorder.VideoExport = new(supportOverride: async _ =>
                {
                    if (++probe == 1) { entered.TrySetResult(); await release.Task; throw new OperationCanceledException("Injected cancelled first capability probe"); }
                    return true;
                }, run: Run);
            }
            using var cancel = new CancellationTokenSource();
            try
            {
                using var timing = SaveTiming.Begin(Guid.NewGuid());
                var watch = Stopwatch.StartNew(); var first = await recorder.AcceptSave(mode is "cancel" or "active-cancel" ? cancel.Token : default);
                measurements.Add(new { mode, AcceptanceMs = watch.Elapsed.TotalMilliseconds, first.AcceptedQpc });
                if (mode == "closure-timeout")
                {
                    bool timedOut = false;
                    try { await first.Completion; } catch (TimeoutException) { timedOut = true; }
                    check(timedOut && recorder.OutstandingSaves == 0 && recorder.SaveReservations == (0, 0), "Missing accepted tail explicitly fails and releases ownership without publication");
                    check(!Directory.Exists(settings.OutputDirectory), "Incomplete accepted tail never reports/publishes saved clip");
                    continue;
                }
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                check(first.Stage == SaveClipStage.Exporting && await first.PreparationStarted, mode + ": worker reports preparation followed by exporting");
                await Task.Delay(90);
                using var secondCancellation = new CancellationTokenSource();
                var second = await recorder.AcceptSave(mode == "queued-cancel" ? secondCancellation.Token : default);
                check(!first.Completion.IsCompleted && !second.Completion.IsCompleted, mode + ": second accepted while first worker gated");
                check(!second.ExportStarted.IsCompleted && recorder.ProcessingSaves == 1, mode + ": waiting request cannot start before FIFO predecessor");
                check(second.Stage == SaveClipStage.Queued && !second.PreparationStarted.IsCompleted, mode + ": queued means a real predecessor owns the worker");
                AcceptedSave? third = mode is "middle-failure" or "queued-cancel" ? await recorder.AcceptSave() : null;
                if (mode == "queued-cancel") secondCancellation.Cancel();
                if (mode == "burst")
                {
                    using var burstCancellation = new CancellationTokenSource();
                    var pending = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => recorder.AcceptSave(burstCancellation.Token)));
                    check(recorder.OutstandingSaves == 8, "Concurrent asynchronous admissions reach exactly default limit of eight");
                    bool full = false; try { await recorder.AcceptSave(); } catch (InvalidOperationException) { full = true; }
                    check(full, "Ninth request explicitly rejected without evicting accepted requests");
                    check(!recorder.CanAcceptSave, "Presentation disables admission when the job limit is reached");
                    burstCancellation.Cancel();
                    release.TrySetResult();
                    foreach (var accepted in pending) { try { await accepted.Completion; check(false, "Cancelled pending request cannot publish"); } catch (OperationCanceledException) { } }
                }
                var reservations = recorder.SaveReservations;
                if (mode is "overload" or "resource" or "video-resource" or "disk" or "output-disk")
                {
                    if (mode == "overload") recorder.SaveJobLimit = 2;
                    else if (mode == "resource") recorder.SavePcmLimit = reservations.Pcm;
                    else if (mode == "video-resource") recorder.SaveVideoLimit = reservations.Video;
                    else recorder.FreeSpace = path => mode == "output-disk" && path != settings.OutputDirectory ? long.MaxValue : 255L * 1024 * 1024;
                    bool rejected = false;
                    try { await recorder.AcceptSave(); } catch (Exception ex) when (ex is InvalidOperationException or IOException) { rejected = true; }
                    check(rejected && recorder.OutstandingSaves == 2 && recorder.SaveReservations == reservations, mode + ": explicit rejection has no queue/cutoff/ownership side effects");
                }
                if (mode is "epoch" or "failed-epoch") { await recorder.SetRewindEnabled(false); await recorder.SetRewindEnabled(true); }
                if (mode is "rollover" or "failed-rollover")
                {
                    recorder.RewindByteLimitForTest = 1;
                    var maintenance = Stopwatch.StartNew();
                    // Maintain deliberately skips a contended metadata lock. The
                    // second snapshot may still own it immediately after admission.
                    do { await recorder.Maintain(); if (Recorder.ReadSegments(ring).Count == 0) break; await Task.Delay(20); }
                    while (maintenance.Elapsed < TimeSpan.FromSeconds(3));
                    check(Recorder.ReadSegments(ring).Count == 0, "Ring entries removed while snapshots retain video");
                }
                if (mode is "cancel" or "active-cancel") cancel.Cancel();
                Task? closing = null;
                if (mode == "stop")
                {
                    await recorder.Stop();
                    check(!first.Completion.IsCompleted && !second.Completion.IsCompleted, "Recorder Stop preserves independently owned accepted exports");
                    bool rejected = false; try { await recorder.AcceptSave(); } catch (InvalidOperationException) { rejected = true; }
                    check(rejected, "Stopped recorder rejects new saves while accepted jobs drain");
                }
                if (mode is "shutdown" or "dispose-concurrent")
                {
                    closing = recorder.DisposeAsync().AsTask(); await Task.Delay(50);
                    if (mode == "dispose-concurrent")
                    {
                        var disposals = new Task[8];
                        Parallel.For(0, disposals.Length, i => disposals[i] = recorder.DisposeAsync().AsTask());
                        check(disposals.All(t => ReferenceEquals(t, closing)), "Eight simultaneous disposals share the queued-save cleanup task");
                    }
                    check(!closing.IsCompleted, "Shutdown joins accepted jobs before ownership disposal");
                    bool rejected = false; try { await recorder.AcceptSave(); } catch (InvalidOperationException) { rejected = true; }
                    check(rejected, "Shutdown explicitly rejects new saves");
                }
                release.TrySetResult();
                SavedClip? firstClip = null;
                try { firstClip = await first.Completion; check(mode is not ("failure" or "failed-rollover" or "failed-epoch" or "publish-failure" or "cancel" or "active-cancel" or "probe-cancel" or "timeout"), mode + ": first export published"); }
                catch (Exception ex) when (mode is "failure" or "failed-rollover" or "failed-epoch" or "publish-failure" or "cancel" or "active-cancel" or "probe-cancel" or "timeout")
                { check(mode is "cancel" or "active-cancel" or "probe-cancel" ? ex is OperationCanceledException : mode == "timeout" ? ex is TimeoutException : ex is IOException, mode + ": correct terminal failure without false success"); }
                SavedClip secondClip;
                if (mode == "queued-cancel")
                {
                    bool cancelled = false; try { await second.Completion; } catch (OperationCanceledException) { cancelled = true; }
                    check(cancelled && !await second.ExportStarted, "Cancelled waiting job never enters the exporter");
                    secondClip = await third!.Completion;
                    var recovered = Metadata(secondClip);
                    check(recovered.GetProperty("ActualStart").GetDouble() >= first.AcceptedQpc && recovered.GetProperty("ActualStart").GetDouble() < second.AcceptedQpc, "Successor recovers cancelled waiting interval from its frozen snapshot");
                    second = third;
                }
                else if (mode == "middle-failure")
                {
                    bool failed = false; try { await second.Completion; } catch (IOException) { failed = true; }
                    check(failed, "Middle failure does not advance successful cutoff");
                    // The seam failed only the middle job; restore real commands for the frozen third snapshot.
                    secondClip = await third!.Completion;
                    var recovered = Metadata(secondClip);
                    check(recovered.GetProperty("ActualStart").GetDouble() >= first.AcceptedQpc && recovered.GetProperty("ActualStart").GetDouble() < second.AcceptedQpc, "Third frozen job recovers failed middle interval without duplicating published first clip");
                    second = third;
                }
                else secondClip = await second.Completion; var secondMeta = Metadata(secondClip);
                check(Math.Abs(secondMeta.GetProperty("AcceptedSaveBoundary").GetDouble() - second.AcceptedQpc) < 1e-8, mode + ": original second QPC preserved");
                if (mode == "gap") check(Metadata(firstClip!).GetProperty("RemovedCaptureGapSeconds").GetDouble() > .9, "Frozen video/audio intervals preserve and account for capture gap");
                if (firstClip != null)
                {
                    check(firstClip.Path != secondClip.Path && File.Exists(firstClip.Path), mode + ": requests publish distinct files without overwriting predecessor");
                    check(secondMeta.GetProperty("ActualStart").GetDouble() >= first.AcceptedQpc, mode + ": no history before successful predecessor cutoff");
                    await Decode(firstClip, check);
                    await ExactFrames(firstClip, sourceTimeline, check);
                }
                else check(secondMeta.GetProperty("ActualStart").GetDouble() < first.AcceptedQpc - 1, mode + ": failed/cancelled predecessor history recovered from frozen second snapshot");
                await Decode(secondClip, check);
                await ExactFrames(secondClip, sourceTimeline, check);
                if (closing != null)
                {
                    await closing; await recorder.DisposeAsync();
                    bool rejected = false; try { await recorder.AcceptSave(); } catch (InvalidOperationException) { rejected = true; }
                    check(rejected && !recorder.CanAcceptSave, mode + ": completed disposal safely rejects new saves and presentation queries");
                }
                check(recorder.OutstandingSaves == 0 && recorder.ProcessingSaves == 0 && recorder.SaveReservations == (0, 0), mode + ": all resource reservations released");
                check(maxActive == 1, mode + ": single export process at a time");
                if (mode == "publish-failure")
                {
                    var retained = Directory.GetDirectories(settings.BufferDirectory, ".saving-*");
                    check(retained.Length == 1 && File.Exists(Path.Combine(retained[0], "clip.mkv")), "Validated original retained for recovery after publication failure");
                    await Decode(new(Path.Combine(retained[0], "clip.mkv"), 0, []), check);
                    var recoveredBytes = Directory.GetFiles(retained[0], "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);
                    var cutoff = Metadata(secondClip).GetProperty("AcceptedSaveBoundary").GetDouble();
                    var fresh = Recorder.ReadSegments(ring).Where(s => s.End + origin > cutoff && s.Start + origin < Native.Clock).Sum(s => new FileInfo(s.Path).Length) + 64L * 1024 * 1024;
                    recorder.SaveVideoLimit = fresh + recoveredBytes - 1;
                    bool rejected = false; try { await recorder.AcceptSave(); } catch (InvalidOperationException) { rejected = true; }
                    check(rejected && recorder.OutstandingSaves == 0, "Recovery media remains charged to retention budget on future admission");
                }
                else check(Directory.GetDirectories(settings.BufferDirectory, ".saving-*").Length == 0, mode + ": staging ownership cleaned");
                if (mode is "epoch" or "failed-epoch") check((double)typeof(Recorder).GetField("rewindSaveCutoff", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(recorder)! == double.NegativeInfinity, "Old epoch publication cannot consume new epoch history");
            }
            finally { release.TrySetResult(); lockedFinal?.Dispose(); }
        }
        // PCM leases keep immutable source bytes and mute flags through retention resets.
        using var mic = new AudioTrack("Microphone", true, null, null, 2, -50);
        mic.ProcessPacket(new(10, new byte[1920]), 10);
        using var lease = mic.RetainPcm(9, 12, 8192);
        mic.SetRetention(0); mic.ProcessPacket(new(11, new byte[1920]), 11); mic.ProcessPacket(new(13, new byte[1920]), 13);
        lease.Dispose(); check(lease.Packets.Count == 2 && lease.Bytes == 3840, "PCM lease survives retention resets and excludes out-of-interval future packets");
    }
    private static async Task AdmissionLifecycle(string root, Action<bool, string> check)
    {
        await using var recorder = new Recorder(SettingsFor(Path.Combine(root, "admission")), null, Native.Displays().First(d => d.Primary));
        var metadata = (SemaphoreSlim)typeof(Recorder).GetField("files", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(recorder)!;
        await metadata.WaitAsync();
        using var cancellation = new CancellationTokenSource();
        var pending = Enumerable.Range(0, 8).Select(_ => recorder.AcceptSave(cancellation.Token)).ToArray();
        try
        {
            bool rejected = false; try { await recorder.AcceptSave(); } catch (InvalidOperationException) { rejected = true; }
            check(rejected && pending.All(t => !t.IsCompleted), "Admission backlog bounded to eight while metadata is unavailable");
            cancellation.Cancel();
        }
        finally { metadata.Release(); }
        foreach (var task in pending)
        {
            bool cancelled = false; try { await task; } catch (OperationCanceledException) { cancelled = true; }
            check(cancelled, "Cancelled admission releases its FIFO slot without reserving media");
        }
        check(recorder.OutstandingSaves == 0 && recorder.SaveReservations == (0, 0), "Cancelled admission backlog leaves no retained data");
        await recorder.DisposeAsync();
        bool closed = false; try { await recorder.AcceptSave(); } catch (InvalidOperationException) { closed = true; }
        check(closed && !recorder.CanAcceptSave, "Save after disposal rejects without touching a disposed semaphore");
    }
    private static async Task PreparationState(string root, string source, Action<bool, string> check)
    {
        foreach (var cancelPreparation in new[] { false, true })
        {
            var settings = SettingsFor(Path.Combine(root, cancelPreparation ? "preparation-cancel" : "preparation"));
            var ring = Path.Combine(settings.BufferDirectory, "ring"); Directory.CreateDirectory(ring);
            foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(ring, Path.GetFileName(file)));
            var manifest = Path.Combine(ring, "segments.csv"); var closed = File.ReadAllLines(manifest);
            File.WriteAllLines(manifest, closed.Take(2));
            await using var recorder = new Recorder(settings, null, Native.Displays().First(d => d.Primary));
            // This test-owned CPU process keeps Running true while the fixture
            // deliberately withholds its tail's completed segment-list entry.
            var producer = MediaTools.Create("ffmpeg", ["-v", "error", "-re", "-f", "lavfi", "-i", "color=s=64x64:r=30", "-f", "null", "-"]);
            producer.Start(); Set(recorder, "video", producer);
            Set(recorder, "origin", Native.Clock - 4); Set(recorder, "videoDirectory", ring); Set(recorder, "<Encoder>k__BackingField", "av1_nvenc");
            using var cancel = new CancellationTokenSource();
            try
            {
                var request = await recorder.AcceptSave(cancel.Token);
                check(await request.PreparationStarted.WaitAsync(TimeSpan.FromSeconds(5)), "Idle worker claims preparation immediately");
                await Task.Delay(100);
                check(request.Stage == SaveClipStage.Preparing && recorder.PreparingSaves == 1 && recorder.ProcessingSaves == 0 && !request.ExportStarted.IsCompleted,
                    "Unclosed tail is preparing, never queued or exporting");
                if (cancelPreparation)
                {
                    cancel.Cancel(); bool cancelled = false;
                    try { await request.Completion; } catch (OperationCanceledException) { cancelled = true; }
                    check(cancelled && request.Stage == SaveClipStage.Cancelled && !await request.ExportStarted,
                        "Preparation cancellation reaches terminal state without entering FFmpeg export");
                }
                else
                {
                    File.WriteAllLines(manifest, closed);
                    var clip = await request.Completion;
                    check(request.Stage == SaveClipStage.Completed && Math.Abs(Metadata(clip).GetProperty("AcceptedSaveBoundary").GetDouble() - request.AcceptedQpc) < 1e-8,
                        "Tail completion preserves the original request time and publishes successfully");
                    await Decode(clip, check);
                }
                check(recorder.OutstandingSaves == 0 && recorder.PreparingSaves == 0 && recorder.ProcessingSaves == 0 && recorder.SaveReservations == (0, 0),
                    "Both preparation outcomes release worker state and resource reservations");
            }
            finally { await recorder.DisposeAsync(); }
        }
    }
    private static async Task PreflightLifecycle(string root, string source, Action<bool, string> check)
    {
        foreach (var cancelProbe in new[] { false, true })
        {
            var settings = SettingsFor(Path.Combine(root, cancelProbe ? "preflight-cancel" : "preflight"));
            settings.BufferSeconds = 15;
            var ring = Path.Combine(settings.BufferDirectory, "ring"); Directory.CreateDirectory(ring);
            var originals = Directory.GetFiles(source, "*.mkv").Order().ToArray();
            var entries = Enumerable.Range(0, 9).Select(i => $"part-{i:D3}.mkv,{i*2},{i*2+2}").ToArray();
            for (var i = 0; i < 9; i++) File.Copy(originals[i % originals.Length], Path.Combine(ring, $"part-{i:D3}.mkv"));
            var manifest = Path.Combine(ring, "segments.csv"); File.WriteAllLines(manifest, entries.Take(8));
            await using var recorder = new Recorder(settings, null, Native.Displays().First(d => d.Primary));
            var producer = MediaTools.Create("ffmpeg", ["-v", "error", "-re", "-f", "lavfi", "-i", "color=s=64x64:r=30", "-f", "null", "-"]);
            producer.Start(); Set(recorder, "video", producer);
            Set(recorder, "origin", Native.Clock - 16); Set(recorder, "videoDirectory", ring); Set(recorder, "<Encoder>k__BackingField", "av1_nvenc");
            using var cancel = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int probes = 0; string? retainedSample = null;
            var baselineProcesses = Process.GetProcessesByName("ffmpeg").Select(p => { using (p) return p.Id; }).ToHashSet();
            recorder.VideoExport = new(supportOverride: async sample =>
            {
                if (++probes > 1) return true;
                retainedSample = sample;
                entered.TrySetResult();
                if (cancelProbe)
                    await MediaTools.Run("ffmpeg", ["-v", "error", "-re", "-f", "lavfi", "-i", "color=s=64x64:r=30", "-f", "null", "-"], 30, bindLifetime: true);
                else await release.Task;
                return true;
            });
            try
            {
                var accepted = await recorder.AcceptSave(cancel.Token);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                check(accepted.Stage == SaveClipStage.Preparing && !accepted.ExportStarted.IsCompleted && File.Exists(retainedSample),
                    "Provably unaligned idle save preflights only an already owned closed sample during tail preparation");
                if (cancelProbe)
                {
                    await Task.Delay(100); cancel.Cancel(); bool cancelled = false;
                    try { await accepted.Completion; } catch (OperationCanceledException) { cancelled = true; }
                    check(cancelled && !File.Exists(retainedSample) && !Process.GetProcessesByName("ffmpeg").Any(p => { using (p) return !baselineProcesses.Contains(p.Id); }),
                        "Cancellation joins/reaps the real preflight child before releasing its owned sample");
                    File.WriteAllLines(manifest, entries);
                    var next = await recorder.AcceptSave(); var recovered = await next.Completion;
                    check(probes == 2 && next.Stage == SaveClipStage.Completed, "Cancelled preflight cache is discarded and the next save retries successfully");
                    await Decode(recovered, check);
                }
                else
                {
                    File.WriteAllLines(manifest, entries); await Task.Delay(100);
                    check(!accepted.ExportStarted.IsCompleted && File.Exists(retainedSample), "Tail readiness cannot race preflight sample cleanup or start another export process");
                    release.TrySetResult(); var clip = await accepted.Completion;
                    check(probes == 1 && accepted.Stage == SaveClipStage.Completed, "Export reuses the preflight capability result without a duplicate probe");
                    await Decode(clip, check);
                }
                check(recorder.OutstandingSaves == 0 && recorder.SaveReservations == (0, 0), "Preflight paths drain resource reservations");
            }
            finally { cancel.Cancel(); release.TrySetResult(); await recorder.DisposeAsync(); }
        }
    }
    private static async Task ExactFrames(SavedClip clip, Segment[] sources, Action<bool, string> check)
    {
        var metadata = Metadata(clip);
        var start = metadata.GetProperty("ActualStart").GetDouble();
        var end = metadata.GetProperty("ActualEnd").GetDouble();
        // Independent expected source grid: fixture has 60 original frames per
        // 2-second segment, Matroska timestamps rounded to milliseconds.
        var expectedPts = new List<double>(); double cursor = 0;
        foreach (var s in sources.OrderBy(s => s.Start))
        {
            foreach (var n in Enumerable.Range(0, 60))
            {
                var pts = Math.Round(n * 1000.0 / 30, MidpointRounding.AwayFromZero) / 1000;
                if (s.Start + pts >= start && s.Start + pts < end) expectedPts.Add(cursor + pts);
            }
            cursor += s.End - s.Start;
        }
        var probe = await MediaTools.Run("ffprobe", ["-v", "error", "-select_streams", "v:0", "-show_packets", "-show_entries", "packet=pts,dts", "-of", "json", clip.Path]);
        using var parsed = JsonDocument.Parse(probe.Output);
        var packets = parsed.RootElement.GetProperty("packets").EnumerateArray().ToArray();
        check(probe.Code == 0 && packets.Length == expectedPts.Count, "Exact frozen source frame count; no duration/resampler extra frame: " + Path.GetFileName(clip.Path));
        check(packets.Select((p, i) => p.GetProperty("pts").GetInt64() == p.GetProperty("dts").GetInt64() && p.GetProperty("pts").GetInt64() == (long)Math.Round((expectedPts[i] - expectedPts[0]) * 1000, MidpointRounding.AwayFromZero)).All(x => x), "Exact normalized source frame PTS/DTS: " + Path.GetFileName(clip.Path));
    }
    private static async Task Live(string root, Action<bool, string> check, List<object> measurements)
    {
        var settings = SettingsFor(root, true);
        var label = new System.Windows.Controls.TextBlock { FontSize = 34, Foreground = System.Windows.Media.Brushes.White };
        var window = new System.Windows.Window { Title = "Rewind queue acceptance validation", Width = 700, Height = 350, Content = label, Background = System.Windows.Media.Brushes.DarkSlateBlue, ShowActivated = false, Topmost = true };
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) }; int tick = 0;
        var beats = new List<double>(); var watch = Stopwatch.StartNew();
        timer.Tick += (_, _) => { label.Text = $"{++tick:D7}  {DateTime.Now:HH:mm:ss.fff}"; beats.Add(watch.Elapsed.TotalMilliseconds); };
        window.Show(); timer.Start();
        var stages = new List<object>(); var commands = new List<object>();
        void Observe(Guid id, string stage, double elapsed, object? details) { lock (stages) stages.Add(new { id, stage, elapsed, details }); }
        SaveTiming.Observed += Observe;
        try
        {
            await Task.Delay(400);
            await using var recorder = new Recorder(settings, null, Native.Displays().First(d => d.Primary));
            async Task<(int Code, string Output, string Error)> Run(string tool, IEnumerable<string> input, int timeout)
            {
                var args = input.ToArray();
                lock (commands) commands.Add(new { tool, args });
                // Preserve the exact owned sources and WAVs solely for independent paired decoding.
                var staging = args.Select(Path.GetDirectoryName).FirstOrDefault(p => p != null && Path.GetFileName(p).StartsWith(".saving-"));
                if (tool == "ffmpeg" && staging != null)
                {
                    var destination = Path.Combine(root, "retained", Path.GetFileName(staging));
                    if (!Directory.Exists(destination))
                    {
                        Directory.CreateDirectory(destination);
                        foreach (var file in Directory.GetFiles(staging)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
                    }
                }
                return await MediaTools.Run(tool, args, timeout, bindLifetime: true);
            }
            recorder.PacketCopy = new(Run); recorder.VideoExport = new(run: Run);
            await recorder.Start(); await recorder.StartRecording();
            await Task.Delay(32500);
            var requests = new List<AcceptedSave>(); var timings = new List<IDisposable?>();
            try
            {
                double initial = Native.Clock;
                for (var i = 0; i < 4; i++)
                {
                    if (i > 0) await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, initial + i - Native.Clock)));
                    var timing = SaveTiming.Begin(Guid.NewGuid()); timings.Add(timing);
                    timing?.Mark("command-accepted");
                    var sw = Stopwatch.StartNew(); var request = await recorder.AcceptSave(); requests.Add(request);
                    measurements.Add(new { Save = i + 1, AcceptanceMs = sw.Elapsed.TotalMilliseconds, request.AcceptedQpc, FirstStillExporting = !requests[0].Completion.IsCompleted, Outstanding = recorder.OutstandingSaves });
                    if (i > 0) check(!requests[0].Completion.IsCompleted, "Real rapid Save +" + i + "s accepted before first publication");
                    await recorder.Maintain();
                }
                var screenshot = await ScreenshotCapture.Take(new("Desktop", true, settings.Fps, null, Native.Displays().First(d => d.Primary), settings.OutputDirectory, false, null));
                check(File.Exists(screenshot.Path), "Real screenshot while save jobs pending");
                for (var i = 0; i < requests.Count; i++)
                {
                    var clip = await requests[i].Completion; var metadata = Metadata(clip);
                    measurements.Add(new { Save = i + 1, clip, metadata });
                    check(Math.Abs(metadata.GetProperty("AcceptedSaveBoundary").GetDouble() - requests[i].AcceptedQpc) < 1e-8, "Real original acceptance QPC " + i);
                    if (i > 0) check(Math.Abs(metadata.GetProperty("PreviousSaveCutoff").GetDouble() - requests[i - 1].AcceptedQpc) < 1e-8 && metadata.GetProperty("ActualStart").GetDouble() >= requests[i - 1].AcceptedQpc, "Real exact FIFO cutoff " + i);
                    await Decode(clip, check);
                }
                check(recorder.Running && recorder.Recording.State == RecordingState.Recording, "Capture and conventional recording uninterrupted by rapid saves");
                var recording = await recorder.StopRecording(); check(recording != null, "Concurrent conventional recording published");
                if (recording != null) await Decode(recording, check);
                check(recorder.CaptureDroppedFrames == 0 && recorder.CaptureDuplicatedFrames == 0, "Capture diagnostics no dropped/duplicated frames");
                check(recorder.OutstandingSaves == 0 && recorder.SaveReservations == (0, 0), "Live queue drains without retained-resource leak");
                measurements.Add(new { MaxUiGapMs = beats.Zip(beats.Skip(1), (a, b) => b - a).Max(), CaptureDrops = recorder.CaptureDroppedFrames, CaptureDuplicates = recorder.CaptureDuplicatedFrames, PrivateMiB = Process.GetCurrentProcess().PrivateMemorySize64 / 1048576.0 });
                File.WriteAllText(Path.Combine(root, "stages.json"), JsonSerializer.Serialize(stages, Json));
                File.WriteAllText(Path.Combine(root, "commands.json"), JsonSerializer.Serialize(commands, Json));
            }
            finally { foreach (var timing in timings.AsEnumerable().Reverse()) timing?.Dispose(); }
        }
        finally { SaveTiming.Observed -= Observe; timer.Stop(); window.Close(); }
    }
}
