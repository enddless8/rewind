using System.Text.Json;

namespace Replay;
internal static class RecoveryTest
{
    public static async Task<int> Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Check(bool value, string name) { if (!value) throw new Exception(name); checks.Add(name); }
        try
        {
            Check(CaptureRecovery.IsAccessLost("AcquireNextFrame failed: 887a0026") && !CaptureRecovery.IsAccessLost("encoder failure"), "Only recognized DXGI loss is recoverable");
            var policy = new CaptureRecovery();
            policy.Attempt(0); Check(!policy.CanAttempt(4) && policy.CanAttempt(5), "Retry backoff");
            policy.Attempt(5); policy.Attempt(10); Check(policy.Exhausted && !policy.CanAttempt(100), "Three attempt limit");
            policy.Reset(); Check(policy.CanAttempt(100) && policy.Attempts == 0, "Healthy session resets retry budget");
            var pcm = new byte[5 * 192000];
            for (var i = 0; i < pcm.Length; i++) pcm[i] = (byte)(i / 192000 + 1);
            var joined = Recorder.JoinAudio([new(10, pcm)], [new("a", 10, 11), new("b", 14, 15)], 15, 0);
            Check(joined.Count == 2 && joined[0].Start == 0 && joined[1].Start == 1 && joined[0].Data.All(b => b == 1) && joined[1].Data.All(b => b == 5), "Audio removes exactly the same gap as video without shifting later sound");
            var offsetAudio = Recorder.JoinAudio([new(10, pcm)], [new("a", 10, 11)], 11, 1);
            Check(offsetAudio.Single().Data.All(b => b == 2), "Audio offset still applies on stitched timeline");
            var settings = new Settings { CaptureMode = "Desktop", AudioMode = "System", HideDisplayBorder = true, MicrophoneId = "off", Discord = false, Codec = "H264", Height = 720, Fps = 30, BufferSeconds = 15, OutputDirectory = directory };
            settings.BufferDirectory = Path.Combine(directory, "custom-buffer");
            Check(JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!.BufferDirectory == settings.BufferDirectory, "Custom buffer directory survives settings serialization");
            Check(JsonSerializer.Deserialize<Settings>("{}")!.BufferDirectory == Path.Combine(Settings.DataDirectory, "buffer"), "Older settings keep default buffer location");
            var blocked = Path.Combine(directory, "not-a-directory"); File.WriteAllText(blocked, "test");
            var blockedSettings = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;
            blockedSettings.BufferDirectory = blocked;
            await using (var rejected = new Recorder(blockedSettings, null, Native.Displays().First(x => x.Primary)))
            {
                bool denied = false;
                try { await rejected.Start(); } catch (IOException ex) { denied = ex.Message.Contains(blocked); }
                Check(denied && !rejected.Running, "Unavailable buffer fails clearly without fallback");
            }
            await using var before = new Recorder(settings, null, Native.Displays().First(x => x.Primary));
            Check(Path.GetDirectoryName(before.SessionDirectory) == settings.BufferDirectory, "Recording session uses selected buffer directory");
            await before.Start(); await Task.Delay(3500);
            await before.SimulateAccessLossForTest();
            Check(before.RecoverableCaptureLoss && before.Error?.Length < 150, "Stopped session reports recoverable loss and concise error");
            var old = await before.Save();
            Check(File.Exists(old.Path) && old.Duration > 1, "Completed segment survives capture failure");
            if (!string.Equals(Path.GetPathRoot(directory), Path.GetPathRoot(Settings.DataDirectory), StringComparison.OrdinalIgnoreCase))
            {
                var destination = Path.Combine(Settings.DataDirectory, "tests", "cross-volume-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(destination);
                var published = Path.Combine(destination, "clip.mkv");
                await ClipFiles.Publish(old.Path, published, "{}");
                Check(File.ReadAllBytes(old.Path).SequenceEqual(File.ReadAllBytes(published)), "Cross-volume publication preserves clip bytes");
                Check(!Directory.EnumerateFiles(destination, ".publishing-*").Any(), "Cross-volume publication leaves no partial file");
            }
            var after = before;
            var previousFiles = Directory.GetFiles(directory, "*.mkv", SearchOption.AllDirectories).Length;
            await after.RestartCapture(); await Task.Delay(3500);
            Check(Directory.GetFiles(Path.Combine(directory, "Desktop"), "*.mkv").Length == 1, "Recovery does not automatically publish extra clips");
            var clip = await after.Save();
            Check(after.Running && after.CaptureBackend == before.CaptureBackend && after.SessionDirectory == before.SessionDirectory, "Recovery keeps the same buffer");
            using var savedBoundary = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(old.Path, ".json")));
            using var recoveredBoundary = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(clip.Path, ".json")));
            Check(clip.Duration > 2 && recoveredBoundary.RootElement.GetProperty("ActualStart").GetDouble() >= savedBoundary.RootElement.GetProperty("AcceptedSaveBoundary").GetDouble(), "Recovery preserves the consumed-history cutoff and saves only new footage");
            await after.SimulateAccessLossForTest(); await after.RestartCapture(); await Task.Delay(3000);
            var twice = await after.Save();
            Check(twice.Duration > 2 && after.Running, "Repeated recovery still saves one playable clip");
            var probe = await MediaTools.Run("ffmpeg", ["-v", "error", "-i", twice.Path, "-map", "0", "-f", "null", "-"], 30);
            Check(probe.Code == 0 && string.IsNullOrWhiteSpace(probe.Error), "Recovered video and audio decode cleanly");
            await after.Maintain();
            Check(after.AvailableSeconds <= settings.BufferSeconds, "Combined buffer respects configured time limit");
            File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(new { Passed = true, checks }));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(new { Passed = false, checks, Error = ex.ToString() })); return 1;
        }
    }
}
