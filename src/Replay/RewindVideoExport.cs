using System.Text.Json;

namespace Replay;

// Only the decoder of Rewind's first (video) input changes. The caller owns the
// immutable segment/PCM snapshot, trim, timestamps, encoder and publication.
internal sealed class RewindVideoExport
{
    internal delegate Task<(int Code, string Output, string Error)> Command(string tool, IEnumerable<string> args, int timeout);
    internal record Result(string Decoder, bool SoftwareFallback);
    internal record VideoFormat(string Codec, string Profile, int Width, int Height, string PixelFormat);
    private record CapabilityKey(string Ffmpeg, VideoFormat Format);
    private static readonly object cacheGate = new();
    private static readonly Dictionary<CapabilityKey, Lazy<Task<bool>>> capabilities = new();
    private readonly bool hardwareEnabled;
    private readonly Func<string, Task<bool>>? supportOverride;
    private readonly Command run;
    private Task<bool>? support;
    private readonly object supportGate = new();
    private bool hardwareFailed;

    // Overrides are instance-scoped test seams, never persisted settings/global flags.
    internal RewindVideoExport(bool hardwareEnabled = true, Func<string, Task<bool>>? supportOverride = null, Command? run = null)
    {
        this.hardwareEnabled = hardwareEnabled;
        this.supportOverride = supportOverride;
        this.run = run ?? ((tool, args, timeout) => MediaTools.Run(tool, args, timeout, bindLifetime: true));
    }

    internal async Task<Result> Run(string encoder, string closedSample, IReadOnlyList<string> softwareArgs,
        string temporaryOutput, Func<Task> verify)
    {
        bool hardware;
        try
        {
            hardware = encoder == "av1_nvenc" && hardwareEnabled && !hardwareFailed &&
                await Support(closedSample);
        }
        catch (OperationCanceledException)
        {
            // A cancelled request must not poison the next FIFO request's capability check.
            // Support clears the specific cancelled task under its gate.
            throw;
        }
        if (hardware)
        {
            try
            {
                await Attempt(Arguments(softwareArgs, true), "nvdec-av1", verify);
                return new("nvdec-av1", false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Run has reaped its child before returning/throwing. Never publish
                // a partial hardware result or retry Save()/take another snapshot.
                hardwareFailed = true; // avoid repeating a runtime failure in this capture session
                SaveTiming.Current?.Mark("hardware-decode-fallback", new { Reason = ex.Message });
                ClipFiles.Log("rewind-nvdec-fallback", ex, temporaryOutput);
                await ClipFiles.RetrySharing(() => File.Delete(temporaryOutput));
            }
        }
        await Attempt(Arguments(softwareArgs, false), "software-default", verify);
        return new("software-default", hardware);
    }

    // The exact same guarded capability check may overlap an idle job's tail
    // closure. It never starts an encoder or takes a new media snapshot.
    internal async Task Preflight(string encoder, string closedSample)
    {
        if (encoder == "av1_nvenc" && hardwareEnabled && !hardwareFailed)
            await Support(closedSample);
    }
    private async Task<bool> Support(string sample)
    {
        Task<bool> attempt;
        lock (supportGate) attempt = support ??= CheckSupport(sample);
        try { return await attempt; }
        catch (OperationCanceledException)
        {
            lock (supportGate) if (support == attempt) support = null;
            throw;
        }
    }

    private async Task Attempt(IReadOnlyList<string> args, string decoder, Func<Task> verify)
    {
        SaveTiming.Current?.Mark("video-export-attempt-started", new { Decoder = decoder });
        var result = await run("ffmpeg", args, 180);
        SaveTiming.Current?.Mark("video-aac-mux-finalization-completed", new { Decoder = decoder, result.Code });
        // With -v error, any stderr is a decode/encode/mux error even if FFmpeg
        // happened to exit zero. -xerror also makes corrupt input fail promptly.
        if (result.Code != 0 || !string.IsNullOrWhiteSpace(result.Error))
            throw new IOException("Не удалось собрать клип: " + result.Error[^Math.Min(result.Error.Length, 4000)..]);
        await verify();
        SaveTiming.Current?.Mark("video-export-attempt-validated", new { Decoder = decoder });
    }

    internal static IReadOnlyList<string> Arguments(IReadOnlyList<string> software, bool hardware)
    {
        var args = software.ToList();
        args.Insert(0, "-xerror");
        if (hardware)
        {
            var input = args.IndexOf("-i");
            if (input < 0) throw new ArgumentException("Не указан видеоисточник экспорта.");
            // Input-scoped flags belong before video -i, never before the WAV inputs.
            args.InsertRange(input, ["-hwaccel", "cuda", "-hwaccel_output_format", "cuda", "-c:v", "av1_cuvid"]);
        }
        return args;
    }

    private async Task<bool> CheckSupport(string sample)
    {
        SaveTiming.Current?.Mark("nvdec-capability-started");
        try
        {
            var supported = supportOverride != null ? await supportOverride(sample) : await ProbeSupport(sample);
            SaveTiming.Current?.Mark("nvdec-capability-completed", new { Supported = supported });
            return supported;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Optional acceleration must not make a usable software exporter fail.
            SaveTiming.Current?.Mark("nvdec-capability-completed", new { Supported = false, Reason = ex.Message });
            return false;
        }
    }

    private static async Task<bool> ProbeSupport(string sample)
    {
        var probe = await MediaTools.Run("ffprobe", ["-v", "error", "-select_streams", "v:0",
            "-show_entries", "stream=codec_name,profile,width,height,pix_fmt", "-of", "json", sample], 15, bindLifetime: true);
        if (probe.Code != 0 || !string.IsNullOrWhiteSpace(probe.Error)) return false;
        var format = ParseFormat(probe.Output);
        // Rewind currently writes SDR 4:2:0 AV1. Unknown profiles/chroma are left
        // to software; even a known format still needs a real GPU decode test.
        if (!Eligible(format)) return false;
        var binary = new FileInfo(MediaTools.Find("ffmpeg"));
        var key = new CapabilityKey($"{binary.FullName}|{binary.Length}|{binary.LastWriteTimeUtc.Ticks}", format!);
        Lazy<Task<bool>> capability;
        lock (cacheGate)
        {
            if (!capabilities.TryGetValue(key, out capability!))
            {
                // Bound positive and in-flight capability entries.
                if (capabilities.Count >= 16) capabilities.Clear();
                capability = new(() => ProbeDecode(sample, format!));
                capabilities.Add(key, capability);
            }
        }
        bool supported;
        try { supported = await capability.Value; }
        catch (OperationCanceledException)
        {
            lock (cacheGate)
                if (capabilities.GetValueOrDefault(key) == capability) capabilities.Remove(key);
            throw;
        }
        if (!supported)
        {
            // A busy/resetting GPU is not a permanent lack of codec support.
            // This Recorder retains its false support task (no per-Save probes),
            // but a new capture session may probe again with a healthy sample.
            lock (cacheGate)
                if (capabilities.GetValueOrDefault(key) == capability) capabilities.Remove(key);
        }
        return supported;
    }

    internal static VideoFormat? ParseFormat(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("streams", out var streams) || streams.GetArrayLength() != 1) return null;
        var stream = streams[0];
        string Text(string key) => stream.TryGetProperty(key, out var value) ? value.GetString() ?? "" : "";
        int Number(string key) => stream.TryGetProperty(key, out var value) && value.TryGetInt32(out var number) ? number : 0;
        return new(Text("codec_name"), Text("profile"), Number("width"), Number("height"), Text("pix_fmt"));
    }

    internal static bool Eligible(VideoFormat? format) => format is { Codec: "av1", Profile: "Main", Width: > 0, Height: > 0,
        PixelFormat: "yuv420p" or "yuv420p10le" };

    private static async Task<bool> ProbeDecode(string sample, VideoFormat format)
    {
        try
        {
            var softwareFormat = format.PixelFormat == "yuv420p10le" ? "p010le" : "nv12";
            var result = await MediaTools.Run("ffmpeg", ["-hide_banner", "-v", "error", "-xerror", "-progress", "pipe:1",
                "-hwaccel", "cuda", "-hwaccel_output_format", "cuda", "-c:v", "av1_cuvid", "-i", sample,
                "-map", "0:v:0", "-vf", "hwdownload,format=" + softwareFormat,
                "-frames:v", "3", "-fps_mode", "passthrough", "-an", "-f", "null", "-"], 15, bindLifetime: true);
            return result.Code == 0 && string.IsNullOrWhiteSpace(result.Error) &&
                result.Output.Split('\n').Any(line => line.StartsWith("frame=") &&
                    int.TryParse(line[6..].Trim(), out var frames) && frames == 3);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
    }
}
