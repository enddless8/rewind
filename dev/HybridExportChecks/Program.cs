using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Replay;

internal static class Program
{
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var root = Path.GetFullPath(args[1]);
            if (!root.StartsWith(Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("Artifacts only.");
            Directory.CreateDirectory(root); ClipFiles.DiagnosticDirectoryForTests = Path.Combine(root, "diagnostics");
            using var guard = new PrivateStateGuard();
            if (args[0] is "live" or "native-failures")
            {
                var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                app.Resources["RewindticaFont"] = new System.Windows.Media.FontFamily("pack://application:,,,/Rewind;component/assets/fonts/#Rewindtica");
                app.Resources["InterFont"] = new System.Windows.Media.FontFamily("pack://application:,,,/Rewind;component/assets/fonts/#Inter 28pt 28pt");
                app.Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        if (args[0] == "native-failures") await NativeFailures.Run(root);
                        else await LiveSave.Run(root, args[2], int.Parse(args[3], CultureInfo.InvariantCulture), args.Length > 4 ? args[4] : "matrix");
                    }
                    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
                    finally { app.Shutdown(); }
                }); app.Run(); return Environment.ExitCode;
            }
            Export(root, args.Length > 2 ? args[2] : "hybrid", args[0] == "plan").GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    internal static RewindExportSnapshot Snapshot(JsonElement s) => new(s.GetProperty("accepted").GetDouble(), s.GetProperty("history_start").GetDouble(),
        s.GetProperty("previous_cutoff").ValueKind == JsonValueKind.Null ? double.NegativeInfinity : s.GetProperty("previous_cutoff").GetDouble(),
        s.GetProperty("epoch").ValueKind == JsonValueKind.Null ? double.NegativeInfinity : s.GetProperty("epoch").GetDouble(), s.GetProperty("fps").GetInt32(),
        s.GetProperty("intervals").EnumerateArray().Select(r => new RewindExportSnapshot.Range(r.GetProperty("path").GetString()!, r.GetProperty("source_start").GetDouble(),
            r.GetProperty("source_end").GetDouble(), r.GetProperty("start").GetDouble(), r.GetProperty("end").GetDouble())),
        s.GetProperty("audio").EnumerateArray().Select(a => a.GetString()!));
    internal static async Task Export(string root, string mode, bool inspect)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "job.json")));
        var snapshot = Snapshot(document.RootElement.GetProperty("snapshot"));
        var args = document.RootElement.GetProperty("arguments").EnumerateArray().Select(a => a.GetString()!).ToList();
        args[^1] = Path.Combine(root, "unpublished.mkv");
        var plan = RewindHybridExport.Inspect(snapshot);
        if (inspect)
        {
            File.WriteAllText(Path.Combine(root, "plan.json"), JsonSerializer.Serialize(new { Eligible = plan.Plan != null, plan.Reason, plan.Plan?.Frames,
                Pieces = plan.Plan?.Pieces.Select(p => new { p.Path, p.First, p.Stop }) }, Json)); return;
        }
        var stages = new List<object>(); var commands = new List<object>(); bool injected = false; FileStream? locked = null;
        void Observe(Guid id, string stage, double elapsed, object? details) { lock (stages) stages.Add(new { id, stage, elapsed, details }); }
        SaveTiming.Observed += Observe;
        Environment.SetEnvironmentVariable("REWIND_SAVE_TIMINGS_FILE", Path.Combine(root, "timing.jsonl"));
        using var timing = SaveTiming.Begin(Guid.NewGuid()); var watch = Stopwatch.StartNew();
        async Task<(int Code, string Output, string Error)> Run(string tool, IEnumerable<string> input, int timeout)
        {
            var a = input.ToList(); commands.Add(new { tool, args = a });
            var head = a[^1].EndsWith("head.mkv"); var mux = a.Any(v => v.StartsWith("setts="));
            if (mode == "missing-scratch" && a.Contains("trace_headers"))
            {
                foreach (var directory in Directory.GetDirectories(root, ".hybrid-*"))
                {
                    if (!Path.GetFullPath(directory).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("Scratch outside test root.");
                    Directory.Delete(directory, true);
                }
                return (1, "", "Injected missing attempt directory");
            }
            if (mode == "validation-software" && a.Contains("av1_cuvid") && !head) return (1, "", "Injected GPU validator failure");
            if (mode == "software" && a.Contains("av1_cuvid")) return (1, "", "Injected GPU decoder failure");
            if (!injected && (head && mode is "head-fail" or "decoder-fail" or "cancel" or "interrupted" || mux && mode is "mux-fail" or "no-space" or "locked"))
            {
                injected = true;
                if (mode == "cancel") throw new OperationCanceledException();
                if (mode == "interrupted")
                    return await MediaTools.Run("ffmpeg", ["-v", "error", "-re", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30", "-c:v", "av1_nvenc", "-t", "60", "-y", a[^1]], 1, bindLifetime: true);
                if (mode == "locked") locked = new FileStream(args[^1], FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                else if (mode == "decoder-fail") a[a.IndexOf("av1_cuvid")] = "h264_cuvid";
                else if (mode == "head-fail") a[a.LastIndexOf("-c:v") + 1] = "missing_boundary_encoder";
                else return (1, "", "Injected " + mode);
            }
            var result = await MediaTools.Run(tool, a, timeout, bindLifetime: true);
            if (mode == "retain-audio" && a[^1].EndsWith("audio-reference.mka"))
            {
                File.Copy(a[^1], Path.Combine(root, "audio-reference.mka"));
                File.Copy(args[^1], Path.Combine(root, "candidate.mkv"));
            }
            if (head && mode == "head-corrupt" || mux && mode == "mux-corrupt")
                using (var f = new FileStream(a[^1], FileMode.Open, FileAccess.Write)) f.SetLength(f.Length / 2);
            if (head && mode is "mutate-video" or "mutate-pcm")
            {
                var path = mode == "mutate-video" ? snapshot.Ranges[0].Path : snapshot.AudioFiles[0];
                File.Move(path, path + ".original"); File.Copy(path + ".original", path);
            }
            return result;
        }
        try
        {
            var result = await new RewindHybridExport(mode != "default" && mode != "nvdec", Run).Run(snapshot, new(Run),
                new(supportOverride: _ => Task.FromResult(true), run: Run), "av1_nvenc", args, args[^1], () => Verify(args[^1], snapshot.AudioFiles.Count));
            File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = true, totalMs = watch.Elapsed.TotalMilliseconds, result, stages, commands,
                PlannedFrames = plan.Plan?.Frames, Pieces = plan.Plan?.Pieces.Select(p => new { p.Path, p.First, p.Stop }), Output = args[^1] }, Json));
        }
        catch (Exception ex) when (mode is "cancel" or "locked" or "mutate-video" or "mutate-pcm")
        {
            if (mode == "cancel" && ex is not OperationCanceledException) throw;
            File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = true, failure = ex.Message, cancelled = ex is OperationCanceledException,
                published = false, stages, commands, sourceRetained = snapshot.Ranges.All(r => File.Exists(r.Path)) }, Json));
        }
        finally { locked?.Dispose(); SaveTiming.Observed -= Observe; Environment.SetEnvironmentVariable("REWIND_SAVE_TIMINGS_FILE", null); }
    }
    internal static async Task Verify(string output, int tracks)
    {
        var probe = await MediaTools.Run("ffprobe", ["-v", "error", "-show_streams", "-of", "json", output], 30, bindLifetime: true);
        using var doc = JsonDocument.Parse(probe.Output);
        if (probe.Code != 0 || !string.IsNullOrWhiteSpace(probe.Error) || doc.RootElement.GetProperty("streams").GetArrayLength() != 1 + tracks + (tracks > 0 ? 1 : 0)) throw new IOException("Invalid streams.");
    }
}
internal sealed class PrivateStateGuard : IDisposable
{
    // Resolve the actual profile explicitly, even when a sandbox virtualizes
    // LOCALAPPDATA. Never load Settings/CompressionQueue from this directory.
    private readonly string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "Replay");
    private readonly Dictionary<string, string> before;
    internal PrivateStateGuard() => before = Read();
    private Dictionary<string, string> Read() => Directory.Exists(directory) ? Directory.GetFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(p => p,
        p => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)))) : [];
    public void Dispose()
    {
        var after = Read();
        if (before.Count != after.Count || before.Any(p => after.GetValueOrDefault(p.Key) != p.Value)) throw new Exception("Private runtime files changed.");
    }
}
