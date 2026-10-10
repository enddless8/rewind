using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Replay;
internal static class SelfTest
{
    public static async Task RenderUi(string path)
    {
        var window = new MainWindow(); window.Show(); await Task.Delay(300); window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(path)) encoder.Save(file);
        window.Hide();
    }
    public static async Task<int> RunAsync(string[] args)
    {
        var directory = args.SkipWhile(x => x != "--self-test").Skip(1).FirstOrDefault() ?? Path.Combine(Settings.DataDirectory, "tests");
        Directory.CreateDirectory(directory);
        var results = new List<object>();
        Window? target = null;
        try
        {
            void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); results.Add(new { Test = name, Passed = true }); }
            await RegressionTests.Run(Path.Combine(directory, "regression-" + Guid.NewGuid().ToString("N")[..8]), Check);
            var silenceFile = Path.Combine(directory, "silence.wav");
            var silence = AudioTrack.WriteWave(silenceFile, [], 0, 184, -55);
            Check(Math.Abs(silence.LongestSilenceSeconds - 184) < .03, "184 seconds of microphone silence");
            var pcm = new byte[48000 * 4];
            for (var frame = 0; frame < 48000; frame++) { var value = (short)(Math.Sin(frame * 2 * Math.PI * 440 / 48000) * 6000); BitConverter.TryWriteBytes(pcm.AsSpan(frame * 4, 2), value); BitConverter.TryWriteBytes(pcm.AsSpan(frame * 4 + 2, 2), value); }
            var tone = AudioTrack.WriteWave(Path.Combine(directory, "signal.wav"), [new(1, pcm)], 0, 3, -55);
            Check(Math.Abs(tone.SilentSeconds - 2) < .04 && tone.PeakDb > -16 && tone.LongestSilenceSeconds < 1.04, "PCM placement, RMS, peak and separate silent intervals");
            var cropped = AudioTrack.WriteWave(Path.Combine(directory, "cropped.wav"), [new(1, pcm)], 1.5, .5, -55);
            Check(cropped.SilentSeconds < .02, "Audio cropping uses clip interval");
            Check(MainWindow.ParseHotkey("Ctrl+Alt+F10") == (3u, 0x79u), "Global hotkey parsing");
            using (var processAudio = new AudioTrack("Process-test", false, null, Environment.ProcessId, 15, -55))
            {
                await processAudio.Start(); await Task.Delay(250);
                Check(processAudio.Health == null, "WASAPI process-loopback initialization");
                using var wave = new NAudio.Wave.RawSourceWaveStream(new MemoryStream(pcm), new NAudio.Wave.WaveFormat(48000, 16, 2));
                using var playback = new NAudio.Wave.WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, false, 50);
                playback.Init(wave); var began = Native.Clock; playback.Play(); await Task.Delay(1200); playback.Stop();
                var captured = AudioTrack.WriteWave(Path.Combine(directory, "process-tone.wav"), processAudio.Snapshot(began, began + 1.2), began, 1.2, -55);
                Check(captured.RmsDb > -50 && captured.SilentSeconds < .6, "Known tone is captured from the target process");
            }
            using (var child = MediaTools.Create("ffmpeg", ["-v", "error", "-re", "-f", "lavfi", "-i", "color=s=64x64:r=1", "-f", "null", "-"]))
            {
                child.Start(); using (var guard = new ProcessLifetime(child)) await Task.Delay(100);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                Check(child.HasExited, "Encoder lifetime is bound to the application job");
            }
            var text = new TextBlock { Foreground = System.Windows.Media.Brushes.White, FontSize = 28, Margin = new Thickness(32), Text = "Проверка захвата Rewind" };
            target = new Window { Title = "Rewind — проверка захвата", Width = 720, Height = 450, Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(18, 31, 48)), Content = text };
            target.Show();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (_, _) => text.Text = $"Rewind / NVENC\n\n{DateTime.Now:HH:mm:ss.fff}\n\nЗахват окна · отдельный звук"; timer.Start();
            var game = new GameWindow(new WindowInteropHelper(target).Handle, Environment.ProcessId, "Replay-Test", target.Title);
            var display = Native.Displays().First(x => x.Primary);
            var configuration = new Settings { Height = 720, Fps = 30, BufferSeconds = 15, MaxBitrateMbps = 5, CaptureMode = "Game", AudioMode = "Game", MicrophoneId = "default", Discord = false, OutputDirectory = Path.Combine(directory, "clips") };
            await using (var recording = new Recorder(configuration, game, display))
            {
                await recording.Start();
                for (var i = 0; i < 19; i++) { await Task.Delay(1000); await recording.Maintain(); }
                Check(recording.Running, "Game capture stays alive with process-loopback audio");
                var clip = await recording.Save();
                Check(File.Exists(clip.Path) && new FileInfo(clip.Path).Length > 1000, "Clip saved while recording continues");
                Check(recording.Running, "Saving does not stop buffer");
                Check(clip.Duration >= 14 && clip.Duration <= 17.1, "Ring buffer returns requested duration plus keyframe margin");
                var probe = await MediaTools.Run("ffprobe", ["-v", "error", "-show_streams", "-show_format", "-of", "json", clip.Path]);
                File.WriteAllText(Path.Combine(directory, "probe-game.json"), probe.Output);
                using var json = JsonDocument.Parse(probe.Output);
                Check(json.RootElement.GetProperty("streams").EnumerateArray().Any(s => s.TryGetProperty("tags", out var tags) && tags.TryGetProperty("title", out var title) && title.GetString() == "Game"), "Process loopback produces a separate Game audio track");
                var decoded = await MediaTools.Run("ffmpeg", ["-v", "error", "-i", clip.Path, "-map", "0", "-f", "null", "-"], 60);
                Check(decoded.Code == 0 && string.IsNullOrWhiteSpace(decoded.Error), "All saved streams decode cleanly");
                results.Add(new { Clip = clip.Path, clip.Warnings, recording.Encoder });
                var compressorFixture = Path.Combine(directory, "compressor-settings.json");
                File.WriteAllText(compressorFixture, """{"codec":"h264","compress":{"codec":"h265","preset":"Сильнее сжать","resolution":"1080p","fps":"30","audio":"Копировать","delete_source":true}}""");
                var imported = CompressionProfile.Import(compressorFixture);
                Check(imported.Codec == "h265" && imported.Cq == 31 && imported.Height == 1080 && imported.Fps == 30, "Imports MP4 Compressor's active compression profile");
                var queueFile = Path.Combine(directory, "compression-" + Guid.NewGuid().ToString("N") + ".json");
                await using (var queue = new CompressionQueue(queueFile))
                {
                    var compressionId = queue.Enqueue(clip.Path, imported);
                    // Enqueue a second task to verify serialization and no overwrite.
                    var secondId = queue.Enqueue(clip.Path, imported);
                    var simultaneous = await recording.Save();
                    Check(recording.Running && File.Exists(simultaneous.Path), "Recording and another clip save continue during queued compression");
                    var until = Native.Clock + 90;
                    while (queue.Snapshot().Any(j => j.State is "Running" or "Pending") && Native.Clock < until) await Task.Delay(200);
                    var completed = queue.Snapshot();
                    Check(completed.Count == 2 && completed.All(j => j.State == "Completed"), "Two compression jobs finish sequentially: " + string.Join(";", completed.Select(j => j.Error)));
                    Check(completed[0].Output != completed[1].Output && File.Exists(clip.Path), "Compressed copies never overwrite each other or delete originals");
                    var compressedProbe = await MediaTools.Run("ffprobe", ["-v", "error", "-show_streams", "-of", "json", completed[0].Output]);
                    using var compressedJson = JsonDocument.Parse(compressedProbe.Output);
                    Check(compressedJson.RootElement.GetProperty("streams").GetArrayLength() == json.RootElement.GetProperty("streams").GetArrayLength(), "Compressed MP4 preserves every audio track");
                    var compressedDecode = await MediaTools.Run("ffmpeg", ["-v", "error", "-i", completed[0].Output, "-map", "0", "-f", "null", "-"], 60);
                    Check(compressedDecode.Code == 0 && string.IsNullOrWhiteSpace(compressedDecode.Error), "Compressed MP4 decodes cleanly");
                    results.Add(new { Compressed = completed[0].Output, completed[0].OriginalBytes, completed[0].CompressedBytes });
                    var failedId = queue.Enqueue(Path.Combine(directory, "missing-input.mkv"), imported);
                    queue.Enqueue(clip.Path, imported);
                    until = Native.Clock + 60;
                    while (queue.Snapshot().Any(j => j.State is "Running" or "Pending") && Native.Clock < until) await Task.Delay(200);
                    Check(queue.Snapshot().Single(j => j.Id == failedId).State == "Failed" && queue.Snapshot().Last().State == "Completed" && recording.Running, "A failed compression does not block the next job or recording");
                }
                await using (var resumed = new CompressionQueue(queueFile))
                    Check(resumed.Snapshot().Count(j => j.State == "Completed") == 3, "Completed queue state survives restarting");
                var interruptedFile = Path.Combine(directory, "interrupted-queue.json");
                var interrupted = new CompressionJob { Source = clip.Path, Output = Path.Combine(directory, "resumed-" + Guid.NewGuid().ToString("N")[..6] + ".mp4"), Profile = imported, State = "Running" };
                File.WriteAllText(interruptedFile, JsonSerializer.Serialize(new[] { interrupted }));
                await using (var resumed = new CompressionQueue(interruptedFile))
                {
                    var until = Native.Clock + 60;
                    while (resumed.Snapshot()[0].State is "Running" or "Pending" && Native.Clock < until) await Task.Delay(200);
                    Check(resumed.Snapshot()[0].State == "Completed" && File.Exists(clip.Path), "Interrupted compression resumes from its durable queue without deleting the source");
                }
                await Task.Delay(2500);
                var second = await recording.Save();
                Check(File.Exists(second.Path), "Second hotkey-style save works without restart");
            }
            configuration.CaptureMode = "Desktop"; configuration.AudioMode = "System"; configuration.MicrophoneId = "off";
            await using (var recording = new Recorder(configuration, game, display))
            {
                await recording.Start(); await Task.Delay(5000); var clip = await recording.Save();
                Check(recording.CaptureBackend.StartsWith("DXGI"), "Borderless desktop mode uses DXGI, not WGC");
                Check(File.Exists(clip.Path), "Monitor capture with system loopback"); results.Add(new { DesktopClip = clip.Path, clip.Warnings });
            }
            timer.Stop(); target.Close(); target = null;
            // Render our own WPF view to an artifact without desktop automation.
            await RenderUi(Path.Combine(directory, "ui.png"));
            File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(new { Passed = true, Results = results }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            target?.Close();
            File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(new { Passed = false, Error = ex.ToString(), Results = results }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }
}

