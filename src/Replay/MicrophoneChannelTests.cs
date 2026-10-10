using System.Runtime.InteropServices;
using NAudio.Wave;

namespace Replay;

internal static class MicrophoneChannelTests
{
    internal static async Task Run(string directory, Action<bool, string> check)
    {
        directory = Path.Combine(directory, "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var stereo = new MicrophoneChannelCapability(2, true, "Подтверждён многоканальный вход.");
        var mono = new MicrophoneChannelCapability(1, false, "устройство предоставляет один входной канал.");
        var line = new Guid("DFF21FE3-F70F-11D0-B917-00A0C9223196");
        var ordinary = new Guid("DFF21BE1-F70F-11D0-B917-00A0C9223196");
        check(MicrophoneChannels.FromTopology(2, 3, new([], [new("line", "", line, 0)])).ShowMode, "Hardware line terminal plus multichannel PCM exposes routing without a device-name whitelist");
        check(!MicrophoneChannels.FromTopology(1, 1, new([], [new("line", "", line, 0)])).ShowMode, "Mono shared format hides even a multichannel interface");
        check(!MicrophoneChannels.FromTopology(2, 3, new([], [new("usb-mic", "", ordinary, 0)])).ShowMode, "Stereo USB microphone format alone never exposes routing");
        check(!MicrophoneChannels.FromTopology(2, 3, new([], [])).ShowMode, "Missing hardware topology remains safe Auto");
        check(!MicrophoneChannels.FromTopology(2, 3, new([], [new("line", "", line, 0), new("mux", "", new("2CEAF780-C556-11D0-8A2B-00A0C9255AC1"), 1)])).ShowMode, "Multiplexed alternatives are not independent simultaneous inputs");
        var jacks = new AudioInputTopology.Route([new("a", ordinary, 1, 10, 0), new("b", ordinary, 2, 10, 0)], [new("a", "", ordinary, 0)]);
        check(MicrophoneChannels.FromTopology(2, 3, jacks).ShowMode, "Separate physical jack maps prove independent channels");
        check(!MicrophoneChannels.FromTopology(2, 0, jacks).ShowMode, "Ambiguous channel ordering cannot enable routing through jack maps");
        var old = Settings.FromJson("{\"BufferSeconds\":180}");
        check(old.MicrophoneChannelMode == "Auto", "Missing legacy config field defaults to Auto");
        var invalid = new Settings { MicrophoneChannelMode = "invalid" };
        bool rejected = false; try { invalid.Validate(); } catch (ArgumentException) { rejected = true; }
        check(rejected, "Unknown channel-mode configuration is validated");

        foreach (var mode in Enum.GetValues<MicrophoneChannelMode>())
        {
            var config = new Settings { MicrophoneChannelMode = mode.ToString() }; config.Validate();
            check(Settings.FromJson(System.Text.Json.JsonSerializer.Serialize(config)).MicrophoneChannelMode == mode.ToString(), mode + ": exact config serialization round-trip");
            foreach (var (l,r) in new (short,short)[] { (12000,0),(0,-8000),(12000,-8000),(32767,32767),(-32768,-32768) })
            {
                short[] input = [l,r];
                var packet = new NativePcmConverter(new WaveFormat(48000,16,2),mode).Convert(MemoryMarshal.AsBytes(input.AsSpan()).ToArray(),1,10);
                var samples = MemoryMarshal.Cast<byte,short>(packet.Data);
                var expectedL = mode switch { MicrophoneChannelMode.Channel2 => r, MicrophoneChannelMode.MonoMix => (short)Math.Round((l+(double)r)/2), _ => l };
                var expectedR = mode switch { MicrophoneChannelMode.Channel1 => l, MicrophoneChannelMode.MonoMix => expectedL, _ => r };
                check(samples[0] == expectedL && samples[1] == expectedR && packet.Start == 10 && packet.Data.Length == 4, $"{mode}: exact L/R mapping ({l},{r}), unchanged QPC and frame count");
            }
            short[] single = [12000,-6000];
            var singlePacket = new NativePcmConverter(new WaveFormat(48000,16,1),mode).Convert(MemoryMarshal.AsBytes(single.AsSpan()).ToArray(),2,10);
            check(MemoryMarshal.Cast<byte,short>(singlePacket.Data).SequenceEqual(new short[]{12000,12000,-6000,-6000}), mode + ": mono converter fallback never selects a missing physical channel");
            using var monoTrack = new AudioTrack("Microphone",true,"fixture",null,1,-55) { MicrophoneChannelPreference = mode.ToString() };
            monoTrack.CreateConverter(new WaveFormat(48000,16,1),mono);
            check(monoTrack.EffectiveMicrophoneChannelMode == MicrophoneChannelMode.Auto && (mode == MicrophoneChannelMode.Auto || monoTrack.ChannelModeFeedback != null), mode + ": unsupported source gives explicit feedback and preserves preference");

            // Shared post-conversion packets must reach Rewind, conventional writer,
            // and the monitor buffer with exactly one volume/boost application.
            var mute = new MicrophoneMuteControl(); var preview = new MicrophoneMonitorBuffer();
            using var track = new AudioTrack("Microphone",true,"fixture",null,60,-55,mute,preview.Offer)
                { MicrophoneChannelPreference=mode.ToString(), Gain=.5*Math.Pow(10,6.0/20) };
            var converter = track.CreateConverter(new WaveFormat(48000,16,2),stereo);
            var now = Native.Clock;
            var source = new short[480*2]; for(var f=0;f<480;f++) { source[f*2]=6000;source[f*2+1]=-2000; }
            var converted = converter.Convert(MemoryMarshal.AsBytes(source.AsSpan()).ToArray(),480,now);
            var expected = converted.Data.ToArray(); AudioTrack.ApplyGain(expected,track.Gain);
            track.ProcessPacket(converted,now);
            check(converted.Data.SequenceEqual(expected) && !track.Clipping, mode + ": volume/boost applied exactly once after mapping without clipping");
            check(track.Snapshot(now,now+.01).Single().Data.SequenceEqual(expected), mode + ": Rewind retains exact processed channel PCM");
            var rendered = new byte[expected.Length]; preview.Read(rendered,0,rendered.Length);
            check(rendered.SequenceEqual(expected), mode + ": monitoring receives the same processed channel PCM");
            var writerDir = Path.Combine(directory,mode.ToString()); Directory.CreateDirectory(writerDir);
            var writer = new RecordingAudioWriter(writerDir,"microphone",now);
            track.AttachRecording(writer,now);
            track.DetachRecording(); await writer.Complete(now+.01);
            check(File.ReadAllBytes(writer.Path).SequenceEqual(expected), mode + ": conventional recording writes sample-exact channel PCM");
            var wavePath = Path.Combine(writerDir,"rewind.wav");
            AudioTrack.WriteWave(wavePath,track.Snapshot(now,now+.01),now,.01,-55);
            using(var reader = new WaveFileReader(wavePath))
            { var decoded = new byte[expected.Length];reader.ReadExactly(decoded);check(decoded.SequenceEqual(expected), mode + ": Rewind WAV decoded PCM preserves mapping and duration"); }
            mute.SetMuted(true);
            var muted = converter.Convert(MemoryMarshal.AsBytes(source.AsSpan()).ToArray(),480,now+.01);
            track.ProcessPacket(muted,now+.01);
            check(muted.IntentionallyMuted && muted.Data.All(b=>b==0) && track.PeakDb==-120 && track.SignalSilenceSeconds==0, mode + ": runtime mute and intentional-silence diagnostics still apply");
            mute.SetMuted(false);
            using var monitor = new MonitorScope(new MicrophoneMonitor("fixture",1,-55,mode.ToString()));
            check(monitor.Value.Input.MicrophoneChannelPreference == mode.ToString(), mode + ": preview owns the selected preference independently of production mute/state");

            foreach (var rate in new[]{44100,48000,96000,192000})
            {
                var timing = new NativePcmConverter(WaveFormat.CreateIeeeFloatWaveFormat(rate,2),mode);
                double next=10;long total=0;
                for(var position=0;position<rate;)
                {
                    var frames=Math.Min(137+position%457,rate-position);var floats=new float[frames*2];
                    for(var f=0;f<frames;f++){floats[f*2]=.25f;floats[f*2+1]=-.125f;}
                    var result=timing.Convert(MemoryMarshal.AsBytes(floats.AsSpan()).ToArray(),frames,10+position/(double)rate);
                    check(Math.Abs(result.Start-next)<1e-8, $"{mode}/{rate}: irregular packet timestamps remain contiguous");
                    next=result.End;total+=result.Data.Length/4;position+=frames;
                }
                check(Math.Abs(total-48000)<=2, $"{mode}/{rate}: channel mapping neither drops frames nor changes resampler timing");
            }
            foreach(var name in new[]{"System","Discord"})
            {
                using var unrelated = new AudioTrack(name,false,null,null,10,-55) { MicrophoneChannelPreference=mode.ToString() };
                var raw=MemoryMarshal.AsBytes(new short[]{6000,-2000}.AsSpan()).ToArray();
                var output=unrelated.CreateConverter(new WaveFormat(48000,16,2),stereo).Convert(raw,1,10);
                check(output.Data.SequenceEqual(raw) && unrelated.EffectiveMicrophoneChannelMode==MicrophoneChannelMode.Auto,mode + ": " + name + " audio ignores microphone routing");
            }
        }
        float[] many=[.25f,-.125f,.5f,-.25f];
        var four=MemoryMarshal.AsBytes(many.AsSpan()).ToArray();
        var mixed=new NativePcmConverter(WaveFormat.CreateIeeeFloatWaveFormat(48000,4),MicrophoneChannelMode.MonoMix).Convert(four,1,10);
        check(MemoryMarshal.Cast<byte,short>(mixed.Data).SequenceEqual(new short[]{3072,3072}),"Mono mix averages every native input channel, including channels beyond the first pair");
        var separated=new NativePcmConverter(WaveFormat.CreateIeeeFloatWaveFormat(48000,4),MicrophoneChannelMode.Stereo).Convert(four,1,10);
        check(MemoryMarshal.Cast<byte,short>(separated.Data).SequenceEqual(new short[]{8192,-4096}),"Stereo preserves the first pair without legacy speaker downmix");
    }
    private sealed class MonitorScope(MicrophoneMonitor value) : IDisposable
    { internal MicrophoneMonitor Value=>value; public void Dispose()=>value.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
}
