using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Replay;

internal static class ConcurrencyChecks
{
    internal static async Task<int> Run(string root)
    {
        List<string> checks = []; List<object> measurements = [];
        void Check(bool value, string name) { if (!value) throw new Exception(name); checks.Add(name); }
        var originals = new[] { "settings.json", "compression-queue.json", "errors.log" }.ToDictionary(n => n,
            n => File.Exists(Path.Combine(Settings.DataDirectory, n)) ? File.ReadAllBytes(Path.Combine(Settings.DataDirectory, n)) : null);
        Check(ClipFiles.DiagnosticDirectoryForTests == Path.Combine(root, "diagnostics"), "Harness installs test-owned logging before recorder work");
        var marker = "lifecycle-log-" + Guid.NewGuid().ToString("N");
        ClipFiles.Log("lifecycle-isolation", new IOException(marker), root);
        Check(File.ReadAllText(Path.Combine(root, "diagnostics", "errors.log")).Contains(marker), "Fault diagnostics reach the isolated log");
        var settings = new Settings { CaptureMode = "Desktop", HideDisplayBorder = true, MicrophoneId = "off", Discord = false,
            Codec = "AV1", Height = 720, Fps = 30, BufferSeconds = 15, AudioMode = "System", GroupByApplication = false,
            OutputDirectory = Path.Combine(root, "output"), BufferDirectory = Path.Combine(root, "buffer") };
        var window = new Window { Title = "Проверка параллельного сохранения", Width = 500, Height = 280, ShowActivated = false };
        window.Show();
        var beats = new List<double>(); var watch = Stopwatch.StartNew();
        var heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        heartbeat.Tick += (_, _) => beats.Add(watch.Elapsed.TotalMilliseconds); heartbeat.Start();
        Recorder? recorder = null; TaskCompletionSource? release = null;
        try
        {
            recorder = new(settings, null, Native.Displays().First(d => d.Primary));
            await recorder.Start(); await Task.Delay(3200); await recorder.StartRecording(); await Task.Delay(4300);
            var session = (RecordingSession)typeof(Recorder).GetField("recordingSession", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(recorder)!;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            release = new(TaskCreationOptions.RunContinuationsAsynchronously); bool gated = false;
            session.Exporter = new(async (tool, input, timeout) =>
            {
                var args = input.ToArray();
                Check(!args.Contains("-copyts") && !args[^1].EndsWith(".mka"), "Default recorder uses Stage 1 scheduling");
                if (!gated && args.Contains("-c:a")) { gated = true; entered.SetResult(); await release.Task; }
                return await MediaTools.Run(tool, args, timeout, bindLifetime: true);
            });
            var stopping = recorder.StopRecording(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Check(recorder.Recording.State == RecordingState.Finalizing && recorder.Running, "Finalization retains active producer without metadata lock");
            var toggles = Stopwatch.StartNew(); await recorder.SetRewindEnabled(false); await recorder.SetRewindEnabled(true);
            measurements.Add(new { RewindToggleMs = toggles.Elapsed.TotalMilliseconds });
            Check(toggles.Elapsed < TimeSpan.FromSeconds(2), "Rewind OFF/ON does not wait for recording export");
            await Task.Delay(2400); var saving = Stopwatch.StartNew(); var clip = await recorder.Save();
            measurements.Add(new { ConcurrentSaveMs = saving.Elapsed.TotalMilliseconds, clip.Path });
            Check(File.Exists(clip.Path) && !stopping.IsCompleted, "SaveClip publishes independently while recording finalization is gated");
            await using (var overlays = new OverlayNotificationService(System.Windows.Application.Current.Dispatcher, () => recorder.OverlayTarget))
            {
                overlays.ShowRewindEnabled(null); await Task.Delay(300);
                Check(overlays.Windows.Count > 0, "Production overlay remains responsive during finalization");
            }
            var starting = recorder.StartRecording(); await Task.Delay(250);
            Check(!starting.IsCompleted, "Next conventional recording deliberately waits on recordingCommands");
            release.SetResult(); var completed = await stopping; await starting;
            Check(completed != null && recorder.Recording.State == RecordingState.Recording, "Next recording starts after previous publication, without ownership overlap");
            await Task.Delay(2400); Check(await recorder.StopRecording() != null, "Next recording also publishes correctly");
            var gaps = beats.Zip(beats.Skip(1), (a, b) => b - a).ToArray();
            measurements.Add(new { DispatcherTicks = beats.Count, MaxDispatcherGapMs = gaps.Max() });
            Check(beats.Count > 100, "WPF Dispatcher continues processing during gated export and real media work");
            await recorder.DisposeAsync(); recorder = null;

            // These two disposal calls reproduce the overlapping callers in
            // MainWindow StopRecording and Rewind ON when its producer stopped.
            await using var second = new Recorder(settings, null, Native.Displays().First(d => d.Primary));
            await second.SetRewindEnabled(false); await second.Start(); await second.StartRecording(); await Task.Delay(3200);
            var secondSession = (RecordingSession)typeof(Recorder).GetField("recordingSession", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(second)!;
            var disposalEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            secondSession.Exporter = new(async (tool, input, timeout) =>
            {
                var args = input.ToArray();
                if (args.Contains("-c:a")) { disposalEntered.TrySetResult(); await release.Task; }
                return await MediaTools.Run(tool, args, timeout, bindLifetime: true);
            });
            var oldStop = second.StopRecording(); await disposalEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var disposals = new Task[8];
            Parallel.For(0, disposals.Length, i => disposals[i] = second.DisposeAsync().AsTask());
            Check(disposals.All(t => ReferenceEquals(t, disposals[0])), "Concurrent threads share one cleanup task");
            Check(disposals.All(t => !t.IsCompleted) && !oldStop.IsCompleted, "Disposal joins an already running Stop before cleanup");
            release.SetResult(); await Task.WhenAll(disposals); await oldStop; await second.DisposeAsync();
            measurements.Add(new { ConcurrentDispose = "completed" });
            Check(true, "Concurrent and repeated disposal complete without disposed semaphore access");
            foreach (var pair in originals)
            {
                var path = Path.Combine(Settings.DataDirectory, pair.Key);
                Check(pair.Value == null ? !File.Exists(path) : pair.Value.SequenceEqual(File.ReadAllBytes(path)), "Private runtime file unchanged: " + pair.Key);
            }
            File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = true, checks, measurements }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        finally { release?.TrySetResult(); if (recorder != null) await recorder.DisposeAsync(); heartbeat.Stop(); window.Close(); }
    }
}
