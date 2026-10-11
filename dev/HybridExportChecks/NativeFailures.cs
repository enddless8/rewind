using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using Replay;

internal static class NativeFailures
{
    internal static async Task Run(string root)
    {
        List<object> checks = [];
        void Check(bool ok, string name)
        { checks.Add(new { ok, name }); if (!ok) throw new Exception(name); }
        var settings = new Settings { CaptureMode = "Desktop", HideDisplayBorder = true, AudioMode = "System", MicrophoneId = "off", Discord = false,
            Codec = "AV1", Height = 720, Fps = 30, BufferSeconds = 30, GroupByApplication = false,
            OutputDirectory = Path.Combine(root, "output"), BufferDirectory = Path.Combine(root, "buffer") };
        var window = new Window { Title = "Rewind · rollback validation", Width = 650, Height = 360,
            Content = new System.Windows.Controls.TextBlock { Text = "Production Save cancellation / rollback", FontSize = 26 }, Topmost = true, ShowActivated = false };
        try
        {
            window.Show();
            await using var recorder = new Recorder(settings, null, Native.Displays().First(d => d.Primary));
            var cutoff = typeof(Recorder).GetField("rewindSaveCutoff", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var epoch = typeof(Recorder).GetField("rewindSince", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var mode = "cancel"; bool hybrid = false; int fullAttempts = 0; double acceptedBeforeEpochChange = double.NaN;
            async Task<(int Code, string Output, string Error)> Run(string tool, IEnumerable<string> input, int timeout)
            {
                var args = input.ToArray();
                if (args[^1].EndsWith("head.mkv"))
                {
                    hybrid = true;
                    if (mode == "cancel") throw new OperationCanceledException();
                    if (mode == "epoch-cancel")
                    { acceptedBeforeEpochChange = (double)cutoff.GetValue(recorder)!; await recorder.SetRewindEnabled(false); await recorder.SetRewindEnabled(true); throw new OperationCanceledException(); }
                    if (mode == "no-space") return (1, "", "Injected insufficient storage");
                }
                if (tool == "ffmpeg" && args[^1].EndsWith("clip.mkv") && args.Contains("av1_nvenc"))
                { fullAttempts++; if (mode == "no-space") return (1, "", "Injected insufficient storage, both full export paths"); }
                return await MediaTools.Run(tool, args, timeout, bindLifetime: true);
            }
            recorder.HybridExport = new(true, Run); recorder.VideoExport = new(supportOverride: _ => Task.FromResult(true), run: Run);
            await recorder.Start(); await Task.Delay(32500);
            try { await recorder.Save(); throw new Exception("Cancellation absent."); } catch (OperationCanceledException) { }
            Check(hybrid && fullAttempts == 0, "Accepted Hybrid cancellation never starts full exporter");
            Check(double.IsNegativeInfinity((double)cutoff.GetValue(recorder)!), "Cancellation restores unconsumed cutoff");
            Check(!Directory.Exists(settings.OutputDirectory) || Directory.GetFiles(settings.OutputDirectory, "*.mkv").Length == 0, "Cancellation has no publication");
            mode = "no-space"; fullAttempts = 0;
            try { await recorder.Save(); throw new Exception("Storage failure absent."); } catch (IOException) { }
            Check(fullAttempts == 2 && double.IsNegativeInfinity((double)cutoff.GetValue(recorder)!), "Failed Hybrid plus NVDEC/software restores cutoff");
            Check(recorder.Running && recorder.HasReadableBufferData, "Failed Save retains ring/capture history");
            var oldEpoch = (double)epoch.GetValue(recorder)!; mode = "epoch-cancel"; fullAttempts = 0;
            try { await recorder.Save(); throw new Exception("Epoch cancellation absent."); } catch (OperationCanceledException) { }
            Check((double)epoch.GetValue(recorder)! > oldEpoch && (double)cutoff.GetValue(recorder)! == acceptedBeforeEpochChange &&
                (double)epoch.GetValue(recorder)! >= acceptedBeforeEpochChange, "Late failed old epoch cannot restore pre-epoch history");
            Check(fullAttempts == 0, "Epoch cancellation never retries");
            await Task.Delay(3500); mode = "ok";
            var clip = await recorder.Save();
            Check(File.Exists(clip.Path) && recorder.Running, "Subsequent Save publishes from new epoch");
            Check(!Directory.GetDirectories(settings.BufferDirectory, ".saving-*").Any(), "Accepted attempt staging cleaned");
            File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = true, count = checks.Count, checks, clip }, Program.Json));
        }
        finally { window.Close(); }
    }
}
