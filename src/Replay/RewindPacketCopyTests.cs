using System.Globalization;
using System.Text.Json;

namespace Replay;

internal static class RewindPacketCopyTests
{
    internal static async Task Run(string directory, Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        byte[] sequence = [0, 0];
        byte[] key = [0x0a, 2, 0, 0, 0x32, 1, 0x10];
        check(Av1SegmentIndex.InspectPacket(key, sequence, out var random) && random, "AV1 shown KEY_FRAME plus exact sequence header is independent");
        foreach (var data in new byte[][] { [0x32, 1, 0x10], [0x32, 1, 0x80], [0x32, 1, 0x20], [0x32, 1, 0x50],
            [0x32, 1, 0x70], [0x36, 1, 1, 0x30], [0x32, 127, 0x30], [0x0a, 2, 0, 1, 0x32, 1, 0x10],
            [0x32, 1, 0x30, 0x32, 1, 0x30], [0x1a, 1, 0x30] })
            check(!Av1SegmentIndex.InspectPacket(data, sequence, out _), "Unknown/hidden/show-existing/layered/multi-frame/changed-sequence AV1 packet is rejected");
        check(Av1SegmentIndex.InspectPacket([0x32, 1, 0x30], sequence, out random) && !random, "Shown low-delay inter frame is not mistaken for random access");
        var ranges = new[] { new RewindExportSnapshot.Range("owned.mkv", 0, 2, .5, 2) };
        var audio = new[] { "System.wav" };
        var snapshot = new RewindExportSnapshot(2, .5, .5, 0, 30, ranges, audio);
        ranges[0] = ranges[0] with { Start = 0 }; audio[0] = "changed.wav";
        check(snapshot.Ranges[0].Start == .5 && snapshot.AudioFiles[0] == "System.wav", "Immutable snapshot owns its range/audio collections");
        check((await RewindPacketCopy.Inspect(snapshot)).Plan == null, "In-GOP logical/history cutoff is rejected without probing a file");
        var invalid = new RewindExportSnapshot(1, 0, double.NegativeInfinity, 0, 30,
            [new("missing", 0, 2, 0, 2)], []);
        check((await RewindPacketCopy.Inspect(invalid)).Plan == null, "No range may pass the accepted QPC boundary");
        var nonfinite = new RewindExportSnapshot(double.PositiveInfinity, 0, double.NegativeInfinity, 0, 30,
            [new("missing", 0, 2, 0, 2)], []);
        check((await RewindPacketCopy.Inspect(nonfinite)).Plan == null, "Nonfinite acceptance cannot authorize packet-copy");
        var fake = new Av1SegmentIndex(1_000_000, 33_333_333, "private", "video", [new(0, "hash", true)]);
        var plan = new RewindPacketCopy.Decision(new(snapshot, fake, Array.AsReadOnly(new[] { new RewindPacketCopy.PlannedPacket(0, "hash") })), "fixture");
        List<string> args = ["-v", "error", "-f", "concat", "-safe", "0", "-i", "video.txt", "-i", "System.wav",
            "-filter_complex", "[0:v]trim=start=0.5:duration=1.5,setpts=PTS-STARTPTS[v];[1:a]amix=inputs=1:normalize=0[mix]",
            "-map", "[v]", "-map", "[mix]", "-map", "1:a:0", "-c:v", "av1_nvenc", "-preset", "p4", "-r", "30",
            "-c:a", "aac", "-t", "1.5", "-y", "unused.mkv"];
        var copy = RewindPacketCopy.Arguments(args).ToList();
        check(copy[copy.IndexOf("-c:v") + 1] == "copy" && !copy.Contains("-r") && !copy.Contains("-preset") &&
            copy[copy.IndexOf("-filter_complex") + 1] == "[1:a]amix=inputs=1:normalize=0[mix]" && copy.Contains("1:a:0") &&
            copy[copy.IndexOf("-t") + 1] == "1.5", "Copy changes only video strategy; AAC/mix/track maps and duration remain unchanged");
        foreach (var failure in new[] { "exit", "timeout", "storage" })
        {
            var output = Path.Combine(directory, failure + ".mkv"); var copies = 0; var fallbacks = 0;
            var exporter = new RewindPacketCopy((_, _, _) => {
                copies++; File.WriteAllText(output, "partial");
                if (failure == "timeout") throw new TimeoutException("injected copy timeout");
                if (failure == "storage") throw new IOException("injected insufficient storage");
                return Task.FromResult((1, "", "injected copy error"));
            });
            var fallback = new RewindVideoExport(supportOverride: _ => Task.FromResult(false), run: (_, original, _) => {
                fallbacks++; check(!File.Exists(output), "Rejected copy output is removed before fallback: " + failure);
                check(original.SequenceEqual(RewindVideoExport.Arguments(args, false)), "Fallback receives exactly the original immutable trim/audio arguments: " + failure);
                File.WriteAllText(output, "complete"); return Task.FromResult((0, "", ""));
            });
            var result = await exporter.Run(plan, fallback, "av1_nvenc", "owned.mkv", args, output, () => Task.CompletedTask);
            check(result.CopyFallback && copies == 1 && fallbacks == 1 && File.ReadAllText(output) == "complete", "One failed copy retries Phase 1 once: " + failure);
        }
        var cancelled = 0;
        try {
            await new RewindPacketCopy((_, _, _) => throw new OperationCanceledException()).Run(plan,
                new(run: (_, _, _) => { cancelled++; return Task.FromResult((0, "", "")); }), "av1_nvenc", "owned", args, "none", () => Task.CompletedTask);
            check(false, "Cancellation must propagate");
        } catch (OperationCanceledException) { check(cancelled == 0, "Cancelled packet-copy never starts fallback or publishes"); }
        var lockedOutput = Path.Combine(directory, "locked.mkv"); File.WriteAllText(lockedOutput, "partial");
        var held = new FileStream(lockedOutput, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Run(async () => { await Task.Delay(200); held.Dispose(); });
        var lockedResult = await new RewindPacketCopy((_, _, _) => Task.FromResult((1, "", "locked test"))).Run(plan,
            new(supportOverride: _ => Task.FromResult(false), run: (_, _, _) => {
                check(!File.Exists(lockedOutput), "Sharing retry releases rejected output before fallback"); return Task.FromResult((0, "", ""));
            }), "av1_nvenc", "owned", args, lockedOutput, () => Task.CompletedTask);
        await release; check(lockedResult.CopyFallback, "Temporary output scanner lock uses bounded sharing retry");
        check(!args.Contains("copy") && args.Contains("[v]"), "Copy argument construction does not mutate the decoded-trim reference");
    }

    internal static async Task RunMedia(string directory, Action<bool, string> check)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        var segments = Path.Combine(directory, "segments"); Directory.CreateDirectory(segments);
        await Tool("ffmpeg", ["-v", "error", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=30", "-t", "6", "-c:v", "av1_nvenc",
            "-bf", "0", "-g", "60", "-force_key_frames", "expr:gte(t,n_forced*2)", "-f", "segment", "-segment_time", "2",
            "-reset_timestamps", "1", "-y", Path.Combine(segments, "part-%03d.mkv")]);
        var files = Directory.GetFiles(segments, "*.mkv").Order().ToArray();
        var index = Av1SegmentIndex.Read(files[0]);
        check(index.Packets.Count == 60 && index.Packets[0].RandomAccess, "Real NVENC segment has independently parsed shown keyframe, sequence header and ordered packets");
        var linked = Path.Combine(directory, "linked.mkv"); MediaOwnership.Retain(files[0], linked);
        check(ReferenceEquals(index, Av1SegmentIndex.Read(linked)), "Windows file-ID cache reuses verified index across ownership hard links");
        RewindExportSnapshot Snapshot(double start, double end, bool gap = false) => new(end, start, start, 0, 30,
            files.Take(2).Select((p, i) => new RewindExportSnapshot.Range(p, i == 1 && gap ? 5 : i * 2,
                i == 1 && gap ? 7 : i * 2 + 2, i == 0 ? start : gap ? 5 : 2,
                Math.Min(end, i == 1 && gap ? 7 : i * 2 + 2))), []);
        var aligned = await RewindPacketCopy.Inspect(Snapshot(0, 4));
        check(aligned.Plan != null && aligned.Plan.Packets.Count == 120, "GOP-aligned real start/end is eligible without keyframe rounding");
        check((await RewindPacketCopy.Inspect(Snapshot(.73, 4))).Plan == null, "Real inside-GOP start/cutoff retains NVDEC decoded trimming");
        var tail = await RewindPacketCopy.Inspect(Snapshot(0, 3.15));
        check(tail.Plan != null && tail.Plan.Packets.Count == 95, "Inside-GOP end is safe only for positively verified shown low-delay prefix");
        var gap = await RewindPacketCopy.Inspect(Snapshot(0, 6.45, true));
        check(gap.Plan != null && gap.Plan.Packets.Count == 104, "Each recovery-range boundary is independently verified; removed gaps do not enter output timestamps");
        var clone = Path.Combine(directory, "changed-private.mkv"); File.Copy(files[1], clone, true);
        var bytes = File.ReadAllBytes(clone); var privateData = Convert.FromHexString(index.CodecPrivate); var at = Find(bytes, privateData);
        bytes[at + 1] ^= 1; File.WriteAllBytes(clone, bytes);
        var changed = new RewindExportSnapshot(4, 0, double.NegativeInfinity, 0, 30,
            [new(files[0], 0, 2, 0, 2), new(clone, 2, 4, 2, 4)], []);
        check((await RewindPacketCopy.Inspect(changed)).Plan == null, "Changed AV1 codec private/sequence configuration cannot enter a copy concat");
        bytes[at + 1] |= 0x20; File.WriteAllBytes(clone, bytes);
        check((await RewindPacketCopy.Inspect(changed)).Plan == null, "Unsupported AV1 profile metadata rejects copy before reading packets");
        var preroll = Path.Combine(directory, "unsupported-preroll.mkv");
        var altered = File.ReadAllBytes(files[0]); var uid = Find(altered, [0x73, 0xc5]);
        altered[uid] = 0x56; altered[uid + 1] = 0xbb; File.WriteAllBytes(preroll, altered);
        check((await RewindPacketCopy.Inspect(new(2, 0, double.NegativeInfinity, 0, 30,
            [new(preroll, 0, 2, 0, 2)], []))).Plan == null,
            "Video preroll/delay or timeline transformations require decoded fallback");
        var oldIndex = Av1SegmentIndex.Read(linked); // original is closed, test-owned
        var mutable = Path.Combine(directory, "mutable.mkv"); File.Copy(files[0], mutable, true);
        var cached = Av1SegmentIndex.Read(mutable); File.SetLastWriteTimeUtc(mutable, DateTime.UtcNow.AddSeconds(5));
        check(!ReferenceEquals(cached, Av1SegmentIndex.Read(mutable)), "Cache is invalidated by changed file identity/version");
        foreach (var encoder in new[] { "av1_nvenc", "h264_nvenc", "hevc_nvenc" })
        {
            var incompatible = Path.Combine(directory, encoder + "-different.mkv");
            await Tool("ffmpeg", ["-v", "error", "-f", "lavfi", "-i", "testsrc2=s=320x180:r=30", "-t", "2", "-c:v", encoder, "-bf", "0", "-y", incompatible]);
            var mismatch = new RewindExportSnapshot(4, 0, double.NegativeInfinity, 0, 30,
                [new(files[0], 0, 2, 0, 2), new(incompatible, 2, 4, 2, 4)], []);
            check((await RewindPacketCopy.Inspect(mismatch)).Plan == null, "Actual codec/resolution change falls back: " + encoder);
        }
        foreach (var variant in new[] { "ten-bit", "color" })
        {
            var path = Path.Combine(directory, variant + ".mkv");
            List<string> generate = ["-v", "error", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=30", "-t", "2", "-c:v", "av1_nvenc", "-bf", "0"];
            if (variant == "ten-bit") generate.AddRange(["-pix_fmt", "p010le"]);
            else generate.AddRange(["-vf", "setparams=color_primaries=bt2020:color_trc=arib-std-b67:colorspace=bt2020nc"]);
            generate.AddRange(["-y", path]); await Tool("ffmpeg", generate);
            var mismatch = new RewindExportSnapshot(4, 0, double.NegativeInfinity, 0, 30,
                [new(files[0], 0, 2, 0, 2), new(path, 2, 4, 2, 4)], []);
            check((await RewindPacketCopy.Inspect(mismatch)).Plan == null, "Actual AV1 bit-depth/color metadata change rejects concat: " + variant);
            if (variant == "ten-bit")
            {
                var ten = await RewindPacketCopy.Inspect(new(2, 0, double.NegativeInfinity, 0, 30,
                    [new(path, 0, 2, 0, 2)], []));
                var tenOutput = Path.Combine(directory, "ten-bit-copy.mkv");
                var tenManifest = Path.Combine(directory, "ten-bit.txt");
                File.WriteAllText(tenManifest, "file '" + path.Replace('\\', '/') + "'\nduration 2.000000\n");
                List<string> tenArgs = ["-v", "error", "-f", "concat", "-safe", "0", "-i", tenManifest,
                    "-filter_complex", "[0:v]trim=start=0:duration=2,setpts=PTS-STARTPTS[v]", "-map", "[v]",
                    "-c:v", "av1_nvenc", "-r", "30", "-bf", "0", "-t", "2", "-y", tenOutput];
                var copied = await new RewindPacketCopy().Run(ten, new(), "av1_nvenc", path, tenArgs, tenOutput, () => VerifyStreams(tenOutput, 1));
                check(copied.Video.Decoder == "packet-copy-av1" && Av1SegmentIndex.Read(tenOutput, false).BitDepth == 10,
                    "Actual compatible ten-bit AV1 uses validated packet-copy without bit-depth conversion");
                var sourceRaw = await Tool("ffmpeg", ["-v", "error", "-i", path, "-map", "0:v", "-f", "framemd5", "-"]);
                var copyRaw = await Tool("ffmpeg", ["-v", "error", "-i", tenOutput, "-map", "0:v", "-f", "framemd5", "-"]);
                check(Frames(sourceRaw.Output).SequenceEqual(Frames(copyRaw.Output)), "Ten-bit copied frames/PTS exactly match original decoded pixels");
            }
        }
        foreach (var decision in new[] { aligned, tail, gap })
        {
            var plan = decision.Plan!; var name = plan.Packets.Count.ToString(); var root = Path.Combine(directory, name); Directory.CreateDirectory(root);
            var manifest = Path.Combine(root, "video.txt");
            File.WriteAllLines(manifest, plan.Snapshot.Ranges.SelectMany(r => new[] { "file '" + r.Path.Replace('\\', '/') + "'",
                "duration " + (r.SourceEnd - r.SourceStart).ToString("F6", CultureInfo.InvariantCulture) }));
            var duration = plan.Snapshot.Duration.ToString("F6", CultureInfo.InvariantCulture);
            foreach (var track in new[] { "System", "Microphone", "Discord" })
            {
                var pcm = new byte[(int)Math.Ceiling(plan.Snapshot.Duration * AudioTrack.Rate) * AudioTrack.BytesPerFrame];
                for (var n = 0; n < pcm.Length / 4; n++) {
                    var signal = (short)(Math.Sin(n * (track == "System" ? .03 : track == "Microphone" ? .07 : .11)) * 5000);
                    BitConverter.TryWriteBytes(pcm.AsSpan(n * 4, 2), signal); BitConverter.TryWriteBytes(pcm.AsSpan(n * 4 + 2, 2), signal);
                }
                AudioTrack.WriteWave(Path.Combine(root, track + ".wav"), [new(0, pcm)], 0, plan.Snapshot.Duration, -60);
            }
            var output = Path.Combine(root, "copy.mkv");
            List<string> args = ["-v", "error", "-f", "concat", "-safe", "0", "-i", manifest];
            foreach (var track in new[] { "System", "Microphone", "Discord" }) args.AddRange(["-i", Path.Combine(root, track + ".wav")]);
            args.AddRange(["-filter_complex", "[0:v]trim=start=0:duration=" + duration + ",setpts=PTS-STARTPTS[v];[1:a][2:a][3:a]amix=inputs=3:normalize=0,alimiter=limit=0.95:latency=1[mix]",
                "-map", "[v]", "-map", "[mix]", "-map", "1:a:0", "-map", "2:a:0", "-map", "3:a:0", "-c:v", "av1_nvenc", "-bf", "0", "-r", "30", "-c:a", "aac", "-b:a", "160k", "-t", duration,
                "-metadata:s:a:0", "title=Mix", "-metadata:s:a:1", "title=System", "-metadata:s:a:2", "title=Microphone", "-metadata:s:a:3", "title=Discord", "-y", output]);
            var result = await new RewindPacketCopy().Run(decision, new(), "av1_nvenc", files[0], args, output, () => VerifyStreams(output, 5));
            check(result.Video.Decoder == "packet-copy-av1" && !result.CopyFallback, "Real packet-copy, packet hashes/PTS and full decode validation passed: " + name);
            await CompareRaw(output, args, root, check);
            var packetProbe = await Tool("ffprobe", ["-v", "error", "-select_streams", "v:0", "-show_packets",
                "-show_entries", "packet=pts,dts", "-of", "json", output]);
            var timestamps = JsonSerializer.Deserialize<JsonElement>(packetProbe.Output).GetProperty("packets").EnumerateArray().ToArray();
            check(timestamps.Length == plan.Packets.Count && timestamps.All(p => p.GetProperty("pts").GetInt64() == p.GetProperty("dts").GetInt64()) &&
                timestamps.Zip(timestamps.Skip(1)).All(p => p.First.GetProperty("dts").GetInt64() < p.Second.GetProperty("dts").GetInt64()),
                "Independent ffprobe confirms exact frame count, DTS=PTS and monotonic DTS: " + name);
            if (name == "120")
            {
                var softwarePath = Path.Combine(root, "software-validation.mkv"); args[^1] = softwarePath;
                var hardwareFailures = 0; var softwareValidations = 0;
                var validated = await new RewindPacketCopy(async (tool, command, timeout) => {
                    if (command.Contains("av1_cuvid")) { hardwareFailures++; throw new TimeoutException("injected GPU validator timeout after child reap"); }
                    if (command.Contains("null")) softwareValidations++;
                    return await MediaTools.Run(tool, command, timeout, bindLifetime: true);
                }).Run(decision, new(run: (_, _, _) => throw new Exception("No encoding retry is needed")),
                    "av1_nvenc", files[0], args, softwarePath, () => VerifyStreams(softwarePath, 5));
                check(validated.Video.Decoder == "packet-copy-av1" && !validated.CopyFallback && hardwareFailures == 1 && softwareValidations == 1,
                    "GPU validator exception retains completed copy and performs real full software validation without re-encoding");
            }
            // Force a real FFmpeg stream-copy failure, preserving the exact snapshot.
            var failed = 0;
            var fallbackPath = Path.Combine(root, "fallback.mkv"); args[^1] = fallbackPath;
            var fallback = await new RewindPacketCopy(async (tool, command, timeout) => {
                if (command.Contains("copy")) { failed++; var bad = command.ToList(); bad[bad.LastIndexOf("-c:v") + 1] = "missing_copy_test_codec";
                    var failure = await MediaTools.Run(tool, bad, timeout, bindLifetime: true); File.WriteAllText(fallbackPath, "partial"); return failure; }
                return await MediaTools.Run(tool, command, timeout, bindLifetime: true);
            }).Run(decision, new(), "av1_nvenc", files[0], args, fallbackPath, () => VerifyStreams(fallbackPath, 5));
            check(failed == 1 && fallback.CopyFallback && fallback.Video.Decoder == "nvdec-av1", "Actual FFmpeg copy failure cleans output and retries Phase 1 from the same snapshot: " + name);
            await Tool("ffmpeg", ["-v", "error", "-xerror", "-i", fallbackPath, "-map", "0", "-f", "null", "-"]);
            check(true, "Retry result fully decodes every video/audio stream: " + name);
        }
    }
    private static int Find(byte[] haystack, byte[] needle)
    { for (var i = 0; i <= haystack.Length - needle.Length; i++) if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i; throw new IOException("Fixture pattern missing"); }
    internal static async Task VerifyStreams(string path, int count)
    {
        var probe = await Tool("ffprobe", ["-v", "error", "-show_streams", "-of", "json", path]);
        if (JsonSerializer.Deserialize<JsonElement>(probe.Output).GetProperty("streams").GetArrayLength() != count) throw new IOException("Fixture track count changed");
    }
    internal static async Task<(int Code, string Output, string Error)> Tool(string tool, IEnumerable<string> args)
    {
        var result = await MediaTools.Run(tool, args, 180, bindLifetime: true);
        if (result.Code != 0 || !string.IsNullOrWhiteSpace(result.Error)) throw new IOException(result.Error); return result;
    }
    private static string[] Frames(string output) => output.Split('\n').Where(l => !l.StartsWith('#') && !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).ToArray();
    internal static async Task CompareRaw(string output, List<string> reference, string root, Action<bool, string> check)
    {
        var manifest = reference[reference.IndexOf("-i") + 1]; var duration = reference[reference.IndexOf("-t") + 1];
        var filter = reference[reference.IndexOf("-filter_complex") + 1].Split(';')[0].Replace("[0:v]", "").Replace("[v]", "");
        var expected = await Tool("ffmpeg", ["-v", "error", "-xerror", "-f", "concat", "-safe", "0", "-i", manifest,
            "-vf", filter, "-fps_mode", "passthrough", "-an", "-f", "framemd5", "-"]);
        var actual = await Tool("ffmpeg", ["-v", "error", "-xerror", "-i", output, "-map", "0:v:0", "-fps_mode", "passthrough", "-f", "framemd5", "-"]);
        check(Frames(expected.Output).SequenceEqual(Frames(actual.Output)), "Copied decoded frame indices/hashes/PTS exactly equal original decoded-trim reference");
        File.WriteAllText(Path.Combine(root, "trim-reference.framemd5"), expected.Output); File.WriteAllText(Path.Combine(root, "copy.framemd5"), actual.Output);
        var encoded = reference.ToList(); var referencePath = Path.Combine(root, "nvdec-reference.mkv"); encoded[^1] = referencePath;
        await Tool("ffmpeg", RewindVideoExport.Arguments(encoded, true));
        await VerifyStreams(referencePath, 5);
        foreach (var path in new[] { output, referencePath })
        {
            var pcm = await Tool("ffmpeg", ["-v", "error", "-xerror", "-i", path, "-map", "0:a", "-f", "framemd5", "-"]);
            File.WriteAllText(Path.Combine(root, path == output ? "copy-audio.framemd5" : "reference-audio.framemd5"), pcm.Output);
        }
        check(Frames(File.ReadAllText(Path.Combine(root, "copy-audio.framemd5"))).SequenceEqual(Frames(File.ReadAllText(Path.Combine(root, "reference-audio.framemd5")))),
            "All Mix/System/Microphone/Discord decoded AAC samples and timestamps equal same-snapshot NVDEC export");
    }

    internal static async Task RunProduction(string directory, Action<bool, string> check)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        var label = new System.Windows.Controls.TextBlock { FontSize = 32, Foreground = System.Windows.Media.Brushes.White,
            VerticalAlignment = System.Windows.VerticalAlignment.Center, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
        var window = new System.Windows.Window { Title = "Проверка границ Rewind", Width = 620, Height = 350, Content = label,
            Background = System.Windows.Media.Brushes.DarkSlateBlue, Topmost = true, ShowActivated = false };
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) }; var frame = 0;
        timer.Tick += (_, _) => label.Text = "Проверка границ · " + ++frame;
        window.Show(); window.Hide(); window.Show(); timer.Start();
        try
        {
            var settings = new Settings { CaptureMode = "Desktop", HideDisplayBorder = true, AudioMode = "System", MicrophoneId = "off",
                Discord = false, Codec = "AV1", Height = 720, Fps = 30, BufferSeconds = 30, Quality = 27, MaxBitrateMbps = 15,
                GroupByApplication = false, OutputDirectory = Path.Combine(directory, "output"), BufferDirectory = Path.Combine(directory, "buffer") };
            await using var recorder = new Recorder(settings, null, Native.Displays().First(d => d.Primary));
            await recorder.Start(); await recorder.StartRecording(); await Task.Delay(12500);
            async Task<(int Code, string Output, string Error)> Fail(string tool, IEnumerable<string> input, int timeout)
            {
                var args = input.ToList(); args[args.LastIndexOf("-c:v") + 1] = "missing_both_export_test_codec";
                return await MediaTools.Run(tool, args, timeout, bindLifetime: true);
            }
            recorder.PacketCopy = new(Fail); recorder.VideoExport = new(hardwareEnabled: false, run: Fail);
            try { await recorder.Save(); check(false, "Both copy/fallback failures must throw"); } catch (IOException) { }
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            check(double.IsNegativeInfinity((double)typeof(Recorder).GetField("rewindSaveCutoff", flags)!.GetValue(recorder)!) &&
                Directory.GetFiles(settings.OutputDirectory, "*.mkv").Length == 0, "Real copy + decoded export failure rolls back original cutoff and publishes nothing");
            var references = new List<(string Output, List<string> Args, string Root)>();
            var save = 0;
            recorder.PacketCopy = new(async (tool, input, timeout) => {
                var args = input.ToList();
                if (args.Contains("copy")) {
                    var staging = Path.GetDirectoryName(args[^1])!; var root = Path.Combine(directory, "snapshot-" + save++); Retain(staging, root);
                    var reference = args.Select(a => a.Replace(staging, root)).ToList();
                    var duration = reference[reference.IndexOf("-t") + 1];
                    var filterAt = reference.IndexOf("-filter_complex");
                    reference[filterAt + 1] = "[0:v]trim=start=0:duration=" + duration + ",setpts=PTS-STARTPTS[v];" + reference[filterAt + 1];
                    reference[reference.IndexOf("0:v:0")] = "[v]"; reference[reference.LastIndexOf("-c:v") + 1] = "av1_nvenc";
                    reference.InsertRange(reference.IndexOf("-c:a"), ["-r", "30", "-bf", "0"]);
                    references.Add((args[^1], reference, root));
                }
                return await MediaTools.Run(tool, args, timeout, bindLifetime: true);
            });
            recorder.VideoExport = new();
            var first = await recorder.Save();
            var firstMetadata = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.ChangeExtension(first.Path, ".json")));
            var cutoff = firstMetadata.GetProperty("AcceptedSaveBoundary").GetDouble();
            check(firstMetadata.GetProperty("ExportDecoder").GetString() == "packet-copy-av1", "Real early-history Save uses packet-copy while conventional recording remains active");
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, cutoff + 15 - Native.Clock)));
            var second = await recorder.Save();
            var metadata = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.ChangeExtension(second.Path, ".json")));
            check(!metadata.GetProperty("PacketCopyEligible").GetBoolean() && metadata.GetProperty("ExportDecoder").GetString() == "nvdec-av1" &&
                metadata.GetProperty("ActualStart").GetDouble() >= cutoff && Math.Abs(second.Duration - 15) < .15,
                "30-second buffer: +15s Save with prior cutoff inside GOP uses NVDEC and exposes only new history");
            check(recorder.Running && recorder.Recording.State == RecordingState.Recording, "Copy, rollback and unaligned Save preserve active producer and independent recording ownership");
            // Validate the first copy against its original decoded trim after the
            // second acceptance; reference analysis cannot postpone that boundary.
            var referenceItem = references.Single();
            var reference = referenceItem.Args; var manifest = reference[reference.IndexOf("-i") + 1];
            var filter = reference[reference.IndexOf("-filter_complex") + 1].Split(';')[0].Replace("[0:v]", "").Replace("[v]", "");
            var raw = await Tool("ffmpeg", ["-v", "error", "-xerror", "-f", "concat", "-safe", "0", "-i", manifest,
                "-vf", filter, "-fps_mode", "passthrough", "-an", "-f", "framemd5", "-"]);
            var copied = await Tool("ffmpeg", ["-v", "error", "-xerror", "-i", first.Path, "-map", "0:v:0", "-fps_mode", "passthrough", "-f", "framemd5", "-"]);
            check(Frames(raw.Output).SequenceEqual(Frames(copied.Output)), "Real capture copy frame hashes/PTS equal immutable-snapshot decoded trim, with no preroll");
            await Tool("ffmpeg", ["-v", "error", "-xerror", "-i", second.Path, "-map", "0", "-f", "null", "-"]);
            var recording = await recorder.StopRecording();
            check(recording != null && recording.Duration > first.Duration + second.Duration, "Conventional recording retains history across copy and cutoff saves");
            await Tool("ffmpeg", ["-v", "error", "-xerror", "-i", recording!.Path, "-map", "0", "-f", "null", "-"]);
            check(true, "Conventional recording remains fully playable after concurrent stream-copy Save");
            var locked = Path.Combine(directory, "locked-publication.mkv"); File.Copy(first.Path, locked, true);
            var final = Path.Combine(directory, "published-after-lock.mkv");
            var handle = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read);
            var released = Task.Run(async () => { await Task.Delay(200); handle.Dispose(); });
            await ClipFiles.PublishFile(locked, final); await released;
            check(File.Exists(final) && !File.Exists(locked), "Validated copy still uses atomic publication with sharing retry");
            File.WriteAllText(Path.Combine(directory, "media.json"), JsonSerializer.Serialize(new { First = first, Second = second, Recording = recording,
                FirstMetadata = firstMetadata, SecondMetadata = metadata }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { timer.Stop(); window.Close(); }
    }
    internal static void Retain(string staging, string root)
    {
        Directory.CreateDirectory(root);
        foreach (var path in Directory.GetFiles(staging))
            if (Path.GetExtension(path) is ".mkv" or ".wav") MediaOwnership.Retain(path, Path.Combine(root, Path.GetFileName(path)));
        File.WriteAllText(Path.Combine(root, "video.txt"), File.ReadAllText(Path.Combine(staging, "video.txt")).Replace(staging.Replace('\\', '/'), root.Replace('\\', '/')));
    }
}
