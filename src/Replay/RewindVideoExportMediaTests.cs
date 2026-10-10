using System.Reflection;
using System.Text.Json;

namespace Replay;

// Explicit native/GPU suite, separate from the deterministic regression suite.
// Copies test-owned snapshots only; never changes the user's settings or queue.
internal static class RewindVideoExportMediaTests
{
    internal static async Task Run(string directory, Action<bool, string> check)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        using var scene = new TestScene();
        var settings = new Settings { CaptureMode = "Desktop", AudioMode = "System", MicrophoneId = "off", Discord = false,
            Codec = "AV1", HideDisplayBorder = true, Height = 720, Fps = 30, BufferSeconds = 30,
            Quality = 27, MaxBitrateMbps = 15, OutputDirectory = Path.Combine(directory, "output"),
            BufferDirectory = Path.Combine(directory, "buffer"), GroupByApplication = false };
        await using var recorder = new Recorder(settings, null, Native.Displays().First(d => d.Primary));
        var call = 0; var snapshot = ""; List<string>? reference = null; var fallbackCalls = 0;
        RewindVideoExport Exporter(bool failHardware) => new(supportOverride: failHardware ? _ => Task.FromResult(true) : null,
            run: async (tool, arguments, timeout) =>
            {
                var args = arguments.ToList(); var hardware = args.Contains("av1_cuvid"); var output = args[^1];
                if (reference == null)
                {
                    var staging = Path.GetDirectoryName(output)!; snapshot = Path.Combine(directory, "snapshot-" + call);
                    Directory.CreateDirectory(snapshot);
                    foreach (var file in Directory.GetFiles(staging))
                        File.Copy(file, Path.Combine(snapshot, Path.GetFileName(file)), true);
                    var manifest = Path.Combine(snapshot, "video.txt");
                    File.WriteAllText(manifest, File.ReadAllText(manifest).Replace(staging.Replace('\\', '/'), snapshot.Replace('\\', '/')));
                    reference = args.Select(a => a.Replace(staging, snapshot)).ToList();
                }
                if (failHardware && hardware)
                {
                    fallbackCalls++;
                    // Real FFmpeg input failure, plus a partial result that must be removed.
                    args[args.IndexOf("av1_cuvid")] = "missing_nvdec_test_decoder";
                    var failed = await MediaTools.Run(tool, args, timeout, bindLifetime: true);
                    File.WriteAllText(output, "partial hardware output");
                    return failed;
                }
                if (failHardware) check(!File.Exists(output), "Real fallback removes partial output before software FFmpeg starts");
                return await MediaTools.Run(tool, args, timeout, bindLifetime: true);
            });
        recorder.VideoExport = Exporter(false);
        await recorder.Start(); await recorder.StartRecording();
        var producer = (System.Diagnostics.Process)typeof(Recorder).GetField("video", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(recorder)!;
        var producerId = producer.Id;
        await Task.Delay(32500);
        var first = await recorder.Save();
        var firstReference = reference!; var firstSnapshot = snapshot;
        var firstMetadata = Metadata(first);
        check(firstMetadata.GetProperty("ExportDecoder").GetString() == "nvdec-av1", "Real AV1 Save uses NVDEC after actual-segment capability probe");
        var cutoff = firstMetadata.GetProperty("AcceptedSaveBoundary").GetDouble();
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, cutoff + 15 - Native.Clock)));
        call++; reference = null; recorder.VideoExport = Exporter(true);
        var second = await recorder.Save();
        var secondMetadata = Metadata(second);
        check(secondMetadata.GetProperty("ActualStart").GetDouble() >= cutoff && Math.Abs(second.Duration - 15) < .15 &&
            secondMetadata.GetProperty("PreviousSaveCutoff").GetDouble() == cutoff, "30-second buffer: second Save at +15 seconds has no pre-save history, including fallback");
        check(fallbackCalls == 1 && secondMetadata.GetProperty("HardwareDecodeFallback").GetBoolean(), "Actual FFmpeg hardware failure exports playable software result from the original snapshot");
        check(recorder.Running && producer.Id == producerId && recorder.Recording.State == RecordingState.Recording,
            "NVDEC and fallback preserve the same producer and concurrent conventional recording");
        // Slow independent software/reference decoding happens after both acceptances,
        // so verification itself cannot move the second Save past the +15s boundary.
        await Compare(first, firstReference, firstSnapshot, settings.Fps, check);
        await Compare(second, reference!, snapshot, settings.Fps, check);

        var savedCutoff = secondMetadata.GetProperty("AcceptedSaveBoundary").GetDouble();
        await Task.Delay(2300);
        recorder.VideoExport = new(supportOverride: _ => Task.FromResult(true), run: (_, _, _) => Task.FromResult((1, "", "injected both-path failure")));
        var before = Directory.GetFiles(settings.OutputDirectory, "*.mkv").Length;
        try { await recorder.Save(); check(false, "Both failed paths cannot publish"); } catch (IOException) { }
        var cutoffField = typeof(Recorder).GetField("rewindSaveCutoff", BindingFlags.Instance | BindingFlags.NonPublic)!;
        check((double)cutoffField.GetValue(recorder)! == savedCutoff && Directory.GetFiles(settings.OutputDirectory, "*.mkv").Length == before,
            "Both export failures roll back cutoff and publish no partial media");
        recorder.VideoExport = new(hardwareEnabled: false);
        var retry = await recorder.Save();
        check(Metadata(retry).GetProperty("PreviousSaveCutoff").GetDouble() == savedCutoff && retry.Duration > 2,
            "Retry after both failures retains unconsumed history");
        await Decode(retry.Path);
        var recording = await recorder.StopRecording();
        check(recording != null && recording.Duration > first.Duration + second.Duration, "Concurrent conventional recording includes uninterrupted history across Rewind saves");
        await Decode(recording!.Path);
        await CheckRealRetries(reference!, snapshot, check);
        await CheckOtherFormats(directory, check);
        recorder.VideoExport = new(hardwareEnabled: false);
        var gapBaseline = await recorder.Save(); await Task.Delay(2300);
        call++; reference = null; recorder.VideoExport = Exporter(false);
        await recorder.SimulateAccessLossForTest(); await Task.Delay(350); await recorder.RestartCapture(); await Task.Delay(3300);
        var recovered = await recorder.Save();
        await Compare(recovered, reference!, snapshot, settings.Fps, check);
        check(Metadata(recovered).GetProperty("RemovedCaptureGapSeconds").GetDouble() > 0 &&
            Metadata(recovered).GetProperty("ActualStart").GetDouble() >= Metadata(gapBaseline).GetProperty("AcceptedSaveBoundary").GetDouble(),
            "AV1 NVDEC stitches capture-recovery intervals with the same video/audio gap removal and consumed-history cutoff");
        File.WriteAllText(Path.Combine(directory, "media.json"), JsonSerializer.Serialize(new { First = first, Second = second, Retry = retry, Recording = recording, GapBaseline = gapBaseline, Recovered = recovered }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class TestScene : IDisposable
    {
        private readonly System.Windows.Window window;
        private readonly System.Windows.Threading.DispatcherTimer timer;
        internal TestScene()
        {
            var label = new System.Windows.Controls.TextBlock { FontSize = 32, HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center, Foreground = System.Windows.Media.Brushes.White };
            window = new() { Title = "Проверка экспорта Rewind", Width = 600, Height = 340, Content = label,
                Topmost = true, ShowActivated = false, WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen };
            var frame = 0;
            timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (_, _) => {
                label.Text = "Проверка границ AV1 · " + ++frame;
                window.Background = frame % 20 < 10 ? System.Windows.Media.Brushes.DarkSlateBlue : System.Windows.Media.Brushes.DarkGreen;
            };
            window.Show(); window.Hide(); window.Show(); timer.Start();
        }
        public void Dispose() { timer.Stop(); window.Close(); }
    }

    private static JsonElement Metadata(SavedClip clip) => JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.ChangeExtension(clip.Path, ".json")));
    private static async Task<(int Code, string Output, string Error)> Run(IEnumerable<string> args)
    {
        var result = await MediaTools.Run("ffmpeg", args, 180, bindLifetime: true);
        if (result.Code != 0 || !string.IsNullOrWhiteSpace(result.Error)) throw new IOException("Native media test failed: " + result.Error);
        return result;
    }
    private static async Task Decode(string path) => await Run(["-v", "error", "-xerror", "-i", path, "-map", "0", "-f", "null", "-"]);
    private static string[] Frames(string output) => output.Split('\n').Where(line => !line.StartsWith('#') && !string.IsNullOrWhiteSpace(line)).Select(line => line.Trim()).ToArray();
    private static async Task CheckRealRetries(List<string> reference, string snapshot, Action<bool, string> check)
    {
        foreach (var failure in new[] { "timeout", "validation" })
        {
            var args = reference.ToList(); var hw = args.IndexOf("-hwaccel");
            if (hw >= 0) args.RemoveRange(hw, 6);
            var output = Path.Combine(snapshot, "retry-" + failure + ".mkv"); args[^1] = output;
            var attempts = 0; var validations = 0;
            var exporter = new RewindVideoExport(supportOverride: _ => Task.FromResult(true), run: async (tool, arguments, timeout) =>
            {
                attempts++; var command = arguments.ToList();
                if (command.Contains("av1_cuvid") && failure == "timeout")
                {
                    command.Insert(command.IndexOf("-i"), "-re");
                    return await MediaTools.Run(tool, command, 1, bindLifetime: true);
                }
                if (!command.Contains("av1_cuvid")) check(!File.Exists(output), "Real " + failure + " removes rejected output before software retry");
                return await MediaTools.Run(tool, command, timeout, bindLifetime: true);
            });
            var before = System.Diagnostics.Process.GetProcessesByName("ffmpeg").Select(p => { using (p) return p.Id; }).ToHashSet();
            var result = await exporter.Run("av1_nvenc", Path.Combine(snapshot, "owned-000000000.mkv"), args, output, async () =>
            {
                if (++validations == 1 && failure == "validation") throw new IOException("injected rejection of otherwise complete hardware media");
                var probe = await MediaTools.Run("ffprobe", ["-v", "error", "-show_streams", "-of", "json", output]);
                if (probe.Code != 0 || !string.IsNullOrWhiteSpace(probe.Error)) throw new IOException("Retry media validation failed");
            });
            check(result.SoftwareFallback && attempts == 2, "Real " + failure + " retries the same video/PCM once");
            await Decode(output);
            var after = System.Diagnostics.Process.GetProcessesByName("ffmpeg").Select(p => { using (p) return p.Id; }).ToHashSet();
            check(after.IsSubsetOf(before), "Real " + failure + " reaps FFmpeg before cleanup; no child process leaks");
        }
    }
    private static async Task CheckOtherFormats(string directory, Action<bool, string> check)
    {
        foreach (var encoder in new[] { "h264_nvenc", "hevc_nvenc", "av1_nvenc" })
        {
            var input = Path.Combine(directory, encoder + "-source.mkv");
            List<string> generate = ["-v", "error", "-f", "lavfi", "-i", "testsrc2=s=320x180:r=30", "-t", "3.5", "-c:v", encoder, "-bf", "0", "-g", "60"];
            if (encoder == "av1_nvenc") generate.AddRange(["-pix_fmt", "p010le"]);
            generate.AddRange(["-y", input]); await Run(generate);
            var output = Path.Combine(directory, encoder + "-trimmed.mkv");
            const string trim = "trim=start=0.73:duration=1.02,setpts=PTS-STARTPTS";
            List<string> args = ["-v", "error", "-i", input, "-vf", trim, "-c:v", encoder, "-bf", "0", "-r", "30", "-t", "1.02", "-y", output];
            var exporter = encoder == "av1_nvenc" ? new RewindVideoExport() : new RewindVideoExport(supportOverride: _ => throw new Exception("Non-AV1 must not probe CUDA"));
            var result = await exporter.Run(encoder, input, args, output, async () => {
                var probe = await MediaTools.Run("ffprobe", ["-v", "error", "-show_streams", "-of", "json", output]);
                if (probe.Code != 0) throw new IOException("Native codec output did not validate");
                if (encoder == "av1_nvenc" && JsonSerializer.Deserialize<JsonElement>(probe.Output).GetProperty("streams")[0].GetProperty("pix_fmt").GetString() != "yuv420p10le")
                    throw new IOException("10-bit AV1 export lost bit depth");
            });
            check(result.Decoder == (encoder == "av1_nvenc" ? "nvdec-av1" : "software-default"), "Actual " + encoder + " uses expected decoder; AV1 10-bit has its own real capability probe");
            await Decode(output);
            if (encoder == "av1_nvenc")
            {
                List<string> raw = ["-v", "error", "-xerror", "-i", input, "-vf", trim + ",format=yuv420p10le", "-fps_mode", "passthrough", "-an", "-f", "framemd5", "-"];
                var software = await Run(raw); var hardware = RewindVideoExport.Arguments(raw, true).ToList();
                hardware[hardware.IndexOf("-vf") + 1] = trim + ",hwdownload,format=p010le,format=yuv420p10le";
                var decodedHardware = await Run(hardware);
                check(Frames(software.Output).SequenceEqual(Frames(decodedHardware.Output)), "AV1 Main 10-bit software/NVDEC partial-GOP raw frames and PTS are identical");
            }
        }
    }
    private static async Task Compare(SavedClip clip, List<string> reference, string snapshot, int fps, Action<bool, string> check)
    {
        // Exact same owned compressed frames and WAVs, independent software reference.
        var software = reference.ToList(); var hw = software.IndexOf("-hwaccel");
        if (hw >= 0) software.RemoveRange(hw, 6);
        var softwarePath = Path.Combine(snapshot, "reference-software.mkv"); software[^1] = softwarePath;
        await Run(software);
        const string properties = "stream=codec_type,codec_name,profile,width,height,pix_fmt,sample_aspect_ratio,color_range,color_space,color_transfer,color_primaries,r_frame_rate,avg_frame_rate,sample_rate,channels,channel_layout:stream_tags=title";
        var actualProbe = await MediaTools.Run("ffprobe", ["-v", "error", "-show_entries", properties, "-of", "json", clip.Path]);
        var expectedProbe = await MediaTools.Run("ffprobe", ["-v", "error", "-show_entries", properties, "-of", "json", softwarePath]);
        check(actualProbe.Code == 0 && expectedProbe.Code == 0 &&
            JsonSerializer.Deserialize<JsonElement>(actualProbe.Output).GetProperty("streams").ToString() == JsonSerializer.Deserialize<JsonElement>(expectedProbe.Output).GetProperty("streams").ToString(),
            "NVDEC preserves software export's codec, geometry, color, frame rate and named audio formats");
        var concat = software[software.IndexOf("-i") + 1];
        var videoFilter = software[software.IndexOf("-filter_complex") + 1].Split(';')[0].Replace("[0:v]", "").Replace("[v]", "");
        List<string> raw = ["-v", "error", "-xerror", "-f", "concat", "-safe", "0", "-i", concat, "-vf", videoFilter + ",format=yuv420p", "-fps_mode", "passthrough", "-an", "-f", "framemd5", "-"];
        var rawSoftware = await Run(raw);
        var rawHardware = RewindVideoExport.Arguments(raw, true).ToList();
        rawHardware[rawHardware.IndexOf("-vf") + 1] = videoFilter + ",hwdownload,format=nv12,format=yuv420p";
        var rawGpu = await Run(rawHardware);
        check(Frames(rawSoftware.Output).SequenceEqual(Frames(rawGpu.Output)), "Software/NVDEC trimmed decoded frames and PTS match exactly, including partial-GOP boundaries");
        File.WriteAllText(Path.Combine(snapshot, "trim-software.framemd5"), rawSoftware.Output);
        File.WriteAllText(Path.Combine(snapshot, "trim-nvdec.framemd5"), rawGpu.Output);
        foreach (var mapping in new[] { "0:v:0", "0:a" })
        {
            List<string> args = ["-v", "error", "-xerror", "-i", clip.Path, "-map", mapping, "-f", "framemd5", "-"];
            var actual = await Run(args); args[args.IndexOf("-i") + 1] = softwarePath; var expected = await Run(args);
            check(Frames(actual.Output).SequenceEqual(Frames(expected.Output)), "Published " + mapping + " decoded samples/PTS equal the same-snapshot software export");
            if (mapping == "0:v:0") check(Math.Abs(Frames(actual.Output).Length - clip.Duration * fps) <= 1.1,
                "Published video frame count matches the exact selected interval within one frame");
            File.WriteAllText(Path.Combine(snapshot, mapping == "0:v:0" ? "video.framemd5" : "audio.framemd5"), actual.Output);
        }
        await Decode(clip.Path);
        check(true, "Published MKV independently fully decodes all video/audio tracks without errors");
    }
}
