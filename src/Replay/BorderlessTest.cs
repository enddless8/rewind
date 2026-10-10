using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Replay;
internal static class BorderlessTest
{
    public static async Task<int> Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        var diagnostics = new List<object>();
        void Save(bool passed, Exception? error = null) => File.WriteAllText(Path.Combine(directory, "results.json"),
            JsonSerializer.Serialize(new { Passed = passed, checks, diagnostics, Error = error?.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
        void Check(bool value, string name) { if (!value) throw new Exception(name); checks.Add(name); Save(false); }
        try
        {
            NativePcmTests.Run((value, name) => { if (!value) throw new Exception(name); });
            Check(true, "Native PCM conversion, resampling and packet timelines pass regression checks");
            using var devices = new MMDeviceEnumerator();
            using var output = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            string initialFormat;
            using (var client = output.AudioClient)
            {
                initialFormat = client.MixFormat.ToString();
                diagnostics.Add(new { Output = output.FriendlyName, output.ID, MixFormat = client.MixFormat.ToString() });
            }
            foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active))
                using (device) using (var client = device.AudioClient)
                    diagnostics.Add(new { Device = device.FriendlyName, device.ID, MixFormat = client.MixFormat.ToString() });
            Save(false);
            var tone = new byte[AudioTrack.Rate * AudioTrack.BytesPerFrame * 12];
            for (var i = 0; i < tone.Length / AudioTrack.BytesPerFrame; i++)
            {
                short sample = (short)(Math.Sin(2 * Math.PI * 437 * i / AudioTrack.Rate) * 1000);
                BitConverter.TryWriteBytes(tone.AsSpan(i * 4, 2), sample);
                BitConverter.TryWriteBytes(tone.AsSpan(i * 4 + 2, 2), sample);
            }
            using var input = new RawSourceWaveStream(new MemoryStream(tone), new WaveFormat(AudioTrack.Rate, 16, 2));
            using var player = new WasapiOut(output, AudioClientShareMode.Shared, false, 100);
            player.Init(input);
            using var audio = new AudioTrack("System-test", false, output.ID, null, 20, -55);
            await audio.Start();
            var start = Native.Clock;
            player.Play();
            await Task.Delay(1800);
            var before = AudioTrack.WriteWave(Path.Combine(directory, "before-video.wav"), audio.Snapshot(start + .3, start + 1.5), start + .3, 1.2, -55);
            diagnostics.Add(new { BeforeVideo = before, audio.Error });
            Check(audio.Error == null && before.RmsDb > -45 && before.LongestSilenceSeconds < .1, "System output captures continuous tone before video starts");
            var settings = Settings.Load();
            settings.CaptureMode = "Desktop"; settings.AudioMode = "System"; settings.HideDisplayBorder = true;
            settings.Discord = false; settings.BufferSeconds = 15;
            settings.BufferDirectory = Path.Combine(directory, "buffer"); settings.OutputDirectory = Path.Combine(directory, "clips");
            var display = Native.Displays().FirstOrDefault(d => d.Device == settings.MonitorDevice) ?? Native.Displays().First(d => d.Primary);
            diagnostics.Add(new { display.Device, display.Width, display.Height, TargetHeight = settings.Height, settings.Fps, settings.Codec }); Save(false);
            await using (var recorder = new Recorder(settings, null, display))
            {
                await recorder.Start();
                var began = Native.Clock;
                await Task.Delay(4500);
                var during = AudioTrack.WriteWave(Path.Combine(directory, "during-video.wav"), audio.Snapshot(began + .3, began + 3.8), began + .3, 3.5, -55);
                diagnostics.Add(new { DuringVideo = during, audio.DeviceName, audio.CaptureFormat, audio.Error, audio.MissingFrames, audio.Discontinuities, recorder.Warning, recorder.Encoder, recorder.CaptureBackend });
                Check(recorder.Running && recorder.CaptureBackend.StartsWith("DXGI"), "Borderless DXGI recording stays alive at configured resolution and FPS");
                Check(player.PlaybackState == PlaybackState.Playing && audio.Error == null && during.RmsDb > -45 && during.LongestSilenceSeconds < .1, "Scarlett output and loopback keep delivering tone during DXGI recording");
                var clip = await recorder.Save();
                diagnostics.Add(new { clip.Path, clip.Duration, clip.Warnings });
                Check(clip.Warnings.All(w => !w.StartsWith("System:") && !w.StartsWith("Microphone:")), "System audio and selected microphone initialize in the recorder");
                var decoded = await MediaTools.Run("ffmpeg", ["-v", "error", "-i", clip.Path, "-map", "0", "-f", "null", "-"], 30);
                Check(decoded.Code == 0 && string.IsNullOrWhiteSpace(decoded.Error), "Saved DXGI video and all audio tracks decode");
                var wav = Path.Combine(directory, "saved-system.wav");
                var exported = await MediaTools.Run("ffmpeg", ["-v", "error", "-i", clip.Path, "-map", "0:a:1", "-ar", "48000", "-ac", "2", "-c:a", "pcm_s16le", "-y", wav]);
                Check(exported.Code == 0 && AudioTrack.Analyze(wav, -55).RmsDb > -45, "Saved System audio contains the playback tone");
            }
            using (var afterClient = output.AudioClient)
                Check(afterClient.MixFormat.ToString() == initialFormat, "Output retains its configured mix format after capture stops");
            player.Stop(); Save(true); return 0;
        }
        catch (Exception ex) { Save(false, ex); return 1; }
    }
}
