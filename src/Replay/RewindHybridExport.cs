using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Replay;

// Save accepts/owns media once. This strategy has no capture, history or
// publication access. Phase 2 stays first; failed attempts go directly to Phase 1.
internal sealed class RewindHybridExport
{
    internal sealed record Piece(string Path, Av1SegmentIndex Index, int First, int Stop);
    internal sealed record Plan(RewindExportSnapshot Snapshot, ReadOnlyCollection<Piece> Pieces,
        Av1SegmentIndex Format, int Frames, ReadOnlyCollection<Version> Versions);
    internal sealed record Decision(Plan? Plan, string Reason);
    internal sealed record Result(RewindPacketCopy.Result Export, bool HybridEligible, bool HybridFallback,
        string HybridReason, int EncodedFrames = 0, int CopiedFrames = 0);
    internal sealed record Version(string Path, uint Volume, ulong File, long Size, long Written);
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);
    private readonly RewindVideoExport.Command run;
    private readonly bool enabled;
    // Instance-scoped development opt-in, no setting/environment activation.
    // Default remains off until real Save measurements justify an automatic rule.
    internal RewindHybridExport(bool enabled = false, RewindVideoExport.Command? run = null)
    { this.enabled = enabled; this.run = run ?? ((tool, args, timeout) => MediaTools.Run(tool, args, timeout, bindLifetime: true)); }
    private static string Number(double n) => n.ToString("F6", CultureInfo.InvariantCulture);
    private static double Seconds(double n) => double.Parse(Number(n), CultureInfo.InvariantCulture);
    private static long Grid(int n, int fps) => (long)Math.Round(n * 1000d / fps, MidpointRounding.AwayFromZero);
    private static Version Identify(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (!GetFileInformationByHandle(file.SafeFileHandle, out var info) || info.Volume == 0 || (info.IndexHigh | info.IndexLow) == 0)
            throw new IOException("Не подтверждена идентичность owned media.");
        return new(path, info.Volume, ((ulong)info.IndexHigh << 32) | info.IndexLow, file.Length,
            ((long)info.Written.dwHighDateTime << 32) | (uint)info.Written.dwLowDateTime);
    }
    private static void Unchanged(Plan plan)
    {
        if (plan.Versions.Any(v => v != Identify(v.Path))) throw new IOException("Исходные данные Hybrid изменились.");
    }
    internal static Decision Inspect(RewindExportSnapshot snapshot)
    {
        try
        {
            if (snapshot.Ranges.Count == 0 || snapshot.Fps is < 1 or > 120 || !double.IsFinite(snapshot.Duration) ||
                snapshot.Duration <= 0 || snapshot.Duration > 120.000001 || !double.IsFinite(snapshot.Accepted) ||
                !double.IsFinite(snapshot.HistoryStart) || double.IsNaN(snapshot.PreviousCutoff) ||
                double.IsPositiveInfinity(snapshot.PreviousCutoff) || double.IsNaN(snapshot.Epoch) || double.IsPositiveInfinity(snapshot.Epoch))
                return new(null, "invalid-or-unbounded-snapshot");
            var versions = snapshot.Ranges.Select(r => Identify(r.Path)).Concat(snapshot.AudioFiles.Select(Identify)).ToArray();
            List<Piece> pieces = []; Av1SegmentIndex? format = null;
            double cursor = 0, previous = double.NegativeInfinity; int sourceFrame = 0, count = 0;
            var begin = Seconds(snapshot.Ranges[0].Start - snapshot.Ranges[0].SourceStart);
            var duration = Seconds(snapshot.Duration);
            foreach (var (range, number) in snapshot.Ranges.Select((r, i) => (r, i)))
            {
                if (!double.IsFinite(range.SourceStart) || !double.IsFinite(range.SourceEnd) || !double.IsFinite(range.Start) ||
                    !double.IsFinite(range.End) || range.SourceStart > range.Start || range.End > range.SourceEnd || range.Start >= range.End ||
                    range.Start < Math.Max(snapshot.HistoryStart, snapshot.PreviousCutoff) || range.End > snapshot.Accepted ||
                    range.SourceStart < previous || number > 0 && range.Start != range.SourceStart ||
                    number + 1 < snapshot.Ranges.Count && range.End != range.SourceEnd)
                    return new(null, "history-acceptance-or-interval-mismatch");
                previous = range.SourceEnd;
                var index = Av1SegmentIndex.Read(range.Path); format ??= index;
                var seconds = Seconds(range.SourceEnd - range.SourceStart);
                if (!format.Compatible(index) || Math.Abs(index.DefaultDuration - 1e9 / snapshot.Fps) > 1 ||
                    index.Packets.Skip(1).Any(p => p.RandomAccess) ||
                    Math.Abs(index.Packets.Count / (double)snapshot.Fps - seconds) > .000001 ||
                    index.Packets.Where((p, i) => p.Pts != Grid(i, snapshot.Fps) ||
                        Math.Abs(cursor * 1000 + p.Pts - Grid(sourceFrame + i, snapshot.Fps)) > .000001).Any())
                    return new(null, "incompatible-headers-metadata-cfr-or-multiple-gops");
                var selected = index.Packets.Select((p, i) => (p, i)).Where(p => cursor + p.p.Pts / 1000d >= begin &&
                    cursor + p.p.Pts / 1000d < begin + duration).Select(p => p.i).ToArray();
                var local = index.Packets.Select((p, i) => (p, i)).Where(p => p.p.Pts / 1000d >= Seconds(range.Start - range.SourceStart) &&
                    p.p.Pts / 1000d < Seconds(range.End - range.SourceStart)).Select(p => p.i).ToArray();
                if (selected.Length == 0 || !selected.SequenceEqual(local)) return new(null, "legacy-and-history-frame-selection-disagree");
                // Only the first bounded segment is re-encoded; continuation is
                // at a separately indexed independent shown KEY, never a P frame.
                if (number == 0 && index.Packets.Count > snapshot.Fps * 2 || number > 0 && selected[0] != 0)
                    return new(null, "unsupported-boundary-or-interior");
                pieces.Add(new(range.Path, index, selected[0], selected[^1] + 1)); count += selected.Length;
                cursor += seconds; sourceFrame = checked(sourceFrame + index.Packets.Count);
            }
            if (pieces[0].First == 0 || pieces.Count < 2) return new(null, "no-leading-boundary-or-copyable-interior");
            if (count != Math.Ceiling(duration * snapshot.Fps - 1e-9)) return new(null, "ambiguous-legacy-cfr-boundary");
            var plan = new Plan(snapshot, pieces.AsReadOnly(), format!, count, Array.AsReadOnly(versions));
            Unchanged(plan);
            return new(plan, "verified-bounded-av1-boundary-and-copy");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new(null, "unverified-hybrid: " + ex.Message); }
    }

    internal async Task<Result> Run(RewindExportSnapshot snapshot, RewindPacketCopy copy, RewindVideoExport fallback,
        string encoder, IReadOnlyList<string> arguments, string temporary, Func<Task> verify)
    {
        var original = Array.AsReadOnly(arguments.ToArray());
        var copyDecision = await RewindPacketCopy.Inspect(snapshot);
        if (copyDecision.Plan != null || !enabled || encoder != "av1_nvenc")
            return new(await copy.Run(copyDecision, fallback, encoder, snapshot.Ranges[0].Path, original, temporary, verify),
                false, false, copyDecision.Plan != null ? "packet-copy-preferred" : !enabled ? "hybrid-disabled-pending-production-evidence" : "non-av1");
        SaveTiming.Current?.Mark("hybrid-planning-started");
        var decision = await Task.Run(() => Inspect(snapshot));
        SaveTiming.Current?.Mark("hybrid-planning-completed", new { decision.Reason, Eligible = decision.Plan != null, Frames = decision.Plan?.Frames });
        string? working = null;
        if (decision.Plan is { } plan)
        {
            CheckArguments(snapshot, original, temporary);
            try
            {
                working = Path.Combine(Path.GetDirectoryName(temporary)!, ".hybrid-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(working); Unchanged(plan);
                await SequenceProof(plan.Pieces[0].Path);
                var headPiece = plan.Pieces[0]; var encoded = headPiece.Stop - headPiece.First;
                var headPath = Path.Combine(working, "head.mkv");
                SaveTiming.Current?.Mark("hybrid-boundary-started", new { Frames = encoded });
                await Command("ffmpeg", HeadArguments(headPiece, snapshot.Fps, original, headPath), 30);
                var head = Av1SegmentIndex.Read(headPath, cached: false);
                if (!plan.Format.Compatible(head) || head.Packets.Count != encoded ||
                    head.Packets.Where((p, i) => p.Pts != Grid(i, snapshot.Fps)).Any())
                    throw new IOException("Boundary sequence/header/frame grid несовместим.");
                SaveTiming.Current?.Mark("hybrid-boundary-completed", new { Bytes = new FileInfo(headPath).Length });
                var hashes = head.Packets.Select(p => p.Hash).Concat(plan.Pieces.Skip(1).SelectMany(p => p.Index.Packets.Take(p.Stop).Select(p => p.Hash))).ToArray();
                var tailPiece = plan.Pieces[^1]; var tailPath = tailPiece.Path;
                if (tailPiece.Stop != tailPiece.Index.Packets.Count)
                {
                    // -frames:v on a combined video/AAC output can stop AAC a
                    // frame early. Bound only the video-only tail intermediate;
                    // the final mux retains the exact original audio -t.
                    tailPath = Path.Combine(working, "tail.mkv");
                    SaveTiming.Current?.Mark("hybrid-tail-started", new { Frames = tailPiece.Stop });
                    await Command("ffmpeg", ["-v", "error", "-xerror", "-i", tailPiece.Path, "-map", "0:v:0", "-an", "-c:v", "copy",
                        "-frames:v", tailPiece.Stop.ToString(CultureInfo.InvariantCulture), "-y", tailPath], 30);
                    var tail = Av1SegmentIndex.Read(tailPath, cached: false);
                    if (!plan.Format.Compatible(tail) || tail.Packets.Count != tailPiece.Stop || tail.Packets.Where((p, i) =>
                        p.Hash != tailPiece.Index.Packets[i].Hash || p.Pts != tailPiece.Index.Packets[i].Pts).Any())
                        throw new IOException("Tail prefix packets not preserved.");
                    SaveTiming.Current?.Mark("hybrid-tail-completed", new { Bytes = new FileInfo(tailPath).Length });
                }
                var concat = Path.Combine(working, "video.txt");
                File.WriteAllText(concat, Entry(headPath, encoded, snapshot.Fps) + string.Concat(plan.Pieces.Skip(1).Select(p =>
                    Entry(p == tailPiece ? tailPath : p.Path, p == tailPiece ? p.Stop : p.Index.Packets.Count, snapshot.Fps))), new UTF8Encoding(false));
                SaveTiming.Current?.Mark("hybrid-copy-aac-mux-started", new { Copied = plan.Frames - encoded, Encoded = encoded });
                var args = RewindPacketCopy.Arguments(original).ToList(); args[args.IndexOf("-i") + 1] = concat;
                var ticks = $"round(N/({snapshot.Fps}*TB))";
                args.InsertRange(args.Count - 1, ["-bsf:v", $"setts=pts={ticks}:dts={ticks}"]);
                await Command("ffmpeg", args, 180);
                SaveTiming.Current?.Mark("hybrid-copy-aac-mux-completed");
                await verify(); SaveTiming.Current?.Mark("hybrid-validation-started");
                var output = await Task.Run(() => Av1SegmentIndex.Read(temporary, cached: false));
                if (!plan.Format.Compatible(output) || output.Packets.Count != hashes.Length ||
                    output.Packets.Where((p, i) => p.Pts != Grid(i, snapshot.Fps) || p.Hash != hashes[i]).Any())
                    throw new IOException("Hybrid packet hashes/timestamps/count не подтверждены.");
                await Validate(plan, original, temporary, working, encoded);
                Unchanged(plan);
                SaveTiming.Current?.Mark("hybrid-validation-completed");
                return new(new(new("hybrid-av1", false), false, false, copyDecision.Reason), true, false, decision.Reason, encoded, plan.Frames - encoded);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SaveTiming.Current?.Mark("hybrid-fallback", new { Reason = ex.Message });
                ClipFiles.Log("rewind-hybrid-fallback", ex, temporary);
                await ClipFiles.RetrySharing(() => File.Delete(temporary));
                // A changed accepted source is not a usable fallback snapshot.
                Unchanged(plan);
            }
            finally
            {
                if (working != null)
                    await ClipFiles.RetrySharing(() => { if (Directory.Exists(working)) Directory.Delete(working, true); });
            }
        }
        var video = await fallback.Run(encoder, snapshot.Ranges[0].Path, original, temporary, verify);
        return new(new(video, false, false, copyDecision.Reason), decision.Plan != null, decision.Plan != null, decision.Reason);
    }
    private static void CheckArguments(RewindExportSnapshot snapshot, IReadOnlyList<string> arguments, string temporary)
    {
        var args = arguments.ToList();
        var inputs = args.Select((a, i) => (a, i)).Where(p => p.a == "-i").Select(p => args[p.i + 1]).ToArray();
        var expected = snapshot.Ranges.SelectMany(r => new[] { "file '" + r.Path.Replace('\\', '/').Replace("'", "'\\''") + "'", "duration " + Number(r.SourceEnd - r.SourceStart) });
        var video = $"[0:v]trim=start={Number(snapshot.Ranges[0].Start - snapshot.Ranges[0].SourceStart)}:duration={Number(snapshot.Duration)},setpts=PTS-STARTPTS[v]";
        if (inputs.Length != snapshot.AudioFiles.Count + 1 || !inputs.Skip(1).SequenceEqual(snapshot.AudioFiles) ||
            !File.ReadAllLines(inputs[0]).SequenceEqual(expected) || args[args.IndexOf("-filter_complex") + 1].Split(';')[0] != video ||
            args[args.IndexOf("-t") + 1] != Number(snapshot.Duration) || args[args.IndexOf("-r") + 1] != snapshot.Fps.ToString(CultureInfo.InvariantCulture) ||
            args[^1] != temporary) throw new IOException("Hybrid arguments do not describe the accepted snapshot.");
    }
    private static string Entry(string path, int frames, int fps) => "file '" + path.Replace('\\', '/').Replace("'", "'\\''") + "'\nduration " + Number(frames / (double)fps) + "\n";
    internal static IReadOnlyList<string> HeadArguments(Piece piece, int fps, IReadOnlyList<string> original, string output)
    {
        List<string> args = ["-hide_banner", "-v", "error", "-xerror", "-hwaccel", "cuda", "-hwaccel_output_format", "cuda", "-c:v", "av1_cuvid",
            "-i", piece.Path, "-vf", $"trim=start_frame={piece.First}:end_frame={piece.Stop},setpts=PTS-STARTPTS", "-an", "-c:v", "av1_nvenc"];
        foreach (var option in new[] { "-preset", "-tune", "-rc", "-cq", "-b:v", "-maxrate", "-bufsize", "-bf" })
        { var at = original.ToList().IndexOf(option); if (at < 0) throw new ArgumentException("Missing original encoder option: " + option); args.AddRange([option, original[at + 1]]); }
        args.AddRange(["-g", (fps * 2).ToString(CultureInfo.InvariantCulture), "-r", fps.ToString(CultureInfo.InvariantCulture),
            "-frames:v", (piece.Stop - piece.First).ToString(CultureInfo.InvariantCulture), "-y", output]); return args;
    }
    private async Task<(int Code, string Output, string Error)> Command(string tool, IEnumerable<string> args, int timeout)
    {
        var result = await run(tool, args, timeout);
        if (result.Code != 0 || !string.IsNullOrWhiteSpace(result.Error)) throw new IOException("Ошибка Hybrid export: " + result.Error);
        return result;
    }
    private async Task SequenceProof(string path)
    {
        var result = await run("ffmpeg", ["-hide_banner", "-v", "verbose", "-i", path, "-map", "0:v:0", "-c:v", "copy", "-frames:v", "1", "-bsf:v", "trace_headers", "-f", "null", "-"], 15);
        var fields = Regex.Matches(result.Error, @"\s([a-z0-9_]+(?:\[\d+\])?)\s+[01]+\s+=\s+(\d+)")
            .GroupBy(m => m.Groups[1].Value).ToDictionary(g => g.Key, g => g.Select(m => int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)).Distinct().ToArray());
        if (result.Code != 0 || new[] { "seq_profile", "reduced_still_picture_header", "timing_info_present_flag", "operating_points_cnt_minus_1", "frame_id_numbers_present_flag" }
            .Any(k => !fields.TryGetValue(k, out var v) || !v.SequenceEqual(new[] { 0 }))) throw new IOException("Unknown AV1 sequence/dependency syntax.");
    }

    private static string PixelFilter(Plan plan) => plan.Format.BitDepth == 10 ? "hwdownload,format=p010le,format=yuv420p10le" : "hwdownload,format=nv12,format=yuv420p";
    private static string[] Rows(string text) => text.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith('#') && l.Contains(',')).Select(l => string.Join(',', l.Split(',').Select(v => v.Trim()))).ToArray();
    private async Task<string[]> FrameHashes(List<string> args, bool video)
    {
        if (!video) return Rows((await Command("ffmpeg", args, 180)).Output);
        try { return Rows((await Command("ffmpeg", RewindVideoExport.Arguments(args, true), 180)).Output); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SaveTiming.Current?.Mark("hybrid-validation-software-fallback", new { ex.Message });
            var software = args.Select(a => a.Replace("hwdownload,format=p010le,", "").Replace("hwdownload,format=nv12,", "")).ToList();
            return Rows((await Command("ffmpeg", RewindVideoExport.Arguments(software, false), 180)).Output);
        }
    }
    private async Task Validate(Plan plan, IReadOnlyList<string> original, string output, string working, int encoded)
    {
        var fps = plan.Snapshot.Fps; var pixel = PixelFilter(plan);
        // Full entropy/reference decode of every output stream; text hashes use
        // bounded memory, with no decoded video/audio intermediate on disk.
        var actual = await FrameHashes(["-v", "error", "-xerror", "-i", output, "-map", "0", "-vf", pixel,
            "-fps_mode:v", "passthrough", "-f", "framemd5", "-"], true);
        var video = actual.Where(r => r.StartsWith("0,")).ToArray();
        if (video.Length != plan.Frames) throw new IOException("Incorrect decoded Hybrid frame count.");
        var filter = original[original.ToList().IndexOf("-filter_complex") + 1].Split(';')[0];
        var trim = filter[(filter.IndexOf(']') + 1)..filter.LastIndexOf('[')];
        var source = await FrameHashes(["-v", "error", "-xerror", "-f", "concat", "-safe", "0", "-i", original[original.ToList().IndexOf("-i") + 1],
            "-map", "0:v:0", "-vf", trim + "," + pixel, "-r", fps.ToString(CultureInfo.InvariantCulture), "-t", Number(plan.Snapshot.Duration), "-an", "-f", "framemd5", "-"], true);
        if (source.Length != video.Length || source.Where((r, i) => !r.Split(',').Take(4).SequenceEqual(video[i].Split(',').Take(4)) ||
            i >= encoded && r != video[i]).Any()) throw new IOException("Source/output frame selection, copied pixels or PTS differ.");
        await BoundaryContent(plan, output, working, encoded);
        await PacketTimes(output, plan, original);
        if (plan.Snapshot.AudioFiles.Count == 0) return;
        // Same frozen routing/filter/AAC options, with video mapping removed.
        // Re-encoding AAC gives a reference for decoded samples, priming and
        // padding; this deliberate initial validation cost is benchmarked.
        var audio = RewindPacketCopy.Arguments(original).ToList();
        var map = audio.IndexOf("0:v:0"); audio.RemoveRange(map - 1, 2);
        var codec = audio.LastIndexOf("-c:v"); audio.RemoveRange(codec, 2);
        audio.Insert(0, "-vn"); audio[^1] = Path.Combine(working, "audio-reference.mka");
        await Command("ffmpeg", audio, 180);
        var expectedAudio = await FrameHashes(["-v", "error", "-xerror", "-i", audio[^1], "-map", "0:a", "-f", "framemd5", "-"], false);
        var actualAudio = actual.Where(r => !r.StartsWith("0,")).Select(r =>
        { var split = r.Split(','); split[0] = (int.Parse(split[0], CultureInfo.InvariantCulture) - 1).ToString(CultureInfo.InvariantCulture); return string.Join(',', split); }).ToArray();
        // framemd5 interleaves streams according to worker arrival, not a fixed
        // track order. Compare every sample/PTS row within each named track.
        static Dictionary<string, string[]> Tracks(string[] rows) => rows.GroupBy(r => r.Split(',')[0]).ToDictionary(g => g.Key, g => g.ToArray());
        var expectedTracks = Tracks(expectedAudio); var actualTracks = Tracks(actualAudio);
        if (expectedTracks.Count != actualTracks.Count || expectedTracks.Any(p => !actualTracks.TryGetValue(p.Key, out var rows) || !p.Value.SequenceEqual(rows)))
        {
            SaveTiming.Current?.Mark("hybrid-audio-validation-rejected", new
            {
                Expected = expectedTracks.ToDictionary(p => p.Key, p => p.Value.Length),
                Actual = actualTracks.ToDictionary(p => p.Key, p => p.Value.Length)
            });
            throw new IOException("Decoded AAC samples/timestamps/priming/padding differ.");
        }
    }
    private async Task BoundaryContent(Plan plan, string output, string working, int encoded)
    {
        var piece = plan.Pieces[0]; var pixel = PixelFilter(plan);
        var stats = Path.Combine(working, "boundary-psnr.txt");
        // No GPU interop: download only the bounded boundary for PSNR. CUDA
        // frames otherwise remain on the GPU during the actual boundary encode.
        var path = stats.Replace('\\', '/').Replace(":", "\\:");
        List<string> args = ["-v", "error", "-xerror", "-hwaccel", "cuda", "-hwaccel_output_format", "cuda", "-c:v", "av1_cuvid", "-i", piece.Path,
            "-hwaccel", "cuda", "-hwaccel_output_format", "cuda", "-c:v", "av1_cuvid", "-i", output,
            "-filter_complex", $"[0:v]trim=start_frame={piece.First}:end_frame={piece.Stop},setpts=PTS-STARTPTS,{pixel}[s];[1:v]trim=end_frame={encoded},setpts=PTS-STARTPTS,{pixel}[o];[s][o]psnr=stats_file='{path}'[check]",
            "-map", "[check]", "-frames:v", encoded.ToString(CultureInfo.InvariantCulture), "-an", "-f", "null", "-"];
        try { await Command("ffmpeg", args, 30); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            for (var i = args.Count - 2; i >= 0; i--) if (args[i] is "-hwaccel" or "-hwaccel_output_format" or "-c:v") args.RemoveRange(i, 2);
            var at = args.IndexOf("-filter_complex"); args[at + 1] = args[at + 1].Replace("hwdownload,format=p010le,", "").Replace("hwdownload,format=nv12,", "");
            await Command("ffmpeg", args, 30);
        }
        var values = File.ReadAllLines(stats).Select(l => Regex.Match(l, @"psnr_avg:([\d.]+|inf)")).ToArray();
        if (values.Length != encoded || values.Any(m => !m.Success || m.Groups[1].Value != "inf" &&
            double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) < 30)) throw new IOException("Boundary content/count verification failed.");
    }
    private async Task PacketTimes(string output, Plan plan, IReadOnlyList<string> original)
    {
        var result = await Command("ffprobe", ["-v", "error", "-show_streams", "-show_packets", "-show_entries", "stream=index,codec_name,codec_type,sample_rate,channels:stream_tags=title:stream_disposition=default:packet=stream_index,pts,dts", "-of", "json", output], 30);
        using var document = JsonDocument.Parse(result.Output);
        var streams = document.RootElement.GetProperty("streams");
        var audioStreams = streams.EnumerateArray().Where(s => s.GetProperty("codec_type").GetString() == "audio").ToArray();
        var args = original.ToList();
        var mix = args.Contains("[mix]");
        if (audioStreams.Length != plan.Snapshot.AudioFiles.Count + (mix ? 1 : 0) || audioStreams.Where((s, i) =>
                s.GetProperty("codec_name").GetString() != "aac" || s.GetProperty("sample_rate").GetString() != "48000" || s.GetProperty("channels").GetInt32() != 2 ||
                !s.TryGetProperty("tags", out var tags) || !tags.TryGetProperty("title", out var title) ||
                title.GetString() != args[args.IndexOf($"-metadata:s:a:{i}") + 1][6..] ||
                s.GetProperty("disposition").GetProperty("default").GetInt32() != (mix && i == 0 ? 1 : 0)).Any())
            throw new IOException("AAC tracks/routing metadata not preserved.");
        Dictionary<int, (long Pts, long Dts)> last = []; var frame = 0;
        foreach (var p in document.RootElement.GetProperty("packets").EnumerateArray())
        {
            var stream = p.GetProperty("stream_index").GetInt32(); var pts = p.GetProperty("pts").GetInt64(); var dts = p.GetProperty("dts").GetInt64();
            if (last.TryGetValue(stream, out var previous) && (pts <= previous.Pts || dts <= previous.Dts) || stream == 0 && (pts != dts || pts != Grid(frame++, plan.Snapshot.Fps)))
                throw new IOException("Unverified output PTS/DTS.");
            last[stream] = (pts, dts);
        }
        if (frame != plan.Frames || last.Count != streams.GetArrayLength()) throw new IOException("Incomplete output stream decode/timestamps.");
    }
}
