using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Replay;

// Diagnose child-process plumbing separately from export/media semantics.
internal static class ProcessProfile
{
    internal static async Task Run(string root, bool periods = false)
    {
        using var source = JsonDocument.Parse(File.ReadAllText("artifacts/unified-stage2/finalizers/300-r2-stage1/results.json"));
        var path = source.RootElement.GetProperty("clip").GetProperty("Path").GetString()!;
        List<object> rows = [];
        foreach (var repeat in Enumerable.Range(0, 2))
        foreach (var variant in periods ? new[] { "normal", "period-1", "period-0.1" } : new[] { "normal", "closed-stdin", "nostdin", "no-job", "sync-readers" })
        {
            var args = new List<string> { "-v", "error", "-xerror", "-progress", "pipe:1", "-hwaccel", "cuda",
                "-hwaccel_output_format", "cuda", "-c:v", "av1_cuvid", "-i", path, "-map", "0", "-fps_mode:v", "passthrough", "-f", "null", "-" };
            if (variant == "nostdin") args.Insert(0, "-nostdin");
            if (variant.StartsWith("period-")) args.InsertRange(0, ["-stats_period", variant[7..]]);
            using var process = MediaTools.Create("ffmpeg", args);
            var watch = Stopwatch.StartNew(); process.Start();
            using var lifetime = variant == "no-job" ? null : new ProcessLifetime(process);
            if (variant == "closed-stdin") process.StandardInput.Close();
            var output = variant == "sync-readers" ? Task.Run(process.StandardOutput.ReadToEnd) : process.StandardOutput.ReadToEndAsync();
            var error = variant == "sync-readers" ? Task.Run(process.StandardError.ReadToEnd) : process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(120)); }
            catch { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); await Task.WhenAll(output, error); throw; }
            var elapsed = watch.Elapsed.TotalMilliseconds;
            var text = await output; var err = await error;
            if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(err) || !text.Contains("progress=end") ||
                !int.TryParse(text.Split('\n').LastOrDefault(l => l.StartsWith("frame="))?[6..].Trim(), out var count) || count != 8970)
                throw new Exception("Process plumbing decode failed: " + err);
            rows.Add(new { repeat, variant, elapsedMs = elapsed, physicalMs = (process.ExitTime - process.StartTime).TotalMilliseconds,
                cpuMs = process.TotalProcessorTime.TotalMilliseconds, count, stdoutBytes = text.Length, ProcessorCount = Environment.ProcessorCount });
            File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"{repeat} {variant}: {elapsed:F1} ms");
        }
    }
}
