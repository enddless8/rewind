namespace Replay;

internal static class RewindVideoExportTests
{
    internal static async Task Run(string directory, Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        List<string> original = ["-v", "error", "-f", "concat", "-safe", "0", "-i", "video.txt", "-i", "Microphone.wav",
            "-filter_complex", "[0:v]trim=start=0.75:duration=3.05,setpts=PTS-STARTPTS[v]", "-map", "[v]",
            "-c:v", "av1_nvenc", "-r", "60", "-t", "3.05", "-y", "clip.mkv"];
        var gpu = RewindVideoExport.Arguments(original, true).ToList();
        check(gpu.IndexOf("av1_cuvid") < gpu.IndexOf("-i") && gpu.Count(x => x == "-hwaccel") == 1 &&
            gpu[gpu.LastIndexOf("-c:v") + 1] == "av1_nvenc" && gpu[gpu.IndexOf("-filter_complex") + 1] == original[original.IndexOf("-filter_complex") + 1],
            "NVDEC is scoped to video input; encoder, trim/setpts and audio inputs are unchanged");
        check(RewindVideoExport.Arguments(original, false).SequenceEqual(new[] { "-xerror" }.Concat(original)), "Software retry removes all CUDA options without changing the export interval");
        var main = new RewindVideoExport.VideoFormat("av1", "Main", 1920, 1080, "yuv420p");
        check(RewindVideoExport.Eligible(main) && RewindVideoExport.Eligible(main with { PixelFormat = "yuv420p10le" }), "Known AV1 Main 8/10-bit formats still require runtime capability verification");
        foreach (var unsupported in new[] { main with { Codec = "hevc" }, main with { Profile = "High" }, main with { PixelFormat = "yuv444p" }, main with { Width = 0 } })
            check(!RewindVideoExport.Eligible(unsupported), "Unknown codec/profile/chroma/geometry is not assumed NVDEC-capable");
        check(RewindVideoExport.ParseFormat("{\"streams\":[]}") == null && !RewindVideoExport.Eligible(null), "Missing stream properties safely disable optional hardware routing");

        foreach (var failure in new[] { "exit", "stderr", "timeout", "validation" })
        {
            var output = Path.Combine(directory, failure + ".mkv");
            int attempts = 0, probes = 0, verified = 0;
            var exporter = new RewindVideoExport(supportOverride: _ => { probes++; return Task.FromResult(true); }, run: async (_, arguments, _) =>
            {
                await Task.Yield(); attempts++;
                var hardware = arguments.Contains("av1_cuvid");
                if (!hardware) check(!File.Exists(output), "Partial hardware output is removed before software retry: " + failure);
                File.WriteAllText(output, hardware ? "partial" : "complete");
                if (hardware && failure == "timeout") throw new TimeoutException("test GPU timeout");
                return hardware && failure == "exit" ? (1, "", "test CUDA failure") : hardware && failure == "stderr" ? (0, "", "test corrupt frame") : (0, "", "");
            });
            async Task Verify()
            {
                await Task.Yield(); verified++;
                if (failure == "validation" && File.ReadAllText(output) == "partial") throw new IOException("test invalid hardware media");
            }
            var result = await exporter.Run("av1_nvenc", "closed.mkv", original, output, Verify);
            check(result is { Decoder: "software-default", SoftwareFallback: true } && attempts == 2 && File.ReadAllText(output) == "complete",
                "Hardware failure retries the identical export once and validates software output: " + failure);
            File.Delete(output);
            await exporter.Run("av1_nvenc", "another-closed.mkv", original, output, Verify);
            check(attempts == 3 && probes == 1, "Runtime hardware failure is not retried on every Save in this session: " + failure);
        }

        foreach (var encoder in new[] { "hevc_nvenc", "h264_nvenc", "av1_nvenc" })
        {
            int attempts = 0, probes = 0;
            var exporter = new RewindVideoExport(supportOverride: _ => { probes++; return Task.FromResult(false); }, run: (_, args, _) =>
            { attempts++; check(!args.Contains("av1_cuvid"), "Unavailable hardware/non-AV1 uses unchanged software decoding"); return Task.FromResult((0, "", "")); });
            await exporter.Run(encoder, "closed.mkv", original, "unused.mkv", () => Task.CompletedTask);
            check(attempts == 1 && probes == (encoder == "av1_nvenc" ? 1 : 0), "Only AV1 saves request a hardware capability check: " + encoder);
        }
        var failedProbe = new RewindVideoExport(supportOverride: _ => throw new IOException("test missing driver"), run: (_, _, _) => Task.FromResult((0, "", "")));
        check((await failedProbe.Run("av1_nvenc", "sample", original, "unused", () => Task.CompletedTask)).Decoder == "software-default", "Capability exceptions preserve software export");
        int unavailableChecks = 0;
        var unavailable = new RewindVideoExport(supportOverride: _ => { unavailableChecks++; return Task.FromResult(false); }, run: (_, _, _) => Task.FromResult((0, "", "")));
        await unavailable.Run("av1_nvenc", "sample", original, "unused", () => Task.CompletedTask);
        await unavailable.Run("av1_nvenc", "sample", original, "unused", () => Task.CompletedTask);
        check(unavailableChecks == 1, "Unavailable optional acceleration is cached inside the capture session, without probing every Save");
        int cancelledAttempts = 0;
        var cancelled = new RewindVideoExport(supportOverride: _ => Task.FromResult(true), run: (_, _, _) => { cancelledAttempts++; throw new OperationCanceledException(); });
        try { await cancelled.Run("av1_nvenc", "sample", original, "unused", () => Task.CompletedTask); check(false, "Cancelled export must not publish or retry"); }
        catch (OperationCanceledException) { check(cancelledAttempts == 1, "Cancellation does not start a software retry"); }
        int rejected = 0;
        var allFailed = new RewindVideoExport(supportOverride: _ => Task.FromResult(true), run: (_, _, _) => { rejected++; return Task.FromResult((1, "", "test exporter failure")); });
        try { await allFailed.Run("av1_nvenc", "sample", original, Path.Combine(directory, "failed.mkv"), () => { check(false, "Failed export cannot be validated"); return Task.CompletedTask; }); check(false, "Both failures must reach Save rollback"); }
        catch (IOException) { check(rejected == 2, "Both decoder paths failing propagates failure for existing cutoff rollback"); }
        check(original[0] == "-v" && !original.Contains("av1_cuvid"), "Argument construction never mutates the caller's export snapshot");
    }
}
