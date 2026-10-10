using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Replay;

internal static class ExistingExportBenchmark
{
    internal static async Task Run(string source, string destination)
    {
        source=Path.GetFullPath(source); destination=Path.GetFullPath(destination);
        Directory.CreateDirectory(destination);
        var args=JsonSerializer.Deserialize<List<string>>(File.ReadAllText(Path.Combine(source,"copy-args.json")))!;
        var staging=Path.GetDirectoryName(args[args.IndexOf("-i")+1])!;
        args=args.Select(a=>a.Replace(staging,source)).ToList();
        var duration=double.Parse(args[args.IndexOf("-t")+1],CultureInfo.InvariantCulture);
        var output=Path.Combine(destination,"unpublished.mkv"); args[^1]=output;
        var filter=args.IndexOf("-filter_complex");
        args[filter+1]=$"[0:v]trim=start=0:duration={duration.ToString("F6",CultureInfo.InvariantCulture)},setpts=PTS-STARTPTS[v];"+args[filter+1];
        args[args.IndexOf("0:v:0")]="[v]"; args[args.LastIndexOf("-c:v")+1]="av1_nvenc";
        args.InsertRange(args.IndexOf("-c:a"),["-preset","p4","-tune","hq","-rc","vbr","-cq","27","-b:v","0","-maxrate","15M","-bufsize","30M","-bf","0","-r","60"]);
        var lines=File.ReadAllLines(Path.Combine(source,"video.txt")); double clock=0;
        var ranges=new List<RewindExportSnapshot.Range>();
        for(var i=0;i<lines.Length;i+=2)
        {
            var end=clock+double.Parse(lines[i+1][9..],CultureInfo.InvariantCulture);
            ranges.Add(new(lines[i][6..^1],clock,end,clock,Math.Min(end,duration))); clock=end;
        }
        var snapshot=new RewindExportSnapshot(duration,0,double.NegativeInfinity,double.NegativeInfinity,60,ranges,Directory.GetFiles(source,"*.wav"));
        var events=new List<object>();
        void Observe(Guid id,string stage,double elapsed,object? details)=>events.Add(new {stage,elapsed,details});
        SaveTiming.Observed+=Observe;
        Environment.SetEnvironmentVariable("REWIND_SAVE_TIMINGS_FILE",Path.Combine(destination,"timings.jsonl"));
        var watch=Stopwatch.StartNew();
        using var timing=SaveTiming.Begin(Guid.NewGuid());
        try
        {
            var plan=await RewindPacketCopy.Inspect(snapshot);
            if(plan.Plan==null)throw new Exception("Actual Phase 2 plan rejected: "+plan.Reason);
            var result=await new RewindPacketCopy().Run(plan,new(),"av1_nvenc",ranges[0].Path,args,output,async()=> {
                var probe=await MediaTools.Run("ffprobe",["-v","error","-show_streams","-of","json",output]);
                if(probe.Code!=0 || !string.IsNullOrWhiteSpace(probe.Error))throw new Exception(probe.Error);
                var streams=JsonSerializer.Deserialize<JsonElement>(probe.Output).GetProperty("streams");
                if(streams.GetArrayLength()!=4)throw new Exception("Потеря audio tracks");
            });
            if(result.CopyFallback || result.Video.Decoder!="packet-copy-av1")throw new Exception("Expected actual Phase 2 copy");
            watch.Stop();
            File.WriteAllText(Path.Combine(destination,"results.json"),JsonSerializer.Serialize(new {passed=true,total_ms=watch.Elapsed.TotalMilliseconds,
                source,output,frames=plan.Plan.Packets.Count,result,events},new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine(watch.Elapsed.TotalMilliseconds.ToString("F3",CultureInfo.InvariantCulture));
        }
        finally { SaveTiming.Observed-=Observe; Environment.SetEnvironmentVariable("REWIND_SAVE_TIMINGS_FILE",null); }
    }
}
