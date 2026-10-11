using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Replay;

// Measurement only. A single nvidia-smi sampler stays alive; no tool process
// is launched per sample. GPU numbers are device totals, not exporter counters.
internal sealed class Resources : IAsyncDisposable
{
    private readonly List<object> samples = [];
    private readonly Dictionary<int, string> processes = [];
    private readonly CancellationTokenSource stop = new();
    private readonly Task polling;
    private readonly Process? gpu;
    private readonly Task? gpuReader;
    private readonly string root;
    private readonly Func<string?> staging;
    private readonly Stopwatch time = Stopwatch.StartNew();
    internal double ElapsedMs => time.Elapsed.TotalMilliseconds;
    internal Resources(string root, Func<string?> staging)
    {
        this.root = root; this.staging = staging;
        processes[Environment.ProcessId] = "harness";
        SaveTiming.Observed += Observe;
        try
        {
            gpu = new Process { StartInfo = new("nvidia-smi") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
            foreach (var arg in new[] { "--query-gpu=utilization.gpu,utilization.encoder,utilization.decoder,memory.used,encoder.stats.sessionCount", "--format=csv,noheader,nounits", "-lms", "500" }) gpu.StartInfo.ArgumentList.Add(arg);
            gpu.Start(); gpuReader = ReadGpu();
        }
        catch (Exception ex) { lock (samples) samples.Add(new { stage = "gpu-unavailable", ex.Message }); }
        polling = Poll();
    }
    internal void AddCapture(int pid) { lock (processes) processes[pid] = "capture"; }
    private void Observe(Guid operation, string stage, double elapsed, object? details)
    {
        if (stage != "process-started" || details == null) return;
        var value = JsonSerializer.SerializeToElement(details);
        lock (processes) processes[value.GetProperty("Pid").GetInt32()] = value.GetProperty("Tool").GetString()!;
    }
    private async Task ReadGpu()
    {
        while (await gpu!.StandardOutput.ReadLineAsync() is { } line)
            lock (samples) samples.Add(new { ms = time.Elapsed.TotalMilliseconds, stage = "gpu", values = line });
    }
    private async Task Poll()
    {
        while (!stop.IsCancellationRequested)
        {
            KeyValuePair<int, string>[] current; lock (processes) current = processes.ToArray();
            foreach (var item in current)
                try
                {
                    using var process = Process.GetProcessById(item.Key);
                    lock (samples) samples.Add(new { ms = time.Elapsed.TotalMilliseconds, stage = "memory", pid = item.Key, role = item.Value,
                        workingSet = process.WorkingSet64, peakWorkingSet = process.PeakWorkingSet64, privateBytes = process.PrivateMemorySize64 });
                }
                catch (ArgumentException) { lock (processes) processes.Remove(item.Key); }
                catch (InvalidOperationException) { }
            try
            {
                if (staging() is { } directory && Directory.Exists(directory))
                {
                    var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Where(p => !Path.GetFileName(p).StartsWith("owned-"));
                    var bytes = files.Sum(p => new FileInfo(p).Length);
                    lock (samples) samples.Add(new { ms = time.Elapsed.TotalMilliseconds, stage = "temporary-disk", bytes });
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            try { await Task.Delay(100, stop.Token); } catch (OperationCanceledException) { }
        }
    }
    public async ValueTask DisposeAsync()
    {
        SaveTiming.Observed -= Observe; stop.Cancel(); await polling;
        if (gpu != null)
        {
            try { if (!gpu.HasExited) gpu.Kill(true); await gpu.WaitForExitAsync(); if (gpuReader != null) await gpuReader; } catch (InvalidOperationException) { }
            gpu.Dispose();
        }
        lock (samples) File.WriteAllText(Path.Combine(root, "resources.json"), JsonSerializer.Serialize(samples, Program.Json));
        stop.Dispose();
    }
}
