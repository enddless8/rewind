using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Replay;

// Recording has a contiguous owned journal and streaming PCM, not a Rewind
// snapshot. SaveClip selection/fallback and the research prototype stay separate.
internal sealed class RecordingPacketExport
{
    internal sealed record Snapshot(ReadOnlyCollection<Segment> Segments, double Start, double End, int Fps,
        ReadOnlyCollection<string> AudioFiles)
    {
        internal double Duration => End - Start;
        internal static Snapshot Create(IEnumerable<Segment> segments, double start, double end, int fps, IEnumerable<string> audio) =>
            new(Array.AsReadOnly(segments.ToArray()), start, end, fps, Array.AsReadOnly(audio.ToArray()));
    }
    internal sealed record Piece(string Path, Av1SegmentIndex Index, int First, int Stop);
    internal sealed record Plan(Snapshot Snapshot, ReadOnlyCollection<Piece> Pieces, Av1SegmentIndex Format, int Frames);
    internal sealed record Decision(Plan? Plan, string Reason);
    internal sealed record Result(string Strategy, bool Fallback, string Reason, int CopiedFrames, int EncodedFrames);
    private readonly RewindVideoExport.Command run;
    internal RecordingPacketExport(RewindVideoExport.Command? run = null) =>
        this.run = run ?? ((tool, args, timeout) => MediaTools.Run(tool, args, timeout, bindLifetime: true));
    private static string Number(double n) => n.ToString("F6", CultureInfo.InvariantCulture);
    private static long Grid(int n, int fps) => (long)Math.Round(n * 1000d / fps, MidpointRounding.AwayFromZero);

    internal static Decision Inspect(Snapshot snapshot)
    {
        try
        {
            if (snapshot.Fps <= 0 || !double.IsFinite(snapshot.Start) || !double.IsFinite(snapshot.End) ||
                snapshot.Duration <= 0 || snapshot.Segments.Count == 0) return new(null, "invalid-recording-interval");
            var first = snapshot.Segments[0].Start;
            var begin = double.Parse(Number(snapshot.Start - first), CultureInfo.InvariantCulture);
            var duration = double.Parse(Number(snapshot.Duration), CultureInfo.InvariantCulture);
            var limit = begin + duration;
            if (begin < 0 || snapshot.End > snapshot.Segments[^1].End) return new(null, "outside-owned-media");
            List<Piece> pieces = []; Av1SegmentIndex? format = null; double cursor = 0; double? previous = null; int count = 0, sourceFrame = 0;
            foreach (var segment in snapshot.Segments)
            {
                if (!double.IsFinite(segment.Start) || !double.IsFinite(segment.End) || segment.End <= segment.Start ||
                    previous is { } end && Math.Abs(segment.Start - end) > .000001)
                    return new(null, "discontinuous-or-reordered-journal");
                previous = segment.End;
                var index = Av1SegmentIndex.Read(segment.Path); format ??= index;
                var seconds = double.Parse(Number(segment.End - segment.Start), CultureInfo.InvariantCulture);
                if (!format.Compatible(index) || Math.Abs(index.DefaultDuration - 1e9 / snapshot.Fps) > 1 ||
                    Math.Abs(index.Packets.Count / (double)snapshot.Fps - seconds) > .000001 ||
                    index.Packets.Where((p, i) => p.Pts != Grid(i, snapshot.Fps) ||
                        Math.Abs(cursor * 1000 + p.Pts - Grid(sourceFrame + i, snapshot.Fps)) > .000001).Any())
                    return new(null, "incompatible-format-or-frame-grid");
                var selected = index.Packets.Select((p, i) => (p, i)).Where(p => cursor + p.p.Pts / 1000d >= begin &&
                    cursor + p.p.Pts / 1000d < limit).Select(p => p.i).ToArray();
                if (selected.Length > 0)
                {
                    if (pieces.Count > 0 && selected[0] != 0) return new(null, "partial-interior-start");
                    pieces.Add(new(segment.Path, index, selected[0], selected[^1] + 1)); count += selected.Length;
                }
                cursor += seconds;
                sourceFrame = checked(sourceFrame + index.Packets.Count);
            }
            if (pieces.Count == 0 || count != Math.Ceiling(duration * snapshot.Fps - 1e-9) || count > 360_000)
                return new(null, "ambiguous-legacy-cfr-boundary");
            // Encode only a partial first segment; join at the next original KEY.
            if (pieces[0].First > 0 && pieces.Count == 1) return new(null, "no-copyable-interior");
            return new(new(snapshot, pieces.AsReadOnly(), format!, count), "verified-av1-recording-frame-grid");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new(null, "unverified-recording: " + ex.Message); }
    }

    internal async Task<Result> Run(Snapshot snapshot, string encoder, Settings settings, IReadOnlyList<string> arguments,
        string temporary, Func<Task> verify, int timeout)
    {
        var original = Array.AsReadOnly(arguments.ToArray());
        SaveTiming.Current?.Mark("recording-planning-started");
        var decision = encoder == "av1_nvenc" ? await Task.Run(() => Inspect(snapshot)) : new Decision(null, "non-av1-legacy-finalizer");
        SaveTiming.Current?.Mark("recording-planning-completed", new { decision.Reason, Eligible = decision.Plan != null, Frames = decision.Plan?.Frames });
        string? working = null;
        if (decision.Plan is { } plan)
        {
            try
            {
                working = Path.Combine(Path.GetDirectoryName(temporary)!, ".packet-export-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(working);
                var identities = snapshot.Segments.Select(s => Fingerprint(s.Path)).Concat(snapshot.AudioFiles.Select(Fingerprint)).ToArray();
                await SequenceProof(plan.Pieces[0].Path);
                List<string> hashes = []; List<(string Path, int Count)> assembled = []; var encoded = 0;
                foreach (var piece in plan.Pieces)
                {
                    if (piece.First > 0)
                    {
                        var boundary = Path.Combine(working, "head.mkv"); encoded = piece.Stop - piece.First;
                        SaveTiming.Current?.Mark("recording-boundary-started", new { Frames = encoded });
                        await EncodeHead(piece, snapshot.Fps, settings, boundary);
                        var head = Av1SegmentIndex.Read(boundary, cached: false);
                        if (!plan.Format.Compatible(head) || head.Packets.Count != encoded ||
                            head.Packets.Where((p, i) => p.Pts != Grid(i, snapshot.Fps)).Any())
                            throw new IOException("Начальный GOP записи несовместим с исходными сегментами.");
                        hashes.AddRange(head.Packets.Select(p => p.Hash)); assembled.Add((boundary, encoded));
                        SaveTiming.Current?.Mark("recording-boundary-completed");
                    }
                    else
                    {
                        hashes.AddRange(piece.Index.Packets.Take(piece.Stop).Select(p => p.Hash));
                        assembled.Add((piece.Path, piece.Index.Packets.Count));
                    }
                }
                var concat = Path.Combine(working, "video.txt");
                File.WriteAllText(concat, string.Concat(assembled.Select(p => "file '" + p.Path.Replace('\\', '/').Replace("'", "'\\''") +
                    "'\nduration " + Number(p.Count / (double)snapshot.Fps) + "\n")), new UTF8Encoding(false));
                SaveTiming.Current?.Mark("recording-copy-aac-mux-started", new { Copied = plan.Frames - encoded, Encoded = encoded });
                await Command("ffmpeg", CopyArguments(original, concat, snapshot.Fps), timeout);
                SaveTiming.Current?.Mark("recording-copy-aac-mux-completed");
                await verify(); SaveTiming.Current?.Mark("recording-validation-started");
                var output = await Task.Run(() => Av1SegmentIndex.Read(temporary, cached: false));
                if (!plan.Format.Compatible(output) || output.Packets.Count != hashes.Count ||
                    output.Packets.Where((p, i) => p.Pts != Grid(i, snapshot.Fps) || p.Hash != hashes[i]).Any())
                    throw new IOException("Проверка пакетов записи не пройдена.");
                await Decode(temporary, plan.Frames, timeout);
                if (!identities.SequenceEqual(snapshot.Segments.Select(s => Fingerprint(s.Path)).Concat(snapshot.AudioFiles.Select(Fingerprint))))
                    throw new IOException("Исходные данные записи изменились во время экспорта.");
                SaveTiming.Current?.Mark("recording-validation-completed");
                return new(encoded == 0 ? "av1-packet-copy" : "av1-boundary-copy", false, decision.Reason, plan.Frames - encoded, encoded);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SaveTiming.Current?.Mark("recording-copy-fallback", new { Reason = ex.Message });
                ClipFiles.Log("recording-copy-fallback", ex, temporary);
                await ClipFiles.RetrySharing(() => File.Delete(temporary));
            }
            finally
            {
                if (working != null)
                {
                    // Unique attempt-owned head/manifest only, never source media.
                    try { Directory.Delete(working, true); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ClipFiles.Log("recording-copy-cleanup", ex, working); }
                }
            }
        }
        SaveTiming.Current?.Mark("recording-legacy-started");
        var legacy = await run("ffmpeg", original.Prepend("-xerror"), timeout);
        if (legacy.Code != 0 || !string.IsNullOrWhiteSpace(legacy.Error)) throw new IOException("Не удалось собрать запись: " + legacy.Error);
        await verify(); SaveTiming.Current?.Mark("recording-legacy-completed");
        return new("legacy-reencode", decision.Plan != null, decision.Reason, 0, 0);
    }
    private static (long Size, long Written) Fingerprint(string path)
    { var file = new FileInfo(path); return (file.Length, file.LastWriteTimeUtc.Ticks); }
    private async Task Command(string tool, IEnumerable<string> args, int timeout)
    {
        var result = await run(tool, args, timeout);
        if (result.Code != 0 || !string.IsNullOrWhiteSpace(result.Error)) throw new IOException("Ошибка экспорта записи: " + result.Error);
    }
    private async Task SequenceProof(string source)
    {
        var result = await run("ffmpeg", ["-hide_banner", "-v", "verbose", "-i", source, "-map", "0:v:0", "-c:v", "copy",
            "-frames:v", "1", "-bsf:v", "trace_headers", "-f", "null", "-"], 15);
        var fields = Regex.Matches(result.Error, @"\s([a-z0-9_]+(?:\[\d+\])?)\s+[01]+\s+=\s+(\d+)")
            .GroupBy(m => m.Groups[1].Value).ToDictionary(g => g.Key, g => g.Select(m => int.Parse(m.Groups[2].Value)).Distinct().ToArray());
        if (result.Code != 0 || new[] { "seq_profile", "reduced_still_picture_header", "timing_info_present_flag",
                "operating_points_cnt_minus_1", "frame_id_numbers_present_flag" }.Any(k => !fields.TryGetValue(k, out var v) || !v.SequenceEqual(new[] { 0 })))
            throw new IOException("Не подтверждены AV1 sequence/dependency ограничения записи.");
    }
    private async Task EncodeHead(Piece piece, int fps, Settings settings, string output)
    {
        List<string> args = ["-hide_banner", "-v", "error", "-xerror", "-hwaccel", "cuda", "-hwaccel_output_format", "cuda", "-c:v", "av1_cuvid",
            "-i", piece.Path, "-vf", $"trim=start_frame={piece.First}:end_frame={piece.Stop},setpts=PTS-STARTPTS", "-an",
            "-c:v", "av1_nvenc", "-preset", "p4", "-tune", "hq", "-rc", "vbr", "-cq", settings.Quality.ToString(), "-b:v", "0",
            "-maxrate", settings.MaxBitrateMbps + "M", "-bufsize", settings.MaxBitrateMbps * 2 + "M", "-bf", "0", "-g", (fps * 2).ToString(), "-r", fps.ToString(), "-y", output];
        await Command("ffmpeg", args, 30);
    }
    private async Task Decode(string path, int expected, int timeout)
    {
        List<string> args = ["-v", "error", "-xerror", "-progress", "pipe:1", "-i", path, "-map", "0", "-fps_mode:v", "passthrough", "-f", "null", "-"];
        bool Complete((int Code, string Output, string Error) r) => r.Code == 0 && string.IsNullOrWhiteSpace(r.Error) && r.Output.Contains("progress=end") &&
            int.TryParse(r.Output.Split('\n').LastOrDefault(l => l.StartsWith("frame="))?[6..].Trim(), out var frames) && frames == expected;
        (int Code, string Output, string Error) result = (-1, "", "");
        try { result = await run("ffmpeg", RewindVideoExport.Arguments(args, true), timeout); }
        catch (Exception ex) when (ex is not OperationCanceledException) { SaveTiming.Current?.Mark("recording-validation-software-fallback", new { ex.Message }); }
        if (!Complete(result)) result = await run("ffmpeg", RewindVideoExport.Arguments(args, false), timeout);
        if (!Complete(result)) throw new IOException("Запись не прошла полное декодирование видео и аудио.");
    }
    internal static IReadOnlyList<string> CopyArguments(IReadOnlyList<string> original, string concat, int fps)
    {
        var args = original.ToList(); args.Insert(0, "-xerror"); args[args.IndexOf("-i") + 1] = concat;
        var filter = args.IndexOf("-filter_complex");
        var filters = args[filter + 1].Split(';').Where(f => !f.StartsWith("[0:v]", StringComparison.Ordinal)).ToArray();
        if (filters.Length == 0) args.RemoveRange(filter, 2); else args[filter + 1] = string.Join(';', filters);
        args[args.IndexOf("[v]")] = "0:v:0"; args[args.LastIndexOf("-c:v") + 1] = "copy";
        foreach (var option in new[] { "-preset", "-tune", "-rc", "-cq", "-b:v", "-maxrate", "-bufsize", "-bf", "-r" })
        { var at = args.IndexOf(option); if (at >= 0) args.RemoveRange(at, 2); }
        var ticks = $"round(N/({fps}*TB))"; args.InsertRange(args.Count - 1, ["-bsf:v", $"setts=pts={ticks}:dts={ticks}"]);
        return args;
    }
}
