using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.Wave;
using Brushes = System.Windows.Media.Brushes;

namespace Replay;

internal static class RecordingTest
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static HashSet<int> Processes() { HashSet<int> ids = []; foreach (var p in Process.GetProcessesByName("ffmpeg")) using (p) ids.Add(p.Id); return ids; }
    private static AudioTrack[] Tracks(Recorder r) => ((List<AudioTrack>)typeof(Recorder).GetField("tracks", Private)!.GetValue(r)!).ToArray();
    private static Process Producer(Recorder r) => (Process)typeof(Recorder).GetField("video", Private)!.GetValue(r)!;
    private static byte[] Tone(int frames)
    {
        var data = new byte[frames * 4]; var pcm = MemoryMarshal.Cast<byte, short>(data.AsSpan());
        for (int i = 0; i < frames; i++) pcm[i * 2] = pcm[i * 2 + 1] = (short)(Math.Sin(i * 2 * Math.PI * 440 / 48000) * 10000);
        return data;
    }
    private sealed class ToneSource : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = new(48000, 16, 2);
        public int Read(byte[] buffer, int offset, int count) { Tone(count / 4).CopyTo(buffer, offset); return count; }
    }
    public static async Task<int> RunEdges(string directory)
    {
        directory = Path.GetFullPath(directory);Directory.CreateDirectory(directory);
        List<string> checks = [];List<object> artifacts=[];
        void Check(bool value,string name) { if(!value)throw new Exception(name);checks.Add(name); }
        Recorder? recorder=null;Window? target=null;var animation=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(80)};
        var baseline=Processes();
        try
        {
            Check(new DriveInfo(Path.GetPathRoot(directory)!).AvailableFreeSpace>300L*1024*1024,"Disk precondition checked before edge tests");
            // Offsets are applied at the sink's QPC origin, matching Rewind JoinAudio's +offset source interval.
            foreach(var offset in new[]{-.5,.5})
            {
                var root=Path.Combine(directory,offset<0?"offset-negative":"offset-positive");Directory.CreateDirectory(root);
                using var track=new AudioTrack("System",false,null,null,2,-55);
                track.ProcessPacket(new(99.5,Tone(24000)),100);
                var audio=new RecordingAudioWriter(root,"System",100+offset);track.AttachRecording(audio,100+offset);
                track.ProcessPacket(new(100,Tone(48000)),101);track.DetachRecording();await audio.Complete(101+offset);
                var data=File.ReadAllBytes(audio.Path);
                Check(data.Length==192000&&data.AsSpan(0,96000).ContainsAnyExcept((byte)0),"Offset/pre-roll uses QPC interval and keeps fixed session duration: "+offset);
                if(offset>0)Check(!data.AsSpan(96000).ContainsAnyExcept((byte)0),"Missing positive-offset tail becomes timed silence rather than shortened audio");
            }
            target=new Window{Title="Проверка исчезновения источника",Width=600,Height=340,Background=Brushes.DarkSlateBlue,Content=new TextBlock{FontSize=30}};
            target.Show();animation.Tick+=(_,_)=>((TextBlock)target.Content).Text=DateTime.Now.ToString("HH:mm:ss.fff");animation.Start();await Task.Delay(150);
            var game=new GameWindow(new WindowInteropHelper(target).Handle,Environment.ProcessId,"DisappearingRecordingFixture",target.Title);
            var display=Native.Displays().First(d=>d.Primary);
            var settings=new Settings{CaptureMode="Game",AudioMode="Game",MicrophoneId="MissingRecordingDeviceFixture",Discord=false,Height=720,Fps=30,Codec="H264",BufferSeconds = 15,
                GroupByApplication=false,Quality=35,MaxBitrateMbps=2,OutputDirectory=Path.Combine(directory,"output"),BufferDirectory=Path.Combine(directory,"buffer")};
            recorder=new(settings,game,display);await recorder.Start();recorder.FreeSpace=_=>1;
            bool refused=false;try { await recorder.StartRecording(); }catch(IOException){refused=true;}
            Check(refused&&recorder.Recording.State==RecordingState.Error&&Directory.GetDirectories(settings.BufferDirectory,".recording-*").Length==0&&recorder.Running,"Insufficient disk refuses a new session without destroying warm Rewind capture");
            recorder.FreeSpace=path=>new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace;
            await recorder.StartRecording();await Task.Delay(2300);
            var lockedSession=recorder.Recording.SessionDirectory!;
            using(var lockedJournal=new FileStream(Path.Combine(lockedSession,"recording.json"),FileMode.Open,FileAccess.Read,FileShare.None))
            {
                var completed=await recorder.StopRecording().WaitAsync(TimeSpan.FromSeconds(15));
                Check(completed!=null&&File.Exists(completed.Path)&&recorder.Recording.State==RecordingState.Error,"Locked lifecycle journal cannot prevent stopping, closing audio or returning a published file");
                Check(completed!.Warnings.Any(w=>w.Contains("Microphone")),"Unavailable selected microphone warning is preserved in conventional output rather than silently omitted");
                artifacts.Add(new{LockedJournalRecording=completed});
            }
            Check(!Recorder.FindIncompleteRecordings(settings.BufferDirectory).Any(s=>s.Directory==lockedSession),"Leftovers after successful publication are distinguished from incomplete sessions");
            await recorder.StartRecording();await Task.Delay(2300);
            bool localFailure=false;try{await ScreenshotCapture.Take(new("Game",true,30,game,display with{Device="MissingRecordingMonitor"},settings.OutputDirectory,false,null));}catch(IOException){localFailure=true;}
            Check(localFailure&&recorder.Recording.State==RecordingState.Recording&&recorder.Running,"Screenshot failure is local while conventional recording is active");
            target.Close();target=null;animation.Stop();
            for(int i=0;i<150&&recorder.Recording.State!=RecordingState.Error;i++)await Task.Delay(100);
            Check(recorder.Recording.State==RecordingState.Error&&recorder.Recording.Result!=null&&recorder.Recording.Error!.Contains("Захват"),"Actual HWND disappearance terminates recording and preserves a valid available portion");
            var partial=recorder.Recording.Result!;
            Check((await MediaTools.Run("ffmpeg",["-v","error","-i",partial.Path,"-map","0","-f","null","-"])).Code==0,"Source-disappearance output fully decodes");
            artifacts.Add(new{SourceDisappeared=partial});await recorder.DisposeAsync();recorder=null;
            // Simulate abnormal termination after confirmed ownership, using the real journal and finalizer.
            var crashDir=Path.Combine(settings.BufferDirectory,".recording-crash-fixture");Directory.CreateDirectory(crashDir);
            var clipSegment=Path.Combine(crashDir,"video-000000000.mkv");
            var generated=await MediaTools.Run("ffmpeg",["-v","error","-f","lavfi","-i","color=s=1280x720:r=30","-t","2","-an","-c:v","h264_nvenc","-y",clipSegment]);
            Check(generated.Code==0,"Crash fixture contains a real independently decodable closed MKV segment");
            File.WriteAllText(Path.Combine(crashDir,"segments.jsonl"),JsonSerializer.Serialize(new Segment(Path.GetFileName(clipSegment),100,102))+"\n{torn");
            var crash=new RecordingManifest{Settings=settings,ApplicationName="Crash",Encoder="h264_nvenc",Backend="fixture",StartedAt=100.25,State=RecordingState.Recording};
            File.WriteAllText(Path.Combine(crashDir,"recording.json"),JsonSerializer.Serialize(crash));
            Check(Recorder.FindIncompleteRecordings(settings.BufferDirectory).Any(s=>s.Directory==crashDir&&s.ConfirmedEnd==102),"Abnormal termination with active manifest and torn final journal line is detected safely");
            var recovered=await Recorder.RecoverRecording(crashDir);
            Check(Math.Abs(recovered.Duration-1.75)<.01&&!Directory.Exists(crashDir)&&(await MediaTools.Run("ffmpeg",["-v","error","-i",recovered.Path,"-f","null","-"])).Code==0,"Recovery trims active crash session to confirmed interval and publishes without recovery-gap splicing");
            artifacts.Add(new{CrashRecovered=recovered});
            var corrupt=Path.Combine(settings.BufferDirectory,".recording-corrupt-fixture");Directory.CreateDirectory(corrupt);File.WriteAllText(Path.Combine(corrupt,"recording.json"),"{corrupt");
            Check(Recorder.FindIncompleteRecordings(settings.BufferDirectory).Any(s=>s.Directory==corrupt&&s.State==RecordingState.Error)&&Directory.Exists(corrupt),"Corrupt journal detection preserves potentially recoverable media and reports an error");
            Check(Processes().IsSubsetOf(baseline),"Edge cases leak no FFmpeg processes");
            File.WriteAllText(Path.Combine(directory,"results.json"),JsonSerializer.Serialize(new{Passed=true,checks,artifacts},new JsonSerializerOptions{WriteIndented=true}));return 0;
        }
        catch(Exception ex){File.WriteAllText(Path.Combine(directory,"results.json"),JsonSerializer.Serialize(new{Passed=false,checks,artifacts,Error=ex.ToString()},new JsonSerializerOptions{WriteIndented=true}));return 1;}
        finally{animation.Stop();target?.Close();if(recorder!=null)await recorder.DisposeAsync();}
    }
    public static async Task<int> Run(string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        List<string> checks = []; List<object> artifacts = [];
        void Check(bool value, string name) { if (!value) throw new Exception(name); checks.Add(name); }
        var baseline = Processes(); var config = Path.Combine(Settings.DataDirectory, "settings.json");
        var bytes = File.Exists(config) ? File.ReadAllBytes(config) : null;
        Window? target = null; Recorder? active = null;
        var animation = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        try
        {
            Check(new DriveInfo(Path.GetPathRoot(directory)!).AvailableFreeSpace > 350L * 1024 * 1024, "Disk checked before bounded native recording tests (>350MB)");
            // Deterministic post-gain/shared-mute path uses the actual streaming writer (no mock exporter).
            var audioDir = Path.Combine(directory, "audio-fixture"); Directory.CreateDirectory(audioDir);
            var mute = new MicrophoneMuteControl(); using var fixture = new AudioTrack("Microphone", true, "off", null, 1, -55, mute);
            var writer = new RecordingAudioWriter(audioDir, "Microphone", 100);
            fixture.AttachRecording(writer, 100); fixture.Gain = 2;
            for (int i = 0; i < 150; i++)
            {
                if (i == 50) mute.SetMuted(true); if (i == 100) mute.SetMuted(false);
                fixture.ProcessPacket(new(100 + i * .02, Tone(960)), 100 + i * .02); await Task.Delay(1);
            }
            fixture.DetachRecording(); await writer.Complete(103);
            var pcm = File.ReadAllBytes(writer.Path);
            Check(pcm.Length == 3 * 192000 && writer.Error == null && writer.QueuedBytes == 0, "Streaming PCM drains a bounded queue and preserves complete duration");
            Check(pcm.AsSpan(0, 192000).ContainsAnyExcept((byte)0) && !pcm.AsSpan(192000, 192000).ContainsAnyExcept((byte)0) && pcm.AsSpan(384000, 192000).ContainsAnyExcept((byte)0), "Microphone fixture has audio / one-second intentional silence / audio after unmute");
            Check(File.ReadLines(Path.Combine(audioDir, "Microphone.mute.jsonl")).Count() == 50, "Historical intentional mute timing is streamed to disk rather than lost");
            var saturated = new RecordingAudioWriter(audioDir, "Overflow", 0); saturated.Offer(new(0, new byte[RecordingAudioWriter.QueueByteLimit + 4]));
            await saturated.Complete(0);
            Check(saturated.Error != null && saturated.QueuedBytes == 0, "Bounded audio overflow reports failure without blocking producer or growing RAM");
            var display = Native.Displays().First(d => d.Primary);
            target = new Window { Width = 640, Height = 380, Topmost = true, Title = "Проверка обычной записи", Background = Brushes.DarkSlateBlue,
                Content = new TextBlock { Foreground = Brushes.White, FontSize = 32 } };
            target.Show(); target.Activate();
            animation.Tick += (_, _) => ((TextBlock)target.Content).Text = $"Запись\n{DateTime.Now:HH:mm:ss.fff}"; animation.Start(); await Task.Delay(200);
            var game = new GameWindow(new WindowInteropHelper(target).Handle, Environment.ProcessId, "RecordingFixture", target.Title);
            Settings Make(bool grouped = true) => new() { CaptureMode = "Game", AudioMode = "System", MicrophoneId = "default", Discord = true, Height = 720,
                Fps = 30, Codec = "H264", BufferSeconds = 15, Quality = 35, MaxBitrateMbps = 2, MaxBufferMegabytes = 128,
                GroupByApplication = grouped, OutputDirectory = Path.Combine(directory, "output"), BufferDirectory = Path.Combine(directory, "buffer") };
            using var speaker = new WasapiOut(); speaker.Init(new ToneSource()); speaker.Play();
            var settings = Make(); settings.GameNames[game.Exe] = "Запись: приложение";
            mute = new(); active = new(settings, game, display, mute); await active.Start(); await Task.Delay(3100);
            var beforeStart = Native.Clock; await Task.WhenAll(active.StartRecording(), active.StartRecording(), active.StartRecording());
            var session = active.Recording.SessionDirectory!; var started = JsonDocument.Parse(File.ReadAllText(Path.Combine(session, "recording.json"))).RootElement.GetProperty("StartedAt").GetDouble();
            var id = Producer(active).Id; var tracks = Tracks(active); var memory = Process.GetCurrentProcess().PrivateMemorySize64;
            Check(active.Recording.State == RecordingState.Recording && Directory.GetDirectories(settings.BufferDirectory, ".recording-*").Length == 1, "Repeated simultaneous StartRecording owns exactly one independent session");
            Check(started >= beforeStart && started - beforeStart < .2, "Warm start T0 is command time without waiting for an earlier segment boundary");
            settings.Height = 1080; settings.Quality = 15;
            Check(JsonDocument.Parse(File.ReadAllText(Path.Combine(session, "recording.json"))).RootElement.GetProperty("Settings").GetProperty("Height").GetInt32() == 720, "Recording and producer snapshot settings; caller mutation cannot change encoding");
            await Task.Delay(3400); mute.SetMuted(true); await Task.Delay(1000); mute.SetMuted(false);
            Check(active.Microphone?.IsMuted == false && ReferenceEquals(active.Microphone, tracks.FirstOrDefault(t => t.Name == "Microphone")), "Both consumers share microphone track and runtime mute control");
            var saving = active.Save(); await Task.Delay(200);
            Check(await ((SemaphoreSlim)typeof(Recorder).GetField("files", Private)!.GetValue(active)!).WaitAsync(0), "Save Rewind releases metadata semaphore during segment wait/export");
            ((SemaphoreSlim)typeof(Recorder).GetField("files", Private)!.GetValue(active)!).Release();
            var rewind = await saving;
            var shot = await ScreenshotCapture.Take(new("Game", true, 30, game, display, settings.OutputDirectory, false, null));
            Check(active.Recording.State == RecordingState.Recording && Producer(active).Id == id && Tracks(active).SequenceEqual(tracks), "Save Rewind and PNG do not restart capture, NVENC or WASAPI while recording");
            var completedOwned = Directory.GetFiles(session, "video-*.mkv");
            await active.SetRewindEnabled(false); await Task.Delay(2300);
            Check(active.Running && Producer(active).Id == id && !active.HasReadableBufferData && active.AvailableSeconds == 0 && completedOwned.All(File.Exists), "Rewind OFF clears history but retained recording media and shared producer survive");
            Check(tracks.All(t => t.Snapshot(0, Native.Clock + 1).Sum(p => p.Data.Length) < 192000 * 3), "Rewind OFF audio RAM stays bounded to two-second offset pre-roll");
            await active.SetRewindEnabled(true); await Task.Delay(3200);
            for (int i = 0; i < 30 && !active.HasReadableBufferData; i++) await Task.Delay(100);
            Check(active.HasReadableBufferData && active.Recording.State == RecordingState.Recording && Producer(active).Id == id, "Enabling Rewind while recording accumulates new history without capture restart");
            active.RewindByteLimitForTest = 16000; await Task.Delay(5000); await active.Maintain();
            while (active.Recording.State == RecordingState.Recording && active.Recording.Duration < settings.BufferSeconds * 2 + 2) await Task.Delay(200);
            Check(Directory.GetFiles(session, "video-*.mkv").Length >= 8 && completedOwned.All(File.Exists), "Recording ownership survives independent Rewind disk-budget eviction");
            Check(active.Recording.Duration > settings.BufferSeconds * 2 && active.AvailableSeconds <= settings.BufferSeconds, "Recording substantially exceeds BufferSeconds while Rewind remains bounded");
            var growth = Process.GetCurrentProcess().PrivateMemorySize64 - memory;
            var disk = Directory.GetFiles(session, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);
            Check(Processes().IsSubsetOf(baseline.Append(id).ToHashSet()), "Steady simultaneous capture has one FFmpeg/NVENC and no duplicated capture/audio clients");
            var stop = Native.Clock; var results = await Task.WhenAll(active.StopRecording(), active.StopRecording()); var recording = results[0]!;
            Check(recording != null && results.All(r => r?.Path == recording.Path) && active.Recording.State == RecordingState.Idle && active.Running, "Repeated StopRecording publishes one file and keeps Rewind capture running");
            Check(Math.Abs(recording!.Duration - (stop - started)) < .12, "Reported T0/T1 interval matches QPC command span within scheduling tolerance");
            Check(!Directory.Exists(session) && recording.Path.StartsWith(Path.Combine(settings.OutputDirectory, "Запись_ приложение")), "Published recording releases owned media and reuses application grouping");
            foreach (var clip in new[] { recording, rewind })
            {
                var decode = await MediaTools.Run("ffmpeg", ["-v", "error", "-i", clip.Path, "-map", "0", "-f", "null", "-"], 120);
                Check(decode.Code == 0, "Real WGC video and all audio streams decode: " + Path.GetFileName(clip.Path));
            }
            var probe = await MediaTools.Run("ffprobe", ["-v", "error", "-show_streams", "-show_format", "-of", "json", recording.Path]);
            using var doc = JsonDocument.Parse(probe.Output); var streams = doc.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            var containerDuration = double.Parse(doc.RootElement.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            Check(Math.Abs(containerDuration - recording.Duration) < .12, "Decoded container duration stays within one frame plus AAC padding of requested duration");
            Check(streams.Count(s => s.GetProperty("codec_type").GetString() == "audio") == tracks.Length + 1, "Mix and individual selected system/microphone/available Discord tracks are preserved");
            Check(streams.Where(s => s.GetProperty("codec_type").GetString() == "audio").Any(s => s.GetProperty("tags").GetProperty("title").GetString() == "System"), "Actual WASAPI system source remains separately labelled");
            artifacts.Add(new { Recording = recording, Rewind = rewind, Screenshot = shot, Started = started, Stop = stop, ContainerDuration = containerDuration,
                SteadyCaptureProcesses = 1, SteadyEncoderProcesses = 1, FinalizationAdditionalEncoder = true, WasapiTracks = tracks.Select(t => t.Name), TemporaryLogicalBytes = disk,
                PrivateMemoryGrowthBytes = growth, PcmQueueLimitPerTrack = RecordingAudioWriter.QueueByteLimit, StartEpochAccuracy = "arrival-estimated; not hardware-exact" });
            await active.DisposeAsync(); active = null;
            // Cold recording-only source: actual DXGI; no Rewind retention and no additional producer.
            settings = Make(false); settings.CaptureMode = "Desktop"; settings.Discord = false; settings.MicrophoneId = "off";
            active = new(settings, null, display); await active.SetRewindEnabled(false); await active.Start(); await active.StartRecording(); await Task.Delay(3300);
            var dxgi = await active.StopRecording();
            Check(dxgi != null && !active.Running && active.Recording.State == RecordingState.Idle && !active.HasReadableBufferData, "Rewind OFF + recording uses DXGI and stops unused producer after finalization");
            Check(Path.GetDirectoryName(dxgi!.Path) == settings.OutputDirectory, "Grouping OFF records directly into configured output directory");
            Check((await MediaTools.Run("ffmpeg", ["-v", "error", "-i", dxgi.Path, "-f", "null", "-"])).Code == 0, "Real DXGI conventional recording decodes");
            artifacts.Add(new { DxgiRecording = dxgi }); await active.DisposeAsync(); active = null;
            // Low-space seam does not fill the disk; monitor exercises real stop and exporter.
            settings = Make(false); settings.Discord = false; active = new(settings, game, display); await active.Start(); await active.StartRecording(); await Task.Delay(2700);
            active.FreeSpace = _ => 1;
            for (int i = 0; i < 200 && active.Recording.State != RecordingState.Error; i++) await Task.Delay(100);
            Check(active.Recording.State == RecordingState.Error && active.Recording.Result != null && active.Recording.Error!.Contains("места") && active.Running, "Simulated low disk safely publishes partial and reports reason while Rewind survives");
            artifacts.Add(new { LowDisk = active.Recording }); await active.DisposeAsync(); active = null;
            // Producer loss: completed segments retained, no recovery gap spliced into the recording.
            active = new(settings, game, display); await active.Start(); await active.StartRecording();
            // Test loss AFTER the precondition under test: a confirmed, owned
            // segment. A fixed 2.9s delay can kill WGC before its first closure
            // under scheduling load, testing "no video yet" instead of recovery.
            for (var attempt = 0; attempt < 120 && Directory.GetFiles(active.Recording.SessionDirectory!, "video-*.mkv").Length == 0; attempt++)
                await Task.Delay(100);
            Check(Directory.GetFiles(active.Recording.SessionDirectory!, "video-*.mkv").Length > 0, "Capture-loss fixture has a confirmed owned segment before interruption");
            await active.SimulateAccessLossForTest();
            for (int i = 0; i < 200 && active.Recording.State != RecordingState.Error; i++) await Task.Delay(100);
            Check(active.Recording.State == RecordingState.Error && active.Recording.Result != null && active.Recording.Error!.Contains("Захват"), "Capture failure finalizes confirmed partial and preserves error reason");
            var partial = active.Recording.Result!; await active.RestartCapture(); await Task.Delay(2200);
            Check(active.Running && active.HasReadableBufferData && active.Recording.Result?.Path == partial.Path, "Independent Rewind recovery does not resume/splice the terminated conventional session");
            artifacts.Add(new { CaptureFailure = partial }); await active.DisposeAsync(); active = null;
            // Failed finalization retains raw media, then explicit recovery publishes it.
            var blocked = Path.Combine(directory, "blocked-output"); File.WriteAllText(blocked, "fixture");
            settings = Make(false); settings.OutputDirectory = blocked; settings.Discord = false; active = new(settings, game, display);
            await active.Start(); await active.StartRecording(); await Task.Delay(2600); await active.StopRecording();
            var unfinished = active.Recording.SessionDirectory!;
            Check(active.Recording.State == RecordingState.Error && Directory.GetFiles(unfinished, "video-*.mkv").Length > 0, "Publication failure retains completed segments/PCM/journal instead of discarding the session");
            await active.DisposeAsync(); active = null;
            var found = Recorder.FindIncompleteRecordings(settings.BufferDirectory);
            Check(found.Any(r => r.Directory == unfinished && r.ConfirmedEnd > r.StartedAt), "Next-launch detection finds incomplete conventional recording without destructive cleanup");
            var manifestPath = Path.Combine(unfinished, "recording.json");
            var manifest = JsonSerializer.Deserialize<RecordingManifest>(File.ReadAllText(manifestPath))!;
            manifest.Settings.OutputDirectory = Path.Combine(directory, "recovered"); File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));
            var recovered = await Recorder.RecoverRecording(unfinished);
            Check(File.Exists(recovered.Path) && !Directory.Exists(unfinished) && (await MediaTools.Run("ffmpeg", ["-v", "error", "-i", recovered.Path, "-map", "0", "-f", "null", "-"])).Code == 0, "Explicit incomplete-session recovery decodes and releases ownership only after publication");
            artifacts.Add(new { Recovered = recovered });
            // Stop idle, rapid zero-length stop and collision safety.
            active = new(Make(false), game, display); Check(await active.StopRecording() == null, "StopRecording while Idle is idempotent");
            await active.Start(); await active.StartRecording(); await active.StopRecording(); await active.StopRecording();
            Check(active.Recording.State is RecordingState.Idle or RecordingState.Error, "Rapid start/stop does not duplicate consumers or leak lifecycle tasks");
            await active.DisposeAsync(); active = null;
            var collision = Path.Combine(directory, "collision.partial"); File.WriteAllText(collision, "fixture"); var original = File.ReadAllBytes(recording.Path);
            bool rejected = false; try { await ClipFiles.PublishFile(collision, recording.Path); } catch (IOException) { rejected = true; }
            Check(rejected && original.SequenceEqual(File.ReadAllBytes(recording.Path)) && File.Exists(collision), "Publication collision never overwrites an existing recording"); File.Delete(collision);
            Check(bytes == null ? !File.Exists(config) : bytes.SequenceEqual(File.ReadAllBytes(config)), "Recording backend leaves existing settings.json byte-for-byte compatible");
            Check(Processes().IsSubsetOf(baseline), "All recording/finalizer FFmpeg processes are reaped");
            File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(new { Passed = true, checks, artifacts }, new JsonSerializerOptions { WriteIndented = true })); return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(new { Passed = false, checks, artifacts, Error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true })); return 1; }
        finally { animation.Stop(); target?.Close(); if (active != null) await active.DisposeAsync(); }
    }
}
