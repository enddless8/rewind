using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Replay;

internal static class LiveSave
{
    internal static async Task Run(string root, string category, int seconds, string exercise)
    {
        var settings = new Settings { CaptureMode = "Desktop", HideDisplayBorder = true, AudioMode = "System", MicrophoneId = "off", Discord = false,
            Codec = "AV1", Height = 1080, Fps = 60, BufferSeconds = seconds, Quality = 27, MaxBitrateMbps = 15, GroupByApplication = false,
            OutputDirectory = Path.Combine(root, "output"), BufferDirectory = Path.Combine(root, "buffer") };
        var label = new TextBlock { FontSize = 42, Foreground = System.Windows.Media.Brushes.White };
        var window = new Window { Title = "Rewind · Hybrid Save validation", Width = 800, Height = 450, Content = label,
            Background = System.Windows.Media.Brushes.DarkSlateBlue, Topmost = true, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var animation = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) }; var frame = 0;
        animation.Tick += (_, _) => label.Text = $"{++frame:D7}   {DateTime.Now:HH:mm:ss.fff}";
        var stages = new List<object>(); string? staging = null; bool retained = false, failed = false;
        var commands = new List<object>(); var retainedRoot = Path.Combine(root, "retained");
        void Observe(Guid id, string stage, double elapsed, object? details) { lock (stages) stages.Add(new { id, stage, elapsed, details }); }
        SaveTiming.Observed += Observe;
        Environment.SetEnvironmentVariable("REWIND_SAVE_TIMINGS_FILE", Path.Combine(root, "timing.jsonl"));
        try
        {
            window.Show(); animation.Start();
            // Ensure the changing fixture has actually rendered before starting
            // a fresh DXGI producer; do not change production startup policy.
            var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler? firstRender = null;
            firstRender = (_, _) => { System.Windows.Media.CompositionTarget.Rendering -= firstRender; rendered.TrySetResult(); };
            System.Windows.Media.CompositionTarget.Rendering += firstRender;
            await rendered.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Task.Delay(300);
            await using var recorder = new Recorder(settings, null, Native.Displays().First(d => d.Primary));
            var epoch = (double)typeof(Recorder).GetField("rewindSince", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(recorder)!;
            async Task<(int Code, string Output, string Error)> Run(string tool, IEnumerable<string> input, int timeout)
            {
                var args = input.ToList(); commands.Add(new { tool, arguments = args });
                // Find only this accepted export's .saving directory. Never copy
                // the live ring/session journal or mutate the accepted clocks.
                if (!retained && tool == "ffmpeg")
                {
                    staging = args.Select(Path.GetDirectoryName).FirstOrDefault(p => p != null && Path.GetFileName(p).StartsWith(".saving-"));
                    staging ??= args.Select(Path.GetDirectoryName).Select(p => p == null ? null : Path.GetDirectoryName(p)).FirstOrDefault(p => p != null && Path.GetFileName(p).StartsWith(".saving-"));
                    if (staging != null)
                    { RewindPacketCopyTests.Retain(staging, retainedRoot); retained = true; }
                }
                if (category == "D" && !failed && args[^1].EndsWith("head.mkv"))
                { failed = true; return (1, "", "Injected boundary encoder failure, production same-snapshot fallback"); }
                return await MediaTools.Run(tool, args, timeout, bindLifetime: true);
            }
            recorder.PacketCopy = new(Run); recorder.VideoExport = new(run: Run);
            recorder.HybridExport = new(category is "C" or "D", Run);
            await recorder.Start();
            await using var resources = new Resources(root, () => staging);
            resources.AddCapture(((Process)typeof(Recorder).GetField("video", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(recorder)!).Id);
            if (exercise is "controls" or "history") await recorder.StartRecording();
            // A accepts just before the initial history expires, so its start
            // remains the first closed KEY. B/C/D use the full rolling window.
            var origin = (double)typeof(Recorder).GetField("origin", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(recorder)!;
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, origin + (category == "A" ? seconds - .05 : seconds + 2.5) - Native.Clock)));
            var operation = Guid.NewGuid();
            using var timing = SaveTiming.Begin(operation); timing?.Mark("save-accepted");
            await using var overlays = new OverlayNotificationService(System.Windows.Application.Current.Dispatcher, () => recorder.OverlayTarget);
            overlays.BeginRewindSave(operation);
            var resourceStart = resources.ElapsedMs;
            var watch = Stopwatch.StartNew();
            var saving = recorder.Save();
            if (exercise == "controls")
            {
                // Toggle only after the snapshot is owned and export has begun.
                // Toggling during closure correctly invalidates Save acceptance.
                var exporting = false;
                for (var i = 0; i < 120 && !exporting && !saving.IsCompleted; i++)
                {
                    lock (stages) exporting = stages.Any(s => JsonSerializer.SerializeToElement(s).GetProperty("stage").GetString() is "hybrid-boundary-started" or "video-export-attempt-started");
                    if (!exporting) await Task.Delay(50);
                }
                if (!exporting) throw new Exception("No active owned export for controls test.");
                var shot = await ScreenshotCapture.Take(new("Desktop", true, settings.Fps, null, Native.Displays().First(d => d.Primary), settings.OutputDirectory, false, null));
                File.WriteAllText(Path.Combine(root, "screenshot.json"), JsonSerializer.Serialize(shot, Program.Json));
                await recorder.SetRewindEnabled(false); await recorder.SetRewindEnabled(true);
            }
            var clip = await saving; var elapsed = watch.Elapsed.TotalMilliseconds; var resourceEnd = resources.ElapsedMs;
            overlays.CompleteRewindSave(operation, clip); await Task.Delay(500);
            var metadata = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.ChangeExtension(clip.Path, ".json")));
            if (!retained || staging == null) throw new Exception("Missing accepted retained snapshot.");
            var events = stages.Select(s => JsonSerializer.SerializeToElement(s)).ToArray();
            var selection = events.First(s => s.GetProperty("stage").GetString() == "segment-selection-completed").GetProperty("details");
            double clock = selection.GetProperty("RawStart").GetDouble(); var actualStart = metadata.GetProperty("ActualStart").GetDouble(); var actualEnd = metadata.GetProperty("ActualEnd").GetDouble();
            var lines = File.ReadAllLines(Path.Combine(retainedRoot, "video.txt")); var ranges = new List<object>();
            for (var i = 0; i < lines.Length; i += 2)
            {
                var end = clock + double.Parse(lines[i + 1][9..], CultureInfo.InvariantCulture);
                ranges.Add(new { path = lines[i][6..^1], source_start = clock, source_end = end, start = Math.Max(actualStart, clock), end = Math.Min(actualEnd, end) }); clock = end;
            }
            // Recover the untouched full-export arguments from the production
            // call. Hybrid/copy arguments lack its video filter, so reconstruct
            // only that known legacy portion for supplementary paired exports.
            var audio = Directory.GetFiles(retainedRoot, "*.wav");
            var original = LegacyArguments(retainedRoot, selection.GetProperty("RawStart").GetDouble(), actualStart, clip.Duration, settings, audio);
            var accepted = metadata.GetProperty("AcceptedSaveBoundary").GetDouble();
            var previousCutoff = metadata.GetProperty("PreviousSaveCutoff").ValueKind == JsonValueKind.Null ? double.NegativeInfinity : metadata.GetProperty("PreviousSaveCutoff").GetDouble();
            var snapshot = new { accepted, history_start = Math.Max(accepted - settings.BufferSeconds, Math.Max(epoch, previousCutoff)), configured_buffer_seconds = settings.BufferSeconds,
                previous_cutoff = metadata.GetProperty("PreviousSaveCutoff").ValueKind == JsonValueKind.Null ? (double?)null : metadata.GetProperty("PreviousSaveCutoff").GetDouble(),
                epoch = double.IsFinite(epoch) ? (double?)epoch : null, fps = settings.Fps, intervals = ranges, audio };
            File.WriteAllText(Path.Combine(retainedRoot, "job.json"), JsonSerializer.Serialize(new { snapshot, arguments = original }, Program.Json));
            var alive = recorder.Running; var concurrentRecording = exercise is "controls" or "history" && recorder.Recording.State == RecordingState.Recording;
            if (!alive || exercise is "controls" or "history" && !concurrentRecording) throw new Exception("Capture/recording interrupted.");
            if (exercise is "controls" or "history")
            {
                var originalHash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(clip.Path));
                await using var compression = new CompressionQueue(Path.Combine(root, "compression-queue.json"));
                compression.Enqueue(clip.Path, new());
                for (var i = 0; i < 20 && compression.Snapshot().Single().State == "Pending"; i++) await Task.Delay(50);
                var secondBoundary = metadata.GetProperty("AcceptedSaveBoundary").GetDouble() + 15;
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, secondBoundary - Native.Clock)));
                var compressionAtSecondSave = compression.Snapshot().Single().State;
                var second = await recorder.Save();
                var secondMeta = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.ChangeExtension(second.Path, ".json")));
                if (secondMeta.GetProperty("ActualStart").GetDouble() < accepted ||
                    exercise == "history" && Math.Abs(secondMeta.GetProperty("AcceptedSaveBoundary").GetDouble() - secondBoundary) > .15)
                    throw new Exception("Previous cutoff / fifteen-second repeat acceptance changed.");
                File.WriteAllText(Path.Combine(root, "second-save.json"), JsonSerializer.Serialize(new { second, secondMeta, compressionAtSecondSave }, Program.Json));
                var conventional = await recorder.StopRecording();
                File.WriteAllText(Path.Combine(root, "conventional.json"), JsonSerializer.Serialize(conventional, Program.Json));
                for (var i = 0; i < 1800 && compression.Snapshot().Any(j => j.State is "Pending" or "Running"); i++) await Task.Delay(100);
                if (compression.Snapshot().Single().State != "Completed" || !System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(clip.Path)).SequenceEqual(originalHash)) throw new Exception("Compression/original retention failed.");
            }
            File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = true, category, seconds, SaveReturnMs = elapsed, clip, metadata,
                ResourceSaveStartedMs = resourceStart, ResourceSaveEndedMs = resourceEnd,
                CaptureRunning = alive, ConcurrentRecording = concurrentRecording, PrivateStateUnchanged = true, stages, commands,
                CaptureSyncDropped = recorder.CaptureDroppedFrames, CaptureSyncDuplicated = recorder.CaptureDuplicatedFrames }, Program.Json));
        }
        finally { animation.Stop(); window.Close(); SaveTiming.Observed -= Observe; Environment.SetEnvironmentVariable("REWIND_SAVE_TIMINGS_FILE", null); }
    }
    internal static List<string> LegacyArguments(string root, double raw, double start, double duration, Settings settings, string[] audio)
    {
        List<string> args = ["-hide_banner", "-v", "error", "-f", "concat", "-safe", "0", "-i", Path.Combine(root, "video.txt")];
        foreach (var wave in audio) args.AddRange(["-i", wave]);
        var filter = $"[0:v]trim=start={(start - raw).ToString("F6", CultureInfo.InvariantCulture)}:duration={duration.ToString("F6", CultureInfo.InvariantCulture)},setpts=PTS-STARTPTS[v]";
        if (audio.Length > 0) filter += ";" + string.Concat(Enumerable.Range(1, audio.Length).Select(i => $"[{i}:a]")) + $"amix=inputs={audio.Length}:normalize=0,alimiter=limit=0.95:latency=1[mix]";
        args.AddRange(["-filter_complex", filter, "-map", "[v]"]); if (audio.Length > 0) args.AddRange(["-map", "[mix]"]);
        foreach (var i in Enumerable.Range(1, audio.Length)) args.AddRange(["-map", $"{i}:a:0"]);
        args.AddRange(["-c:v", "av1_nvenc", "-preset", "p4", "-tune", "hq", "-rc", "vbr", "-cq", settings.Quality.ToString(), "-b:v", "0", "-maxrate", settings.MaxBitrateMbps + "M", "-bufsize", settings.MaxBitrateMbps * 2 + "M", "-bf", "0", "-r", settings.Fps.ToString(), "-c:a", "aac", "-b:a", "160k", "-t", duration.ToString("F6", CultureInfo.InvariantCulture)]);
        if (audio.Length > 0) args.AddRange(["-metadata:s:a:0", "title=Mix", "-disposition:a:0", "default"]);
        for (var i = 0; i < audio.Length; i++) args.AddRange([$"-metadata:s:a:{i + 1}", "title=" + Path.GetFileNameWithoutExtension(audio[i]), $"-disposition:a:{i + 1}", "0"]);
        args.AddRange(["-y", Path.Combine(root, "unpublished.mkv")]); return args;
    }
}
