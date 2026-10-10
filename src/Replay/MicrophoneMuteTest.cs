using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using NAudio.Wave;
using Brushes = System.Windows.Media.Brushes;

namespace Replay;
internal static class MicrophoneMuteTest
{
    private static byte[] Tone(long first, int frames)
    {
        var data = new byte[frames * 4]; var samples = MemoryMarshal.Cast<byte, short>(data.AsSpan());
        for (int i = 0; i < frames; i++)
            samples[2*i] = samples[2*i+1] = (short)Math.Round(12000 * Math.Sin(2*Math.PI*437*(first+i)/48000));
        return data;
    }
    private static double Rms(byte[] data, int firstFrame, int frames)
    {
        var samples = MemoryMarshal.Cast<byte, short>(data.AsSpan(firstFrame*4,frames*4));
        double sum=0; foreach(var s in samples)sum+=(double)s*s;
        return Math.Sqrt(sum/samples.Length);
    }
    public static async Task<int> Run(string directory)
    {
        Directory.CreateDirectory(directory); List<string> checks=[];
        void Check(bool good,string description){if(!good)throw new Exception(description);checks.Add(description);}
        Window? target=null;
        try
        {
            var control = new MicrophoneMuteControl();
            using (var track = new AudioTrack("Microphone",true,"fixture",null,30,-55,control))
            {
                track.Gain = .5*Math.Pow(10,6/20);
                var gainBefore = track.Gain; var at = Native.Clock; var converter = new NativePcmConverter(new WaveFormat(48000,16,2));
                var timeline = new AudioTimeline();
                for(int i=0;i<300;i++)
                {
                    if(i==100){control.SetMuted(true);control.SetMuted(true);}
                    if(i==150)track.Gain=.25*Math.Pow(10,6/20);
                    if(i==200){control.SetMuted(false);control.SetMuted(false);}
                    var start=timeline.Place(i*480L,480,at+i*.01,true,false,at+i*.01);
                    track.ProcessPacket(converter.Convert(Tone(i*480,480),480,start),at+i*.01);
                }
                var packets=track.Snapshot(at,at+3);
                Check(packets.Count==300&&packets.Sum(p=>p.Data.Length)==3*192000,"Mute preserves every packet, frame and sample count");
                Check(packets.Select((p,i)=>Math.Abs(p.Start-(at+i*.01))<1e-7).All(x=>x)&&timeline.MissingFrames==0&&timeline.Discontinuities==0,"Mute preserves timestamps and introduces no timeline gaps");
                Check(packets.Take(100).All(p=>!p.IntentionallyMuted&&p.Data.Any(b=>b!=0))&&packets.Skip(100).Take(100).All(p=>p.IntentionallyMuted&&p.Data.All(b=>b==0))&&packets.Skip(200).All(p=>!p.IntentionallyMuted&&p.Data.Any(b=>b!=0)),"Audio before, exact zero PCM during, and audio after runtime mute");
                Check(Math.Abs(track.Gain-gainBefore/2)<1e-10,"Updated configured gain survives mute and is used after unmute");
                var hash=System.Security.Cryptography.SHA256.HashData(packets[0].Data);
                control.SetMuted(true);
                Check(hash.SequenceEqual(System.Security.Cryptography.SHA256.HashData(packets[0].Data))&&!packets[0].IntentionallyMuted,"Muting cannot retroactively alter buffered samples or packet annotations");
                var wave=Path.Combine(directory,"fixture.wav");
                var report=AudioTrack.WriteWave(wave,packets,at,3,-55);
                Check(Math.Abs(report.IntentionalMuteSeconds-1)<1e-8&&report.LongestUnintentionalSilenceSeconds<.021,"Wave diagnostics separate intentional mute from unexpected silence");
                using(var reader=new WaveFileReader(wave))
                {
                    var data=new byte[reader.Length];reader.ReadExactly(data);
                    Check(data.Length==3*192000&&data.AsSpan(192000,192000).IndexOfAnyExcept((byte)0)<0,"Written microphone WAV has exact duration and one second of sample-exact silence");
                    Check(Math.Abs(Rms(data,96000,48000)/Rms(data,0,48000)-.5)<.001,"Latest volume/boost resumes after unmute rather than unity gain");
                }
                var originalSignal=track.LastSignal;
                Check(track.SignalSilenceSeconds==0,"Intentional live mute suppresses signal-silence warnings");
                control.SetMuted(false);
                Check(track.SignalSilenceSeconds<.1&&track.LastSignal==originalSignal,"Unmute starts a diagnostic grace interval without faking LastSignal");
                var failure=new List<AudioPacket>{new(0,new byte[192000]),new(1,new byte[192000]){IntentionallyMuted=true},new(2,new byte[192000])};
                var failed=AudioTrack.WriteWave(Path.Combine(directory,"unexpected-silence.wav"),failure,0,3,-55);
                Check(failed.LongestUnintentionalSilenceSeconds>.99&&failed.LongestUnintentionalSilenceSeconds<1.021&&failed.IntentionalMuteSeconds==1,"Intentional mute does not suppress unrelated unmuted silence diagnostics");
                var clipped=Recorder.JoinAudio(packets,[new("fixture",at+.25,at+2.75)],at+2.75,0);
                var clippedReport=AudioTrack.WriteWave(Path.Combine(directory,"trimmed-fixture.wav"),clipped,0,2.5,-55);
                Check(Math.Abs(clippedReport.IntentionalMuteSeconds-1)<1e-8,"Existing segment join/trim preserves historical mute annotations");
                var overlap=AudioTrack.WriteWave(Path.Combine(directory,"overlap.wav"),[new(0,new byte[192000]){IntentionallyMuted=true},new(.5,new byte[192000]){IntentionallyMuted=true}],0,2,-55);
                Check(overlap.IntentionalMuteSeconds==1.5&&overlap.LongestUnintentionalSilenceSeconds>.49,"Overlapping mute intervals are merged and genuine missing time remains diagnostic");
                using var system=new AudioTrack("System",false,null,null,30,-55,control);
                control.SetMuted(true); system.ProcessPacket(new(at,Tone(0,480)),at);
                Check(!system.IsMuted&&system.RmsDb>-30,"Runtime microphone mute never gates system/game/Discord audio");
                using var meter=new AudioTrack("Microphone test",true,"fixture",null,0,-55);
                meter.ProcessPacket(new(at,Tone(0,480)),at);
                Check(!meter.IsMuted&&meter.RmsDb>-30&&meter.Snapshot(0,at+1).Count==0,"Independent microphone test keeps its own unmuted meter-only state");
            }
            // Verify the actual WASAPI route without requiring speech or a playback loop.
            var selected=Settings.Load().MicrophoneId;
            var liveControl=new MicrophoneMuteControl();
            using(var live=new AudioTrack("Microphone",true,selected=="off"?"default":selected,null,10,-55,liveControl))
            {
                await live.Start();await Task.Delay(300);
                var device=live.DeviceName;var format=live.CaptureFormat;var runner=typeof(AudioTrack).GetField("runner",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(live);
                live.Gain=.37; liveControl.SetMuted(true);var mutedAt=Native.Clock;await Task.Delay(400);
                var silent=live.Snapshot(mutedAt+.05,Native.Clock);
                Check(silent.Count>0&&silent.All(p=>p.IntentionallyMuted&&p.Data.All(b=>b==0))&&live.Health==null,"Actual WASAPI microphone continues delivering timed silent packets while muted");
                liveControl.SetMuted(false);await Task.Delay(300);
                Check(live.Gain==.37&&live.DeviceName==device&&live.CaptureFormat==format&&ReferenceEquals(runner,typeof(AudioTrack).GetField("runner",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(live)),"Mute/unmute preserves the WASAPI worker, endpoint, native format and configured gain");
            }
            target=new Window{Title="Проверка микрофона Rewind",Width=640,Height=360,Background=Brushes.DarkSlateBlue,Content=new System.Windows.Controls.TextBlock{Text="Проверка микрофона",FontSize=32}};
            target.Show();var animation=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(50)};
            animation.Tick+=(_,_)=>((System.Windows.Controls.TextBlock)target.Content).Text=$"Проверка микрофона\n{DateTime.Now:HH:mm:ss.fff}";animation.Start();
            try
            {
                var game=new GameWindow(new WindowInteropHelper(target).Handle,Environment.ProcessId,"MicrophoneMuteFixture",target.Title);
                var settings=new Settings{CaptureMode="Game",AudioMode="Game",Height=720,Fps=30,Codec="H264",MicrophoneId="off",Discord=false,BufferSeconds=20,SilenceWarningSeconds=1,BufferDirectory=Path.Combine(directory,"buffer"),OutputDirectory=Path.Combine(directory,"clips")};
                var captureControl=new MicrophoneMuteControl();
                await using var recorder=new Recorder(settings,game,Native.Displays().First(d=>d.Primary),captureControl);
                var fixtureOrigin=Native.Clock;
                await recorder.Start();
                using var fixture=new AudioTrack("Microphone",true,"fixture",null,40,-55,captureControl){Gain=1};
                ((List<AudioTrack>)typeof(Recorder).GetField("tracks",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(recorder)!).Add(fixture);
                using var cancel=new CancellationTokenSource();
                var feed=Task.Run(async()=>
                {
                    var origin=fixtureOrigin;long i=0;
                    var converter=new NativePcmConverter(new WaveFormat(48000,16,2));
                    while(!cancel.IsCancellationRequested)
                    {
                        var until=origin+i*.02-Native.Clock;if(until>0)await Task.Delay(TimeSpan.FromSeconds(until));
                        fixture.ProcessPacket(converter.Convert(Tone(i*960,960),960,origin+i*.02),Native.Clock);i++;
                    }
                });
                try
                {
                    await Task.Delay(2500);var pre=fixture.Snapshot(0,Native.Clock).First();var preHash=pre.Data.ToArray();
                    captureControl.SetMuted(true);await Task.Delay(2100);fixture.Gain=.5;captureControl.SetMuted(false);await Task.Delay(2500);
                    await recorder.Maintain();var clip=await recorder.Save();
                    Check(recorder.Running&&preHash.SequenceEqual(pre.Data),"Live mute/unmute and Save preserve the running video producer and existing audio history");
                    var meta=JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(clip.Path,".json")));
                    var signal=meta.RootElement.GetProperty("Tracks").GetProperty("Microphone");
                    Check(signal.GetProperty("IntentionalMuteSeconds").GetDouble()>2&&signal.GetProperty("LongestUnintentionalSilenceSeconds").GetDouble()<.1&&!clip.Warnings.Any(w=>w.StartsWith("Микрофон: нет сигнала")),"One actual saved Rewind clip records mute duration without a false microphone-silence failure");
                    var probe=await MediaTools.Run("ffprobe",["-v","error","-show_streams","-of","json",clip.Path]);
                    using var info=JsonDocument.Parse(probe.Output);
                    var index=info.RootElement.GetProperty("streams").EnumerateArray().First(s=>s.TryGetProperty("tags",out var tags)&&tags.TryGetProperty("title",out var title)&&title.GetString()=="Microphone").GetProperty("index").GetInt32();
                    var raw=Path.Combine(directory,"saved-microphone.pcm");
                    var decoded=await MediaTools.Run("ffmpeg",["-v","error","-i",clip.Path,"-map",$"0:{index}","-f","s16le","-ar","48000","-ac","2","-y",raw]);
                    var pcm=File.ReadAllBytes(raw);var last=(int)(clip.Duration*48000);
                    Check(decoded.Code==0&&Rms(pcm,24000,48000)>3000&&Rms(pcm,3*48000,48000)<5&&Rms(pcm,last-72000,48000)>1000,"Decoded microphone AAC in a real MKV contains audio before, timed silence during, and audio after mute");
                    Check(Math.Abs(pcm.Length/192000.0-clip.Duration)<.1,"Saved audio duration remains aligned with the actual video/clip timeline");
                    captureControl.SetMuted(true);var mic=recorder.Microphone;
                    await recorder.SimulateAccessLossForTest();await recorder.RestartCapture();await Task.Delay(300);
                    Check(recorder.Running&&ReferenceEquals(mic,recorder.Microphone)&&fixture.IsMuted&&fixture.RmsDb==-120,"Capture recovery preserves the same microphone track and shared runtime mute intent");
                }
                finally{cancel.Cancel();await feed;}
            }
            finally{animation.Stop();}
            File.WriteAllText(Path.Combine(directory,"results.json"),JsonSerializer.Serialize(new{Passed=true,checks}));return 0;
        }
        catch(Exception ex){File.WriteAllText(Path.Combine(directory,"results.json"),JsonSerializer.Serialize(new{Passed=false,checks,Error=ex.ToString()}));return 1;}
        finally{target?.Close();}
    }
}
