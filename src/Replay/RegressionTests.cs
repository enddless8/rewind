using System.Runtime.InteropServices;
using NAudio.Wave;

namespace Replay;
internal static class RegressionTests
{
    public static async Task Run(string directory, Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        await RewindPacketCopyTests.Run(Path.Combine(directory, "rewind-packet-copy"), check);
        await RewindVideoExportTests.Run(Path.Combine(directory, "rewind-video-export"), check);
        await MicrophoneChannelTests.Run(Path.Combine(directory, "microphone-channels"), check);
        NativePcmTests.Run((value, name) => { if (!value) check(false, name); });
        check(true, "Native PCM conversion, mono/stereo, four sample rates and loss handling");
        var timeline = new AudioTimeline();
        List<AudioPacket> packets = [];
        const int packetFrames = 480, count = 200;
        var expected = new byte[packetFrames * count * AudioTrack.BytesPerFrame];
        for (var i = 0; i < packetFrames * count; i++)
        {
            short value = (short)(Math.Sin(2 * Math.PI * 437 * i / AudioTrack.Rate) * 12000);
            BitConverter.TryWriteBytes(expected.AsSpan(i * 4, 2), value);
            BitConverter.TryWriteBytes(expected.AsSpan(i * 4 + 2, 2), value);
        }
        for (var i = 0; i < count; i++)
        {
            var qpc = 10 + i * .01 + (i == 0 ? 0 : i % 2 == 0 ? .0007 : -.0007);
            var start = timeline.Place(i * packetFrames, packetFrames, qpc, i % 17 != 1, false, qpc + .02);
            packets.Add(new(start, expected.AsSpan(i * packetFrames * 4, packetFrames * 4).ToArray()));
        }
        var file = Path.Combine(directory, "jitter.wav");
        AudioTrack.WriteWave(file, packets, 10, 2, -55);
        using (var wave = new WaveFileReader(file))
        {
            var actual = new byte[expected.Length]; var read = wave.Read(actual, 0, actual.Length);
            check(read == expected.Length && actual.AsSpan().SequenceEqual(expected), "QPC jitter and timestamp errors do not change a single PCM sample");
        }
        var dropped = new AudioTimeline();
        dropped.Place(0, 480, 10, true, false, 10);
        var afterGap = dropped.Place(1440, 480, 10.03, true, true, 10.03);
        check(Math.Abs(afterGap - 10.03) < 1e-8 && dropped.MissingFrames == 960, "Real dropped frames retain their gap rather than shifting audio");

        var pending = Path.Combine(directory, "locked-video.tmp");
        var final = Path.Combine(directory, "retry.mkv");
        File.WriteAllText(pending, "test");
        using (var hold = new FileStream(pending, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var publish = ClipFiles.Publish(pending, final, "{\"ok\":true}");
            await Task.Delay(180); hold.Dispose();
            check(await publish == null && File.Exists(final) && File.Exists(Path.ChangeExtension(final, ".json")), "A transient scanner lock is retried successfully");
        }
        var pending2 = Path.Combine(directory, "metadata-video.tmp");
        var final2 = Path.Combine(directory, "metadata.mkv");
        File.WriteAllText(pending2, "test");
        using (var lockedMetadata = new FileStream(Path.ChangeExtension(final2, ".json"), FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var warning = await ClipFiles.Publish(pending2, final2, "{}");
            check(warning != null && File.Exists(final2), "A locked JSON sidecar does not report the saved video as lost");
        }

        // Exercise both WASAPI modes together, with a tone long enough to inspect packet seams.
        var tone = new byte[AudioTrack.Rate * 4 * 3];
        for (var i = 0; i < tone.Length / 4; i++)
        {
            short value = (short)(Math.Sin(2 * Math.PI * 437 * i / AudioTrack.Rate) * 2500);
            BitConverter.TryWriteBytes(tone.AsSpan(i * 4, 2), value); BitConverter.TryWriteBytes(tone.AsSpan(i * 4 + 2, 2), value);
        }
        using var system = new AudioTrack("System-test", false, null, null, 10, -55);
        using var process = new AudioTrack("Process-test", false, null, Environment.ProcessId, 10, -55);
        await system.Start(); await process.Start();
        using var input = new RawSourceWaveStream(new MemoryStream(tone), new WaveFormat(48000, 16, 2));
        using var player = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, false, 100);
        player.Init(input); var began = Native.Clock; player.Play(); await Task.Delay(3300); player.Stop();
        foreach (var track in new[] { system, process })
        {
            var captured = track.Snapshot(began + .5, began + 2.5);
            check(track.Health == null && captured.Count > 20, track.Name + ": event-driven capture supplies audio");
            var gaps = captured.Zip(captured.Skip(1), (a, b) => Math.Abs(b.Start - a.End) * AudioTrack.Rate).Count(x => x > .51);
            check(gaps == 0 && track.MissingFrames == 0, track.Name + ": no missing or overlapping PCM frames across packet boundaries");
            AudioTrack.WriteWave(Path.Combine(directory, track.Name + ".wav"), captured, began + .5, 2, -55);
        }
    }
}
