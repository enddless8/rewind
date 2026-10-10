using System.Runtime.InteropServices;
using System.Text.Json;

namespace Replay;
internal static class AudioControlsTest
{
    public static async Task<int> Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Check(bool value, string name) { if (!value) throw new Exception(name); checks.Add(name); }
        try
        {
            NativePcmTests.Run((value, name) => { if (!value) Check(false, name); });
            Check(true, "Native audio conversion and resampling regression checks");
            var data = new byte[8];
            short[] original = [1000, -1000, 30000, -30000];
            original.CopyTo(MemoryMarshal.Cast<byte, short>(data.AsSpan()));
            Check(!AudioTrack.ApplyGain(data, .5) && MemoryMarshal.Cast<byte, short>(data).SequenceEqual(new short[] { 500, -500, 15000, -15000 }), "50 percent scales both PCM channels exactly");
            Check(AudioTrack.ApplyGain(data, 4) && MemoryMarshal.Cast<byte, short>(data).SequenceEqual(new short[] { 2000, -2000, 32767, -32768 }), "Boost detects overload and saturates without integer wraparound");
            AudioTrack.ApplyGain(data, 0); Check(data.All(b => b == 0), "Zero volume mutes PCM");
            var settings = new Settings { MicrophoneVolume = 50, MicrophoneBoostDb = 20, SystemVolume = 25 };
            Check(Math.Abs(settings.MicrophoneGain - 5) < .00001, "Volume and dB boost combine correctly");
            var restored = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;
            Check(restored.SystemVolume == 25 && restored.MicrophoneGain == 5, "Audio controls survive settings serialization");
            Check(JsonSerializer.Deserialize<Settings>("{}")!.MicrophoneGain == 1, "Existing settings retain original unity gain");
            settings.MicrophoneBoostDb = double.NaN;
            bool rejected = false; try { settings.Validate(); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "Non-finite gain rejected");
            var selected = Settings.Load().MicrophoneId;
            using (var mic = new AudioTrack("Test", true, selected == "off" ? "default" : selected, null, 0, -55))
            {
                await mic.Start(); await Task.Delay(600);
                Check(mic.Health == null && Native.Clock - mic.LastPacket < .2, "Standalone microphone delivers current packets without video buffer");
                Check(mic.Snapshot(0, Native.Clock).Count == 0, "Meter-only test retains no audio history");
                mic.Gain = 0; await Task.Delay(200);
                Check(mic.PeakDb == -120 && mic.RmsDb == -120, "Live meter follows gain change on incoming PCM");
            }
            File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(new { Passed = true, checks })); return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(new { Passed = false, checks, Error = ex.ToString() })); return 1;
        }
    }
}
