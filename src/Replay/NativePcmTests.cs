using System.Runtime.InteropServices;
using NAudio.Wave;

namespace Replay;
internal static class NativePcmTests
{
    public static void Run(Action<bool, string> check)
    {
        short[] pcm = [short.MinValue, -1, 0, short.MaxValue];
        var bytes = MemoryMarshal.AsBytes(pcm.AsSpan()).ToArray();
        var identity = new NativePcmConverter(new WaveFormat(48000, 16, 2)).Convert(bytes, 2, 10);
        check(identity.Data.SequenceEqual(bytes) && identity.Start == 10, "Native PCM16 remains sample-exact");
        float[] values = [.25f, -.5f, 0, 1];
        var mono = new NativePcmConverter(WaveFormat.CreateIeeeFloatWaveFormat(48000, 1))
            .Convert(MemoryMarshal.AsBytes(values.AsSpan()).ToArray(), values.Length, 10);
        check(MemoryMarshal.Cast<byte, short>(mono.Data).SequenceEqual(new short[] { 8192, 8192, -16384, -16384, 0, 0, 32767, 32767 }),
            "Native mono float becomes stereo with saturation");
        var pcm24 = new NativePcmConverter(new WaveFormat(48000, 24, 2)).Convert([0, 0, 128, 255, 255, 127], 1, 10);
        check(MemoryMarshal.Cast<byte, short>(pcm24.Data).SequenceEqual(new short[] { -32768, 32767 }), "PCM24 sign extension preserves full scale");
        foreach (var rate in new[] { 44100, 48000, 96000, 192000 })
        {
            var converter = new NativePcmConverter(WaveFormat.CreateIeeeFloatWaveFormat(rate, 2));
            var timeline = new AudioTimeline(rate);
            long outputFrames = 0;
            double next = 10, square = 0;
            // Irregular packet lengths expose per-packet rounding or resampler resets.
            for (var position = 0; position < rate;)
            {
                var frames = Math.Min(137 + position % 457, rate - position);
                var source = new float[frames * 2];
                for (var i = 0; i < frames; i++)
                {
                    var value = (float)(.25 * Math.Sin(2 * Math.PI * 437 * (position + i) / rate));
                    source[i * 2] = value; source[i * 2 + 1] = -value;
                }
                var start = timeline.Place(position, frames, 10 + position / (double)rate, true, false, 10 + (position + frames) / (double)rate);
                var packet = converter.Convert(MemoryMarshal.AsBytes(source.AsSpan()).ToArray(), frames, start);
                check(Math.Abs(packet.Start - next) < 1e-8, $"{rate} Hz: converted packet boundary remains contiguous");
                next = packet.End; outputFrames += packet.Data.Length / AudioTrack.BytesPerFrame;
                var samples = MemoryMarshal.Cast<byte, short>(packet.Data);
                for (var i = 0; i < samples.Length; i += 2) { square += Math.Pow(samples[i] / 32768.0, 2); }
                position += frames;
            }
            check(Math.Abs(outputFrames - AudioTrack.Rate) <= 2 && Math.Abs(Math.Sqrt(square / outputFrames) - .25 / Math.Sqrt(2)) < .003,
                $"{rate} Hz: one second resamples to 48 kHz without duration or signal loss (frames={outputFrames}, RMS={Math.Sqrt(square / outputFrames):F6})");
            var afterGap = converter.Convert(new byte[480 * 8], 480, 12);
            check(afterGap.Start == 12 && afterGap.Data.All(b => b == 0), $"{rate} Hz: real gaps reset resampler and keep silence");
        }
        var nativeClock = new AudioTimeline(44100);
        nativeClock.Place(0, 441, 10, true, false, 10);
        var afterLoss = nativeClock.Place(882, 441, 10.02, true, true, 10.02);
        check(Math.Abs(afterLoss - 10.02) < 1e-8 && nativeClock.MissingFrames == 480, "Native device clock preserves lost time and reports gaps in 48 kHz frames");
    }
}
