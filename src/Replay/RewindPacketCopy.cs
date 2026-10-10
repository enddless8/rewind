using System.Collections.ObjectModel;
using System.Globalization;

namespace Replay;

internal sealed class RewindPacketCopy
{
    internal sealed record PlannedPacket(long Pts, string Hash);
    internal sealed record Plan(RewindExportSnapshot Snapshot, Av1SegmentIndex Format,
        ReadOnlyCollection<PlannedPacket> Packets);
    internal sealed record Decision(Plan? Plan, string Reason);
    internal sealed record Result(RewindVideoExport.Result Video, bool Eligible, bool CopyFallback, string Reason);
    private readonly RewindVideoExport.Command run;
    internal RewindPacketCopy(RewindVideoExport.Command? run = null) =>
        this.run = run ?? ((tool, args, timeout) => MediaTools.Run(tool, args, timeout, bindLifetime: true));

    internal static async Task<Decision> Inspect(RewindExportSnapshot snapshot)
    {
        SaveTiming.Current?.Mark("export-planning-started");
        Decision decision;
        try
        {
            // Most saves start inside a GOP. Reject these before touching payloads.
            if (snapshot.Ranges.Count == 0 || snapshot.Fps <= 0 || !double.IsFinite(snapshot.Duration) || snapshot.Duration <= 0 ||
                !double.IsFinite(snapshot.Accepted) || !double.IsFinite(snapshot.HistoryStart) ||
                double.IsNaN(snapshot.PreviousCutoff) || double.IsPositiveInfinity(snapshot.PreviousCutoff) ||
                snapshot.Ranges.Any(r => !double.IsFinite(r.SourceStart) || !double.IsFinite(r.SourceEnd) ||
                    !double.IsFinite(r.Start) || !double.IsFinite(r.End) || r.Start != r.SourceStart || r.End > r.SourceEnd || r.End <= r.Start ||
                    r.Start < snapshot.HistoryStart || r.Start < snapshot.PreviousCutoff || r.End > snapshot.Accepted))
                decision = new(null, "unaligned-or-invalid-logical-start");
            else decision = await Task.Run(() => Build(snapshot));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { decision = new(null, "unverified-segment: " + ex.Message); }
        SaveTiming.Current?.Mark("export-planning-completed", new { Eligible = decision.Plan != null, decision.Reason, Frames = decision.Plan?.Packets.Count });
        return decision;
    }
    private static Decision Build(RewindExportSnapshot snapshot)
    {
        var expected = new List<PlannedPacket>(); Av1SegmentIndex? first = null; double cursor = 0; var frame = 0;
        var duration = double.Parse(snapshot.Duration.ToString("F6", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        for (var range = 0; range < snapshot.Ranges.Count; range++)
        {
            var r = snapshot.Ranges[range];
            if (range + 1 < snapshot.Ranges.Count && r.End != r.SourceEnd) return new(null, "partial-interior-interval");
            var index = Av1SegmentIndex.Read(r.Path); first ??= index;
            if (!first.Compatible(index) || Math.Abs(index.DefaultDuration - 1_000_000_000.0 / snapshot.Fps) > 1)
                return new(null, "incompatible-codec-headers-color-or-timebase");
            var segmentDuration = double.Parse((r.SourceEnd - r.SourceStart).ToString("F6", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            if (Math.Abs(index.Packets.Count / (double)snapshot.Fps - segmentDuration) > .000001)
                return new(null, "segment-duration-not-exact-frame-grid");
            foreach (var packet in index.Packets)
            {
                var seconds = cursor + packet.Pts / 1000.0;
                var grid = (long)Math.Round(frame * 1000.0 / snapshot.Fps, MidpointRounding.AwayFromZero);
                if (Math.Abs(seconds * 1000 - grid) > .000001) return new(null, "non-cfr-or-reordered-packets");
                if (seconds < duration) expected.Add(new(grid, packet.Hash));
                frame++;
            }
            cursor += segmentDuration;
        }
        // The old output -r/-t selects CFR frames before the output duration.
        // Require exactly that set, not merely a similar container duration.
        if (expected.Count == 0 || expected.Count != Math.Ceiling(duration * snapshot.Fps - 1e-9))
            return new(null, "ambiguous-end-boundary");
        return new(new(snapshot, first!, expected.AsReadOnly()), "verified-av1-shown-keyframe-low-delay-prefix");
    }
    internal async Task<Result> Run(Decision decision, RewindVideoExport fallback, string encoder, string closedSample,
        IReadOnlyList<string> arguments, string temporary, Func<Task> verify)
    {
        // Freeze the caller's argument list too. In particular, audio and -t never
        // change between copy, NVDEC and software attempts.
        var original = Array.AsReadOnly(arguments.ToArray());
        var failed = false;
        if (decision.Plan is { } plan)
        {
            try
            {
                SaveTiming.Current?.Mark("packet-copy-started", new { Frames = plan.Packets.Count });
                var result = await run("ffmpeg", Arguments(original), 180);
                SaveTiming.Current?.Mark("packet-copy-processing-completed", new { result.Code });
                if (result.Code != 0 || !string.IsNullOrWhiteSpace(result.Error)) throw new IOException("Не удалось скопировать видео: " + result.Error);
                await verify();
                var index = await Task.Run(() => Av1SegmentIndex.Read(temporary, cached: false));
                if (!plan.Format.Compatible(index) || index.Packets.Count != plan.Packets.Count ||
                    index.Packets.Where((p, i) => p.Pts != plan.Packets[i].Pts || p.Hash != plan.Packets[i].Hash).Any())
                    throw new IOException("Проверка пакетов и временных меток скопированного видео не пройдена.");
                // Prove the actual completed media decodes, not just its key flags.
                // No encoder is started by this null-output validation.
                List<string> decode = ["-v", "error", "-xerror", "-progress", "pipe:1", "-i", temporary,
                    "-map", "0", "-fps_mode:v", "passthrough", "-f", "null", "-"];
                (int Code, string Output, string Error) decoded = (-1, "", "");
                try { decoded = await run("ffmpeg", RewindVideoExport.Arguments(decode, true), 180); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { SaveTiming.Current?.Mark("packet-copy-validation-software-fallback", new { Reason = ex.Message }); }
                if (!CompleteDecode(decoded, plan.Packets.Count))
                    decoded = await run("ffmpeg", RewindVideoExport.Arguments(decode, false), 180);
                if (!CompleteDecode(decoded, plan.Packets.Count)) throw new IOException("Скопированный клип не прошёл проверку декодирования.");
                SaveTiming.Current?.Mark("packet-copy-validated");
                return new(new("packet-copy-av1", false), true, false, decision.Reason);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed = true;
                SaveTiming.Current?.Mark("packet-copy-fallback", new { Reason = ex.Message });
                ClipFiles.Log("rewind-copy-fallback", ex, temporary);
                await ClipFiles.RetrySharing(() => File.Delete(temporary));
            }
        }
        var video = await fallback.Run(encoder, closedSample, original, temporary, verify);
        return new(video, decision.Plan != null, failed, decision.Reason);
    }
    private static bool CompleteDecode((int Code, string Output, string Error) result, int expected)
    {
        var frame = result.Output.Split('\n').LastOrDefault(l => l.StartsWith("frame="));
        return result.Code == 0 && string.IsNullOrWhiteSpace(result.Error) && result.Output.Contains("progress=end") &&
            frame != null && int.TryParse(frame[6..].Trim(), out var count) && count == expected;
    }
    internal static IReadOnlyList<string> Arguments(IReadOnlyList<string> original)
    {
        var args = original.ToList(); args.Insert(0, "-xerror");
        var filter = args.IndexOf("-filter_complex");
        var filters = args[filter + 1].Split(';').Skip(1).ToArray();
        if (filters.Length == 0) args.RemoveRange(filter, 2); else args[filter + 1] = string.Join(';', filters);
        args[args.IndexOf("[v]")] = "0:v:0";
        var encoder = args.LastIndexOf("-c:v"); args[encoder + 1] = "copy";
        foreach (var option in new[] { "-preset", "-tune", "-rc", "-cq", "-b:v", "-maxrate", "-bufsize", "-bf", "-r" })
        { var at = args.IndexOf(option); if (at >= 0) args.RemoveRange(at, 2); }
        return args;
    }
}
