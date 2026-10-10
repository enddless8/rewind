using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Replay;
public sealed record CompressionJob
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Source { get; init; } = "";
    public string Output { get; init; } = "";
    public CompressionProfile Profile { get; init; } = new();
    public string State { get; set; } = "Pending";
    public string? Error { get; set; }
    public double Percent { get; set; }
    public long OriginalBytes { get; set; }
    public long CompressedBytes { get; set; }
}
public sealed class CompressionQueue : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly List<CompressionJob> jobs;
    private readonly string stateFile;
    private readonly SemaphoreSlim wake = new(0);
    private readonly CancellationTokenSource cancel = new();
    private readonly Task worker;
    public event Action<CompressionJob>? Finished;
    public event Action<CompressionJob>? Started;
    public string Status
    {
        get { lock (gate) { var active = jobs.FirstOrDefault(x => x.State == "Running"); var pending = jobs.Count(x => x.State == "Pending"); var last = jobs.LastOrDefault(); return active != null ? $"Сжатие: {active.Percent:F0}% · {Path.GetFileName(active.Source)} · в очереди: {pending}" : pending > 0 ? $"Сжатие: в очереди {pending}" : last?.State == "Completed" ? $"Готово: {Path.GetFileName(last.Output)}" : last?.State == "Failed" ? $"Сжатие не удалось: {last.Error}. Оригинал сохранён." : "Очередь сжатия пуста"; } }
    }
    public IReadOnlyList<CompressionJob> Snapshot() { lock (gate) return jobs.Select(j => j with { }).ToList(); }
    public CompressionQueue(string? file = null)
    {
        stateFile = file ?? Path.Combine(Settings.DataDirectory, "compression-queue.json");
        if (File.Exists(stateFile))
        {
            try { jobs = JsonSerializer.Deserialize<List<CompressionJob>>(File.ReadAllText(stateFile)) ?? []; }
            catch (Exception ex) { ClipFiles.Log("compression-queue-load", ex, stateFile); throw new IOException("Не удалось прочитать очередь сжатия. Исходные клипы сохранены.", ex); }
        }
        else jobs = [];
        foreach (var job in jobs.Where(j => j.State == "Running")) { job.State = "Pending"; job.Percent = 0; }
        worker = Task.Run(Work);
        wake.Release();
    }
    public string Enqueue(string source, CompressionProfile profile)
    {
        profile.Validate();
        var job = new CompressionJob { Source = Path.GetFullPath(source), Profile = profile with { }, Output = Path.Combine(Path.GetDirectoryName(source)!, Path.GetFileNameWithoutExtension(source) + ".compressed.mp4") };
        lock (gate)
        {
            if (cancel.IsCancellationRequested) throw new InvalidOperationException("Очередь уже остановлена.");
            if (File.Exists(job.Output) || jobs.Any(j => j.Output == job.Output)) job = job with { Output = Path.Combine(Path.GetDirectoryName(source)!, Path.GetFileNameWithoutExtension(source) + ".compressed-" + job.Id[..8] + ".mp4") };
            jobs.Add(job);
            try { Persist(); } catch { jobs.Remove(job); throw; }
        }
        wake.Release(); return job.Id;
    }
    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
        var history = jobs.Where(j => j.State is "Completed" or "Failed").TakeLast(50).ToHashSet();
        var saved = jobs.Where(j => j.State is "Pending" or "Running" || history.Contains(j));
        File.WriteAllText(stateFile + ".tmp", JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(stateFile + ".tmp", stateFile, true);
    }
    private void SaveStateSafely() { try { lock (gate) Persist(); } catch (Exception ex) { ClipFiles.Log("compression-queue-save", ex, stateFile); } }
    private async Task Work()
    {
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                CompressionJob? job;
                lock (gate) { job = jobs.FirstOrDefault(j => j.State == "Pending"); if (job != null) job.State = "Running"; }
                if (job == null) { await wake.WaitAsync(cancel.Token); continue; }
                SaveStateSafely();
                var timing = SaveTiming.ForMedia(job.Source);
                using var timingScope = timing?.Enter();
                timing?.Mark("compression-started", new { job.Id, job.Source });
                try { Started?.Invoke(job with { }); }
                catch (Exception ex) { ClipFiles.Log("compression-notification", ex, job.Source); }
                try { await Compress(job); lock (gate) { job.State = "Completed"; job.Percent = 100; } }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested) { lock (gate) { job.State = "Pending"; job.Percent = 0; } }
                catch (Exception ex) { lock (gate) { job.State = "Failed"; job.Error = ex.Message; } ClipFiles.Log("compression", ex, job.Source); }
                SaveStateSafely();
                timing?.Mark("compression-completed", new { job.State, job.Error, job.OriginalBytes, job.CompressedBytes });
                if (job.State != "Pending") try { Finished?.Invoke(job with { }); } catch (Exception ex) { ClipFiles.Log("compression-notification", ex); }
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
    }
    private static async Task<(double Duration, int Audio, string[] Names)> Probe(string path)
    {
        var probe = await MediaTools.Run("ffprobe", ["-v", "error", "-show_entries", "format=duration:stream=codec_type:stream_tags=title,handler_name", "-of", "json", path]);
        if (probe.Code != 0) throw new IOException("Не удалось проверить клип: " + probe.Error);
        using var json = JsonDocument.Parse(probe.Output);
        var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        if (!streams.Any(s => s.GetProperty("codec_type").GetString() == "video")) throw new IOException("В файле нет видеодорожки");
        var duration = double.Parse(json.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        if (duration <= 0) throw new IOException("Некорректная длительность видео");
        var audio = streams.Where(s => s.GetProperty("codec_type").GetString() == "audio").ToArray();
        return (duration, audio.Length, audio.Select((s, i) => s.TryGetProperty("tags", out var tags) && tags.TryGetProperty("title", out var title) ? title.GetString()! : $"Audio {i + 1}").ToArray());
    }
    private async Task Compress(CompressionJob job)
    {
        cancel.Token.ThrowIfCancellationRequested();
        job.Profile.Validate();
        var input = await Probe(job.Source);
        cancel.Token.ThrowIfCancellationRequested();
        job.OriginalBytes = new FileInfo(job.Source).Length;
        // A completed atomic publication can survive a crash just before persisting Completed.
        if (File.Exists(job.Output))
        {
            var existing = await Probe(job.Output);
            if (existing.Audio != input.Audio || Math.Abs(existing.Duration - input.Duration) > .25) throw new IOException("Выходной файл уже существует и не прошёл проверку; он не перезаписан.");
            job.CompressedBytes = new FileInfo(job.Output).Length; return;
        }
        var temporary = Path.Combine(Path.GetDirectoryName(job.Output)!, ".compressing-" + job.Id + ".mp4");
        try
        {
            var arguments = job.Profile.Arguments(job.Source, temporary);
            for (var i = 0; i < input.Names.Length; i++) arguments.InsertRange(arguments.Count - 1, [$"-metadata:s:a:{i}", $"handler_name={input.Names[i]}", $"-disposition:a:{i}", i == 0 ? "default" : "0"]);
            using var process = MediaTools.Create("ffmpeg", arguments);
            SaveTiming.Current?.Mark("compression-process-start-requested", new { Arguments = arguments });
            process.Start(); using var lifetime = new ProcessLifetime(process);
            SaveTiming.Current?.Mark("compression-process-started", new { Pid = process.Id });
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            var errorTask = process.StandardError.ReadToEndAsync();
            var progress = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                    if (line.StartsWith("out_time_us=") && long.TryParse(line[12..], out var us))
                        lock (gate) job.Percent = Math.Clamp(us / 1000000.0 / input.Duration * 100, 0, 99);
            });
            try { await process.WaitForExitAsync(cancel.Token); }
            catch { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); await progress; await errorTask; throw; }
            await progress; var error = await errorTask;
            SaveTiming.Current?.Mark("compression-process-exited", new { Pid = process.Id, Code = process.ExitCode });
            if (process.ExitCode != 0) throw new IOException("FFmpeg: " + error[^Math.Min(error.Length, 1200)..]);
            cancel.Token.ThrowIfCancellationRequested();
            var output = await Probe(temporary);
            if (input.Audio != output.Audio || Math.Abs(input.Duration - output.Duration) > .25) throw new IOException("Проверка MP4 не пройдена: отличаются длительность или аудиодорожки.");
            await ClipFiles.RetrySharing(() => File.Move(temporary, job.Output));
            job.CompressedBytes = new FileInfo(job.Output).Length;
        }
        finally { try { File.Delete(temporary); } catch { } }
    }
    public async ValueTask DisposeAsync()
    {
        cancel.Cancel(); await worker; SaveStateSafely(); wake.Dispose(); cancel.Dispose();
    }
}
