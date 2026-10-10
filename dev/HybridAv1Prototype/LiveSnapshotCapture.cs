using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Windows;
using Replay;

internal static class LiveSnapshotCapture
{
    // Test-owned Recorder instances and its existing command seam. The production
    // strategy still exports both Save calls; hybrid is never injected or activated.
    internal static async Task Run(string root,string? python=null)
    {
        Directory.CreateDirectory(root);
        var config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "Replay");
        var protectedFiles = new[] { "settings.json", "compression-queue.json" }.Select(n => Path.Combine(config,n))
            .Where(File.Exists).ToDictionary(p => p, File.ReadAllBytes);
        var settings = new Settings { CaptureMode = "Desktop", HideDisplayBorder = true, AudioMode = "System", MicrophoneId = "off",
            Discord = false, Codec = "AV1", Height = 720, Fps = 30, BufferSeconds = 30, Quality = 27, MaxBitrateMbps = 15,
            GroupByApplication = false, OutputDirectory = Path.Combine(root,"output"), BufferDirectory = Path.Combine(root,"buffer") };
        var counter = new System.Windows.Controls.TextBlock { FontSize = 42, Foreground = System.Windows.Media.Brushes.White };
        var scene = new Window { Title = "Проверка AV1 · отдельный прототип", Width = 720, Height = 400, Content = counter,
            Background = System.Windows.Media.Brushes.DarkSlateBlue, Topmost = true, ShowActivated = false };
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) }; var frame = 0;
        timer.Tick += (_, _) => counter.Text = $"Кадр {++frame:D6}";
        var collected = new List<(string Root, List<string> Args)>();
        try
        {
            scene.Show(); timer.Start();
            await using var recorder = new Recorder(settings, null, Native.Displays().First(d => d.Primary));
            async Task<(int Code, string Output, string Error)> Retain(string tool, IEnumerable<string> input, int timeout)
            {
                var args = input.ToList();
                if (args.Contains("concat") && args.Contains("-c:a"))
                {
                    var staging = Path.GetDirectoryName(args[^1])!;
                    if (!collected.Any(c => c.Args[^1] == args[^1]))
                    {
                        var target = Path.Combine(root, "snapshot-"+collected.Count);
                        RewindPacketCopyTests.Retain(staging,target);
                        File.WriteAllText(Path.Combine(target,"accepted-arguments.json"),JsonSerializer.Serialize(args.Select(a => a.Replace(staging,target))));
                        collected.Add((target,args));
                    }
                }
                return await MediaTools.Run(tool,args,timeout,bindLifetime:true);
            }
            recorder.PacketCopy = new(Retain); recorder.VideoExport = new(run:Retain);
            await recorder.Start();
            var acceptedEpoch = (double)typeof(Recorder).GetField("rewindSince",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(recorder)!;
            await recorder.StartRecording(); await Task.Delay(31500);
            var first = await recorder.Save();
            var firstMetadata = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.ChangeExtension(first.Path,".json")));
            var cutoff = firstMetadata.GetProperty("AcceptedSaveBoundary").GetDouble();
            var concurrentHybridVerified=false;
            if(python!=null)
            {
                WriteSnapshot(collected[0],0,firstMetadata,cutoff,acceptedEpoch,settings.Fps);
                var info=new ProcessStartInfo(python) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                var script=Path.GetFullPath("dev/HybridAv1Prototype/concurrent_export.py");
                info.ArgumentList.Add(script);info.ArgumentList.Add(Path.Combine(collected[0].Root,"snapshot.json"));info.ArgumentList.Add(Path.Combine(root,"concurrent-hybrid"));
                using var child=new Process{StartInfo=info};child.Start();
                var stdout=child.StandardOutput.ReadToEndAsync();var stderr=child.StandardError.ReadToEndAsync();
                try {await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(180));}
                catch {if(!child.HasExited)child.Kill(true);await child.WaitForExitAsync();throw;}
                if(child.ExitCode!=0)throw new Exception(await stderr);
                File.WriteAllText(Path.Combine(root,"concurrent-child-output.json"),await stdout);
                concurrentHybridVerified=recorder.Running && recorder.Recording.State==RecordingState.Recording;
                if(!concurrentHybridVerified)throw new Exception("Hybrid прервал capture/recording");
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, cutoff+15-Native.Clock)));
            var second = await recorder.Save();
            var secondMetadata = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.ChangeExtension(second.Path,".json")));
            if (secondMetadata.GetProperty("ActualStart").GetDouble()<cutoff || Math.Abs(second.Duration-15)>.15)
                throw new Exception("Нарушена previous Save граница");
            if (!recorder.Running || recorder.Recording.State != RecordingState.Recording) throw new Exception("Capture/recording остановлены");
            var producerAlive = recorder.Running;
            var recording = await recorder.StopRecording();
            foreach (var path in new[] { first.Path, second.Path, recording!.Path })
            {
                var decoded = await MediaTools.Run("ffmpeg", RewindVideoExport.Arguments(["-v","error","-xerror","-i",path,"-map","0","-f","null","-"],true),180);
                if (decoded.Code!=0 || !string.IsNullOrWhiteSpace(decoded.Error)) throw new IOException(decoded.Error);
            }
            foreach (var (item,i) in collected.Select((x,i)=>(x,i)))
                WriteSnapshot(item,i,i==0 ? firstMetadata : secondMetadata,cutoff,acceptedEpoch,settings.Fps);
            foreach(var pair in protectedFiles) if(!File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)) throw new Exception("Изменены пользовательские настройки/очередь");
            File.WriteAllText(Path.Combine(root,"results.json"),JsonSerializer.Serialize(new {
                passed=true,First=first,Second=second,Recording=recording,FirstMetadata=firstMetadata,SecondMetadata=secondMetadata,
                ProducerAlive=producerAlive,ConcurrentRecording=true,ConcurrentHybridVerified=concurrentHybridVerified,
                CaptureSyncDropped=recorder.CaptureDroppedFrames,CaptureSyncDuplicated=recorder.CaptureDuplicatedFrames,
                ConfigUnchanged=true,AcceptedEpoch=acceptedEpoch,Snapshots=collected.Count },new JsonSerializerOptions{WriteIndented=true,NumberHandling=JsonNumberHandling.AllowNamedFloatingPointLiterals}));
            Console.WriteLine("Реальный capture: 30 с / затем 15 с, concurrent recording, два immutable snapshot сохранены.");
        }
        finally { timer.Stop(); scene.Close(); }
    }
    private static void WriteSnapshot((string Root,List<string> Args) item,int index,JsonElement metadata,double cutoff,double epoch,int fps)
    {
        var concat=File.ReadAllLines(Path.Combine(item.Root,"video.txt"));
        var filter=item.Args[item.Args.IndexOf("-filter_complex")+1];
        var trim=Regex.Match(filter,@"trim=start=([0-9.]+):duration=([0-9.]+)");
        var offset=trim.Success ? double.Parse(trim.Groups[1].Value,CultureInfo.InvariantCulture) : 0;
        var clock=metadata.GetProperty("ActualStart").GetDouble()-offset;var end=metadata.GetProperty("ActualEnd").GetDouble();
        var ranges=new List<object>();
        var acceptedArgs=JsonSerializer.Deserialize<List<string>>(File.ReadAllText(Path.Combine(item.Root,"accepted-arguments.json")))!;
        var audio=acceptedArgs.Select((a,i)=>(a,i)).Where(p=>p.a=="-i" && p.i+1<acceptedArgs.Count && acceptedArgs[p.i+1].EndsWith(".wav",StringComparison.OrdinalIgnoreCase))
            .Select(p=>acceptedArgs[p.i+1]).ToArray();
        var mixInputs=Regex.Matches(filter,@"\[(\d+):a\]").Select(m=>int.Parse(m.Groups[1].Value,CultureInfo.InvariantCulture)-1).ToArray();
        for(var n=0;n<concat.Length;n+=2)
        {
            var length=double.Parse(concat[n+1][9..],CultureInfo.InvariantCulture);
            ranges.Add(new {path=concat[n][6..^1],source_start=clock,source_end=clock+length,
                start=Math.Max(clock,metadata.GetProperty("ActualStart").GetDouble()),end=Math.Min(clock+length,end),epoch});clock+=length;
        }
        File.WriteAllText(Path.Combine(item.Root,"snapshot.json"),JsonSerializer.Serialize(new {
            accepted=metadata.GetProperty("AcceptedSaveBoundary").GetDouble(),history_start=metadata.GetProperty("ActualStart").GetDouble(),
            previous_cutoff=index==0 ? (double?)null : cutoff,epoch,fps,intervals=ranges,audio,mix_inputs=mixInputs},
            new JsonSerializerOptions{WriteIndented=true,NumberHandling=JsonNumberHandling.AllowNamedFloatingPointLiterals}));
    }
}
