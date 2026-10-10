using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Replay;

internal static class LiveRecording
{
    internal static async Task<int> Run(string root)
    {
        var privateFiles = new[] { "settings.json", "compression-queue.json" }.Select(n => Path.Combine(Settings.DataDirectory, n))
            .ToDictionary(p => p, p => File.Exists(p) ? File.ReadAllBytes(p) : null);
        var stages = new List<object>();
        SaveTiming.Observed += (_, stage, elapsed, details) => stages.Add(new { stage, elapsed, details });
        Environment.SetEnvironmentVariable("REWIND_SAVE_TIMINGS_FILE", Path.Combine(root, "timing.jsonl"));
        var settings = new Settings { CaptureMode = "Desktop", HideDisplayBorder = true, AudioMode = "System", MicrophoneId = "off", Discord = false,
            Codec = "AV1", Height = 720, Fps = 30, BufferSeconds = 15, Quality = 27, MaxBitrateMbps = 15, GroupByApplication = false,
            OutputDirectory = Path.Combine(root, "output"), BufferDirectory = Path.Combine(root, "buffer") };
        var label = new TextBlock { FontSize = 40, Foreground = System.Windows.Media.Brushes.White };
        var window = new Window { Title = "Проверка финализации записи", Width = 650, Height = 360, Content = label,
            Background = System.Windows.Media.Brushes.DarkSlateBlue, Topmost = true, ShowActivated = false };
        var animation = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        animation.Tick += (_, _) => label.Text = DateTime.Now.ToString("HH:mm:ss.fff");
        try
        {
            window.Show(); animation.Start();
            await using var recorder = new Recorder(settings, null, Native.Displays().First(d => d.Primary));
            await recorder.Start(); await Task.Delay(3100);
            // Deliberately exercise a copy-eligible command phase. This schedules
            // real Start/Stop calls; it never edits their accepted QPC boundaries.
            var origin = (double)typeof(Recorder).GetField("origin", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(recorder)!;
            var startTarget = origin + Math.Ceiling((Native.Clock - origin + .1) * settings.Fps) / settings.Fps - .015;
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, startTarget - Native.Clock)));
            await recorder.StartRecording();
            var session = (RecordingSession)typeof(Recorder).GetField("recordingSession", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(recorder)!;
            var retained = Path.Combine(root, "retained");
            bool copied = false;
            session.Exporter = new(async (tool, input, timeout) =>
            {
                var args = input.ToArray();
                if (!copied)
                {
                    Directory.CreateDirectory(retained);
                    foreach (var file in Directory.GetFiles(session.Directory))
                    {
                        var target = Path.Combine(retained, Path.GetFileName(file));
                        if (file.EndsWith(".mkv")) MediaOwnership.Retain(file, target);
                        else if (file.EndsWith(".pcm") || file.EndsWith(".json") || file.EndsWith(".jsonl")) File.Copy(file, target);
                    }
                    copied = true;
                }
                return await MediaTools.Run(tool, args, timeout, bindLifetime: true);
            });
            await Task.Delay(12000); var replay = await recorder.Save();
            if (recorder.Recording.State != RecordingState.Recording || !recorder.Running) throw new Exception("SaveClip прервал запись.");
            var stopTarget = origin + Math.Ceiling((session.StartedAt + 30 - origin) * settings.Fps) / settings.Fps + .025;
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, stopTarget - Native.Clock)));
            var stopping = recorder.StopRecording();
            var concurrentSave = recorder.Save();
            var result = await stopping ?? throw new Exception(recorder.Recording.Error);
            var replayDuringStop = await concurrentSave;
            if (!recorder.Running || Directory.Exists(session.Directory)) throw new Exception("Нарушено владение записью/producer.");
            await using var overlays = new OverlayNotificationService(System.Windows.Application.Current.Dispatcher, () => recorder.OverlayTarget);
            overlays.ShowRecordingSaved(result); await Task.Delay(700);
            if (!stages.Any(s => JsonSerializer.Serialize(s).Contains("success-first-wpf-render"))) throw new Exception("Не измерен реальный WPF render уведомления.");
            // Isolated compression queue; original media must survive unchanged.
            var original = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(result.Path));
            await using (var compression = new CompressionQueue(Path.Combine(root, "compression-queue.json")))
            {
                compression.Enqueue(result.Path, new());
                for (var i = 0; i < 1200 && compression.Snapshot().Any(j => j.State is "Pending" or "Running"); i++) await Task.Delay(100);
                if (compression.Snapshot().Single().State != "Completed") throw new Exception(compression.Snapshot().Single().Error ?? "Сжатие не завершено.");
            }
            if (!System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(result.Path)).SequenceEqual(original)) throw new Exception("Изменён оригинал MKV.");
            foreach (var pair in privateFiles)
                if (pair.Value == null ? File.Exists(pair.Key) : !File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)) throw new Exception("Изменены настройки/очередь пользователя.");
            var metadata = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.ChangeExtension(result.Path, ".json")));
            var replayMetadata = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.ChangeExtension(replay.Path, ".json")));
            var laterMetadata = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.ChangeExtension(replayDuringStop.Path, ".json")));
            if (laterMetadata.GetProperty("ActualStart").GetDouble() < replayMetadata.GetProperty("AcceptedSaveBoundary").GetDouble())
                throw new Exception("Concurrent Save вернул предыдущую историю.");
            File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = true, result, replay, replayDuringStop, metadata, stages,
                CaptureActive = recorder.Running, OwnershipReleased = true, PrivateStateUnchanged = true, OriginalPreserved = true,
                CaptureSyncDropped = recorder.CaptureDroppedFrames, CaptureSyncDuplicated = recorder.CaptureDuplicatedFrames }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        finally { animation.Stop(); window.Close(); }
    }
}
