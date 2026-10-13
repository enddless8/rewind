using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Replay;
using Application = System.Windows.Application;
using Brushes = System.Windows.Media.Brushes;

// Real desktop capture and production notifications, isolated from MainWindow/settings.
internal static class SaveLatencyChecks
{
    internal static async Task<int> Run(string root, bool single = false)
    {
        if (Directory.EnumerateFileSystemEntries(root).Any()) throw new IOException("Use a new empty fixture directory.");
        // Live timing can coexist with the user's own rolling capture. Its buffer
        // is mutable, so hash persisted settings/queue here rather than race files
        // the other process is legitimately writing/deleting.
        string[] RuntimeConfiguration() => new[] { "settings.json", "compression-queue.json" }
            .Select(name => Path.Combine(Settings.DataDirectory, name))
            .Where(File.Exists).Select(path => path + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
        var before = RuntimeConfiguration();
        var oldTiming = Environment.GetEnvironmentVariable("REWIND_SAVE_TIMINGS_FILE");
        Environment.SetEnvironmentVariable("REWIND_SAVE_TIMINGS_FILE", Path.Combine(root, "timing.jsonl"));
        var checks = new List<string>();
        var stages = new List<object>();
        var requests = new List<object>();
        var shown = new List<object>();
        var observedStages = new Dictionary<Guid, List<string>>();
        var clips = new List<object>();
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks.Add(name); }
        void Observe(Guid id, string stage, double elapsed, object? details)
        { lock (stages) stages.Add(new { id, stage, elapsed, details }); }
        object[] SnapshotStages() { lock (stages) return stages.ToArray(); }
        SaveTiming.Observed += Observe;
        var text = new TextBlock { Foreground = Brushes.White, FontSize = 30 };
        var window = new Window { Title = "Rewind SaveClip preparation timing", Content = text, Width = 650, Height = 320,
            Background = Brushes.DarkSlateBlue, ShowActivated = false };
        var heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        heartbeat.Tick += (_, _) => text.Text = $"SaveClip timing fixture\n{Stopwatch.GetTimestamp()}";
        window.Show(); heartbeat.Start();
        var settings = new Settings { CaptureMode = "Desktop", HideDisplayBorder = true, Codec = "AV1", Height = 1080,
            Fps = 60, BufferSeconds = 30, MicrophoneId = "off", Discord = false, AudioMode = "System", GroupByApplication = false,
            OutputDirectory = Path.Combine(root, "output"), BufferDirectory = Path.Combine(root, "buffer") };
        try
        {
            await using var recorder = new Recorder(settings, null, Native.Displays().First(d => d.Primary));
            if (single)
            {
                async Task<(int Code, string Output, string Error)> RunMedia(string tool, IEnumerable<string> input, int timeout)
                {
                    var args = input.ToArray();
                    if (tool == "ffmpeg" && args.Contains("-c:a") && args[^1].EndsWith("clip.mkv", StringComparison.OrdinalIgnoreCase))
                    {
                        var staging = Path.GetDirectoryName(args[^1])!;
                        var retained = Path.Combine(root, "snapshot");
                        if (!Directory.Exists(retained))
                        {
                            SaveTiming.Current?.Mark("profile-retention-started");
                            Directory.CreateDirectory(retained);
                            foreach (var file in Directory.GetFiles(staging))
                                if (Path.GetExtension(file) == ".mkv") MediaOwnership.Retain(file, Path.Combine(retained, Path.GetFileName(file)));
                                else File.Copy(file, Path.Combine(retained, Path.GetFileName(file)));
                            var manifest = Path.Combine(retained, "video.txt");
                            File.WriteAllText(manifest, File.ReadAllText(manifest).Replace(staging.Replace('\\', '/'), retained.Replace('\\', '/')));
                            File.WriteAllText(Path.Combine(root, "export-arguments.json"), JsonSerializer.Serialize(args.Select(a => a.Replace(staging, retained)).ToArray()));
                            SaveTiming.Current?.Mark("profile-retention-completed");
                        }
                    }
                    return await MediaTools.Run(tool, args, timeout, bindLifetime: true);
                }
                recorder.PacketCopy = new(RunMedia); recorder.VideoExport = new(run: RunMedia);
            }
            await using var overlays = new OverlayNotificationService(Application.Current.Dispatcher, () => recorder.OverlayTarget);
            overlays.Shown += notification =>
            {
                shown.Add(new { notification.SaveOperationId, notification.Body, notification.Kind, Tick = Stopwatch.GetTimestamp() });
                if (notification.SaveOperationId is { } id && notification.Kind == OverlayNotificationKind.RewindSaving)
                {
                    if (!observedStages.TryGetValue(id, out var states)) observedStages[id] = states = [];
                    states.Add(notification.Body);
                }
            };
            await recorder.Start();
            await Task.Delay(30500);
            // Issue the very first request near the middle of a real two-second GOP.
            var origin = (double)typeof(Recorder).GetField("origin", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(recorder)!;
            var target = origin + (Math.Floor((Native.Clock - origin) / 2) + 1) * 2 + 1;
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, target - Native.Clock)));
            Check(recorder.OutstandingSaves == 0, "First request has an idle worker and no predecessor");

            async Task<(Guid Id, AcceptedSave Accepted, Task Observe)> Submit(int number)
            {
                var received = Stopwatch.GetTimestamp();
                var id = Guid.NewGuid();
                using var timing = SaveTiming.Begin(id, received);
                timing?.MarkAt("save-command-received", received, new { Source = "live-harness", Number = number });
                var accepted = await recorder.AcceptSave();
                requests.Add(new { Number = number, Id = id, accepted.AcceptedQpc });
                overlays.BeginRewindSave(id, stage: accepted.Stage);
                var observer = Complete();
                return (id, accepted, observer);
                async Task Complete()
                {
                    if (await accepted.PreparationStarted && accepted.Stage == SaveClipStage.Preparing) overlays.PrepareRewindSave(id);
                    if (await accepted.ExportStarted) overlays.ExportRewindSave(id);
                    var clip = await accepted.Completion;
                    var metadata = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.ChangeExtension(clip.Path, ".json")));
                    clips.Add(new { Number = number, Id = id, clip, metadata });
                    Check(Math.Abs(metadata.GetProperty("AcceptedSaveBoundary").GetDouble() - accepted.AcceptedQpc) < 1e-8,
                        "Request " + number + " preserves its original capture boundary");
                    overlays.CompleteRewindSave(id, clip);
                }
            }

            var first = await Submit(1);
            if (single) await first.Observe;
            else
            {
            await first.Accepted.ExportStarted;
            await Task.Delay(150);
            Check(!first.Accepted.Completion.IsCompleted, "Second request arrives during the first real export");
            var second = await Submit(2);
            await Task.Delay(150);
            Check(!first.Accepted.Completion.IsCompleted, "Third request arrives during the first real export");
            var third = await Submit(3);
            await Task.WhenAll(first.Observe, second.Observe, third.Observe);
            foreach (var request in new[] { second, third })
                Check(observedStages[request.Id].First() == "Клип в очереди" && observedStages[request.Id].Last() == "Сохранение клипа",
                    "Real predecessor wait displays queued, then export, without stale status regression");
            }
            await Task.Delay(350); // Allow actual WPF render/compositor observations to complete.
            Check(observedStages[first.Id].First() == "Подготовка клипа" && !observedStages[first.Id].Contains("Клип в очереди"),
                "Idle first job displays preparation and never a false queued status");
            Check(observedStages[first.Id].Contains("Сохранение клипа"), "First job displays export after snapshot ownership");
            Check(recorder.OutstandingSaves == 0 && recorder.SaveReservations == (0, 0), "Queue drains and releases resource reservations");
            Check(before.SequenceEqual(RuntimeConfiguration()), "Private persisted settings and compression queue unchanged; external capture buffer excluded");
            File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = true, checks, requests, clips, shown, stages = SnapshotStages() }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = false, error = ex.ToString(), checks, requests, shown, stages = SnapshotStages() }, new JsonSerializerOptions { WriteIndented = true }));
            throw;
        }
        finally
        {
            SaveTiming.Observed -= Observe; heartbeat.Stop(); window.Close();
            Environment.SetEnvironmentVariable("REWIND_SAVE_TIMINGS_FILE", oldTiming);
        }
    }
}
