using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Replay;

internal static class MicrophoneMonitorTest
{
    public static async Task<int> Run(string directory)
    {
        Directory.CreateDirectory(directory);
        List<string> checks = [];
        List<object> hardware = [];
        void Check(bool value, string name) { if (!value) throw new Exception(name); checks.Add(name); }
        var config = Path.Combine(Settings.DataDirectory, "settings.json");
        var settingsBefore = File.Exists(config) ? File.ReadAllBytes(config) : null;
        try
        {
            var baseline = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
            foreach (var rate in new[] { 44100, 48000 })
            foreach (var gain in new[] { 0.0, .5, 1.0, 2.0, .4 * Math.Pow(10, 6.0 / 20) })
            {
                var format = WaveFormat.CreateIeeeFloatWaveFormat(rate, 1);
                var converter = new NativePcmConverter(format);
                var buffer = new MicrophoneMonitorBuffer();
                using var input = new AudioTrack("Synthetic preview", true, "default", null, 0, -55, null, buffer.Offer) { Gain = gain };
                for (var packetIndex = 0; packetIndex < 12; packetIndex++)
                {
                    var frames = rate / 100;
                    var native = new byte[frames * 4];
                    var floats = MemoryMarshal.Cast<byte, float>(native.AsSpan());
                    for (var i = 0; i < frames; i++) floats[i] = (float)(.05 * Math.Sin(2 * Math.PI * 997 * (packetIndex * frames + i) / rate));
                    var packet = converter.Convert(native, frames, packetIndex / 100.0);
                    var expected = (byte[])packet.Data.Clone(); AudioTrack.ApplyGain(expected, gain);
                    input.ProcessPacket(packet, Native.Clock);
                    var output = new byte[expected.Length];
                    Check(buffer.Read(output, 0, output.Length) == output.Length && expected.SequenceEqual(output), $"{rate} Hz native tone, gain {gain:F4}, packet {packetIndex}: exact processed samples reach render buffer");
                }
                Check(input.Snapshot(0, double.MaxValue).Count == 0 && !input.IsMuted, "Preview has neither history nor runtime mute dependency");
                var offered = buffer.OfferedBytes;
                buffer.Read(new byte[1920], 0, 1920);
                Check(buffer.OfferedBytes == offered, "Rendering never feeds microphone input or regenerates audio");
            }
            var bounded = new MicrophoneMonitorBuffer();
            bounded.Offer(new AudioPacket(0, Enumerable.Repeat((byte)1, 19200).ToArray()));
            bounded.Offer(new AudioPacket(0, Enumerable.Repeat((byte)2, 19200).ToArray()));
            var latest = new byte[19200]; bounded.Read(latest, 0, latest.Length);
            Check(latest.All(b => b == 2) && bounded.DroppedBytes == 19200, "Overflow keeps newest 100 ms rather than accumulating latency");
            var silence = Enumerable.Repeat((byte)9, 1920).ToArray();
            bounded.Read(silence, 0, silence.Length);
            Check(silence.All(b => b == 0) && bounded.UnderrunBytes >= 1920, "Underrun produces silence and keeps renderer running");
            bounded.Offer(new AudioPacket(0, latest)); await Task.Delay(160);
            bounded.Read(silence, 0, silence.Length);
            Check(silence.All(b => b == 0) && bounded.BufferedBytes == 0, "Stalled packets expire before they can be played late");
            bounded.Offer(new AudioPacket(0, latest)); bounded.Stop(); bounded.Read(silence, 0, silence.Length);
            var stoppedOffered = bounded.OfferedBytes; bounded.Offer(new AudioPacket(0, latest));
            Check(silence.All(b => b == 0) && bounded.OfferedBytes == stoppedOffered, "Stop immediately silences output and rejects further packets");
            var concurrent = new MicrophoneMonitorBuffer();
            var gate = typeof(MicrophoneMonitorBuffer).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(concurrent)!;
            using var acquired = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var holder = Task.Run(() => { lock (gate) { acquired.Set(); release.Wait(); } });
            acquired.Wait();
            var clock = Stopwatch.StartNew(); concurrent.Offer(new AudioPacket(0, latest)); concurrent.Read(silence, 0, silence.Length); clock.Stop();
            release.Set(); await holder;
            Check(clock.ElapsedMilliseconds < 30 && silence.All(b => b == 0), "Capture/render callbacks do not wait on a contended buffer lock");
            await Task.WhenAll(Task.Run(() => { for (var i = 0; i < 10000; i++) concurrent.Offer(new AudioPacket(0, latest)); }),
                Task.Run(() => { var rendered = new byte[1920]; for (var i = 0; i < 10000; i++) concurrent.Read(rendered, 0, rendered.Length); }));
            Check(concurrent.BufferedBytes <= 19200, "Concurrent capture/render stays bounded under overload");
            Check(Directory.GetFiles(directory, "*", SearchOption.AllDirectories).SequenceEqual(baseline), "Synthetic processing writes no media or other files");
            bool offRejected = false;
            try { _ = new MicrophoneMonitor("off", 1, -55); } catch (ArgumentException) { offRejected = true; }
            Check(offRejected, "Disabled microphone cannot start preview");

            using var devices = new MMDeviceEnumerator();
            using var outputs = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var microphones = devices.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToArray();
            var scarlett = microphones.FirstOrDefault(d => d.FriendlyName.Contains("Scarlett", StringComparison.OrdinalIgnoreCase) || d.FriendlyName.Contains("Focusrite", StringComparison.OrdinalIgnoreCase));
            var selected = scarlett?.ID ?? microphones.First().ID;
            var scarlettVerified = scarlett != null;
            foreach (var device in microphones) device.Dispose();
            foreach (var source in new[] { selected, "default", selected })
            {
                await using var monitor = new MicrophoneMonitor(source, .25, -55);
                await monitor.StartAsync(); await Task.Delay(450);
                Check(monitor.Input.Health == null && Native.Clock - monitor.Input.LastPacket < .2, "Real selected microphone provides fresh PCM: " + monitor.Input.DeviceName);
                Check(monitor.OutputDeviceId == outputs.ID && monitor.Buffer.RenderedBytes > 0, "Processed microphone PCM reaches Windows default playback: " + monitor.OutputDeviceName);
                if (source != "default") Check(monitor.InputDeviceId == selected, "Explicit microphone endpoint ID is preserved");
                Check(!monitor.Input.IsMuted && monitor.Input.Snapshot(0, Native.Clock).Count == 0, "Hardware preview is unmuted and retains no history");
                monitor.Input.Gain = 0; await Task.Delay(100);
                Check(monitor.Input.PeakDb == -120 && monitor.Input.RmsDb == -120, "Live volume change affects microphone processing immediately");
                monitor.Input.Gain = .4 * Math.Pow(10, 6.0 / 20); await Task.Delay(100);
                Check(monitor.Input.Gain > .79 && monitor.Buffer.OfferedBytes > 0, "Live boost/volume combine in the same processing path");
                hardware.Add(new { monitor.InputDeviceId, Input = monitor.Input.DeviceName, monitor.Input.CaptureFormat, monitor.OutputDeviceId,
                    Output = monitor.OutputDeviceName, monitor.OutputFormat, monitor.Buffer.RenderedBytes, monitor.Buffer.DroppedBytes,
                    ConfiguredRenderBufferMs = MicrophoneMonitor.PlaybackLatencyMilliseconds });
                await monitor.DisposeAsync();
                Check(monitor.WorkersStopped, "Repeated stop joins capture and render workers completely");
            }
            foreach (var loss in new[] { "output-default", "output-removed", "input-unplugged", "default-input", "capture-error" })
            {
                await using var monitor = new MicrophoneMonitor(loss == "default-input" ? "default" : selected, 0, -55);
                await monitor.StartAsync();
                if (loss == "output-default") monitor.OnDefaultDeviceChanged(DataFlow.Render, Role.Multimedia, "new-output-fixture");
                if (loss == "output-removed") monitor.OnDeviceRemoved(monitor.OutputDeviceId!);
                if (loss == "input-unplugged") monitor.OnDeviceStateChanged(monitor.InputDeviceId!, DeviceState.Unplugged);
                if (loss == "default-input") monitor.OnDefaultDeviceChanged(DataFlow.Capture, Role.Communications, "new-input-fixture");
                if (loss == "capture-error") typeof(AudioTrack).GetField("<Error>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(monitor.Input, "Synthetic device loss");
                for (var i = 0; i < 100 && !monitor.WorkersStopped; i++) await Task.Delay(20);
                Check(monitor.Error != null && monitor.WorkersStopped, loss + ": device-notification/fault handler stops only preview and disposes workers automatically");
            }
            for (var i = 0; i < 3; i++)
            {
                var monitor = new MicrophoneMonitor(selected, 0, -55);
                var start = monitor.StartAsync(); await monitor.DisposeAsync();
                try { await start; } catch (OperationCanceledException) { }
                Check(monitor.WorkersStopped, "Cancellation during startup leaves no audio workers");
            }
            Check(settingsBefore == null ? !File.Exists(config) : settingsBefore.SequenceEqual(File.ReadAllBytes(config)), "Start/stop/device loss never writes settings.json");
            Check(!Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Any(p => new[] { ".wav", ".mp4", ".mkv" }.Contains(Path.GetExtension(p))), "Hardware monitoring creates no production media");
            File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(new { Passed = true, checks, hardware, ScarlettVerified = scarlettVerified,
                Latency = "50 ms configured WASAPI render buffer; acoustic round-trip latency was not measured." })); return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(new { Passed = false, checks, hardware, Error = ex.ToString() })); return 1;
        }
    }
}
