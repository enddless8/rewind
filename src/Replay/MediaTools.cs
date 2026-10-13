using System.Diagnostics;

namespace Replay;
internal static class MediaTools
{
    private static readonly AsyncLocal<CancellationToken> operationCancellation = new();
    internal static IDisposable EnterCancellation(CancellationToken token)
    {
        var old = operationCancellation.Value; operationCancellation.Value = token;
        return new CancellationScope(old);
    }
    private sealed class CancellationScope(CancellationToken old) : IDisposable
    { public void Dispose() => operationCancellation.Value = old; }
    public static string Find(string name)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", name + ".exe");
        if (File.Exists(bundled)) return bundled;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var tools = Path.Combine(dir.FullName, ".tools", "ffmpeg");
            if (Directory.Exists(tools)) { var match = Directory.GetFiles(tools, name + ".exe", SearchOption.AllDirectories).FirstOrDefault(); if (match != null) return match; }
        }
        throw new FileNotFoundException($"Не найден {name}.exe. Запусти scripts/build.ps1.");
    }
    public static Process Create(string tool, IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(Find(tool)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return new Process { StartInfo = info };
    }
    public static async Task<(int Code, string Output, string Error)> Run(string tool, IEnumerable<string> args, int timeout = 30,
        CancellationToken cancellationToken = default, bool bindLifetime = false)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, operationCancellation.Value);
        cancellationToken = linked.Token;
        cancellationToken.ThrowIfCancellationRequested();
        var timing = SaveTiming.Current;
        var arguments = args.ToList();
        var progress = timing != null && tool == "ffmpeg" && arguments.Any(a => a.EndsWith("_nvenc", StringComparison.Ordinal));
        if (progress) arguments.InsertRange(0, ["-progress", "pipe:1", "-stats_period", "0.1"]);
        using var process = Create(tool, arguments);
        timing?.Mark("process-start-requested", new { Tool = tool, Arguments = arguments });
        process.Start();
        timing?.Mark("process-started", new { Tool = tool, Pid = process.Id });
        if (tool == "ffmpeg" && arguments.Contains("-c:a") && arguments.Any(a => a.EndsWith("clip.mkv", StringComparison.OrdinalIgnoreCase)))
            timing?.Mark("ffmpeg-clip-export-started", new { Pid = process.Id });
        ProcessLifetime? lifetime = null;
        var output = progress ? ReadProgress(process, timing!) : process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try
        {
            if (bindLifetime) lifetime = new ProcessLifetime(process);
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(timeout));
            if (timing != null)
            {
                double? cpu = null;
                try { cpu = process.TotalProcessorTime.TotalMilliseconds; } catch { }
                timing.Mark("process-exited", new { Tool = tool, Pid = process.Id, Code = process.ExitCode, CpuMs = cpu });
            }
            return (process.ExitCode, await output, await error);
        }
        catch
        {
            // Release the job and reap the child before callers clean up its output files.
            lifetime?.Dispose(); lifetime = null;
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            await Task.WhenAll(output, error);
            throw;
        }
        finally { lifetime?.Dispose(); }
    }
    private static async Task<string> ReadProgress(Process process, SaveTiming timing)
    {
        var output = new System.Text.StringBuilder();
        var first = true;
        int lastFrame = 0;
        string? outTime = null, speed = null;
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            output.AppendLine(line);
            if (line.StartsWith("frame=") && int.TryParse(line[6..].Trim(), out var count)) lastFrame = count;
            if (line.StartsWith("out_time_us=")) outTime = line[12..];
            if (line.StartsWith("speed=")) speed = line[6..];
            if (first && line.StartsWith("frame=") && int.TryParse(line[6..].Trim(), out var frame) && frame > 0)
            { first = false; timing.Mark("ffmpeg-first-encoded-frame", new { Pid = process.Id, Frame = frame }); }
            if (line == "progress=end") timing.Mark("ffmpeg-final-progress", new { Pid = process.Id, Frames = lastFrame, OutTimeUs = outTime, Speed = speed });
        }
        return output.ToString();
    }
    public static async Task<string> SelectEncoder(string codec)
    {
        var choices = codec switch { "AV1" => new[] { "av1_nvenc" }, "HEVC" => ["hevc_nvenc"], "H264" => ["h264_nvenc"], _ => ["av1_nvenc", "hevc_nvenc", "h264_nvenc"] };
        string error = "";
        foreach (var encoder in choices)
        {
            var result = await Run("ffmpeg", ["-hide_banner", "-f", "lavfi", "-i", "color=s=256x144:r=30", "-frames:v", "1", "-c:v", encoder, "-f", "null", "-"]);
            if (result.Code == 0) return encoder;
            error = result.Error;
        }
        throw new InvalidOperationException("NVENC недоступен. Проверь драйвер NVIDIA и выбранный кодек.\n" + error[^Math.Min(error.Length, 1000)..]);
    }
}
