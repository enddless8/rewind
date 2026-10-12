using System.IO;
using System.Text.Json;
using Replay;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var root = Path.GetFullPath(args[1]);
            var artifacts = Path.GetFullPath("artifacts");
            if (!root.StartsWith(artifacts + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Проверки разрешены только в artifacts.");
            Directory.CreateDirectory(root);
            ClipFiles.DiagnosticDirectoryForTests = Path.Combine(root, "diagnostics");
            if (args[0] is "recording" or "edges" or "recovery" or "live" or "live-long" or "live-long-candidate" or "phase1-media" or "concurrency")
            {
                var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                // The standalone harness has no App.xaml startup (and must not
                // create MainWindow/mutex/private queue). Reuse its font resources
                // so the real notification XAML can load without UI substitution.
                app.Resources["CoolveticaFont"] = new System.Windows.Media.FontFamily("pack://application:,,,/Rewind;component/assets/fonts/#Coolvetica");
                app.Resources["InterFont"] = new System.Windows.Media.FontFamily("pack://application:,,,/Rewind;component/assets/fonts/#Inter 28pt 28pt");
                app.Dispatcher.InvokeAsync(async () =>
                {
                    try { Environment.ExitCode = args[0] switch { "recording" => await RecordingTest.Run(root),
                        "edges" => await RecordingTest.RunEdges(root), "live" => await LiveRecording.Run(root), "live-long" => await LiveRecording.Run(root, 60),
                        "live-long-candidate" => await LiveRecording.Run(root, 60, parallelAudio: true),
                        "concurrency" => await ConcurrencyChecks.Run(root),
                        "phase1-media" => await Phase1Media(root), _ => await RecoveryTest.Run(root) }; }
                    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
                    finally { app.Shutdown(); }
                });
                app.Run(); return Environment.ExitCode;
            }
            if (args[0] == "compression-queue") return CompressionQueueChecks.Run(root, args.Length > 2 ? Path.GetFullPath(args[2]) : null).GetAwaiter().GetResult();
            if (args[0] == "process-profile") ProcessProfile.Run(root, args.Length > 2).GetAwaiter().GetResult();
            else if (args[0] == "process") MediaFailureChecks.Run(root).GetAwaiter().GetResult();
            else if (args[0] == "phase2-media")
            {
                List<object> checks = [];
                RewindPacketCopyTests.RunMedia(root, (ok, name) => { checks.Add(new { ok, name }); if (!ok) throw new Exception(name); }).GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = true, count = checks.Count, checks }));
            }
            else if (args[0] == "regress")
            {
                List<object> checks = [];
                RegressionTests.Run(root, (ok, name) => { checks.Add(new { ok, name }); if (!ok) throw new Exception(name); }).GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = true, count = checks.Count, checks }));
            }
            else
            {
                var manifest = JsonSerializer.Deserialize<RecordingManifest>(File.ReadAllText(Path.Combine(root, "recording.json")))!;
                if (args[0] == "finalize")
                {
                    var requestedMode = args.Length > 2 ? args[2] : "stage1";
                    // Stage 1 fixtures keep their original scheduling, including
                    // long sources. Research callers must explicitly opt in.
                    var parallelAudio = requestedMode == "parallel" || requestedMode == "inspect-copy" ||
                        requestedMode.StartsWith("parallel-", StringComparison.Ordinal);
                    var mode = requestedMode.StartsWith("parallel-", StringComparison.Ordinal) ? requestedMode[9..] : requestedMode;
                    bool injected = false;
                    FileStream? locked = null;
                    async Task<(int Code, string Output, string Error)> Run(string tool, IEnumerable<string> input, int timeout)
                    {
                        var list = input.ToList();
                        if (mode == "legacy" && list.Contains("trace_headers")) return (1, "", "Legacy benchmark requested");
                        if (mode == "no-space" && list.Contains("-c:a")) return (1, "", "No space left on device (test seam)");
                        if (!injected && (mode == "audio-fail" && list[^1].EndsWith(".mka") || mode == "mux-fail" && list.Contains("-copyts")))
                        { injected = true; return (1, "", "Injected parallel worker/remux failure"); }
                        if (mode == "locked" && locked == null && list.Any(v => v.StartsWith("setts=")))
                            locked = new FileStream(Path.Combine(root, "recording.mkv"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                        if (!injected && list.Any(v => v.StartsWith("setts=")) && mode is "fail" or "timeout" or "cancel")
                        {
                            injected = true;
                            if (mode == "cancel") throw new OperationCanceledException();
                            if (mode == "timeout") throw new TimeoutException("Injected bounded export timeout");
                            return (1, "", "Injected copy mux failure");
                        }
                        if (mode == "decode-fail" && list.Contains("av1_cuvid") && list.Contains("null")) return (1, "", "Injected hardware validation failure");
                        var result = await MediaTools.Run(tool, list, timeout, bindLifetime: true);
                        if (mode == "inspect-copy" && tool == "ffmpeg" && list.Contains("-copyts") && result.Code == 0)
                        {
                            // Retain only this unpublished test attempt to explain
                            // a rejected candidate. Production cleanup stays intact.
                            var attempt = Path.Combine(root, "rejected-attempt"); Directory.CreateDirectory(attempt);
                            var inputVideo = list[list.IndexOf("-i") + 1];
                            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(inputVideo)!))
                                File.Copy(file, Path.Combine(attempt, Path.GetFileName(file)));
                            File.Copy(list[^1], Path.Combine(attempt, "candidate.mkv"));
                            var before = Av1SegmentIndex.Read(inputVideo, cached: false);
                            var after = Av1SegmentIndex.Read(list[^1], cached: false);
                            File.WriteAllText(Path.Combine(attempt, "indices.json"), JsonSerializer.Serialize(new
                                { Before = before, After = after, Compatible = before.Compatible(after) }, new JsonSerializerOptions { WriteIndented = true }));
                        }
                        if (tool == "ffmpeg" && list.Any(v => v.StartsWith("setts=")) && mode == "corrupt")
                            using (var file = new FileStream(list[^1], FileMode.Open, FileAccess.Write)) file.SetLength(file.Length / 2);
                        if (tool == "ffmpeg" && list[^1].EndsWith(".mka") && mode == "audio-corrupt")
                            using (var file = new FileStream(list[^1], FileMode.Open, FileAccess.Write)) file.SetLength(file.Length / 2);
                        return result;
                    }
                    var stages = new List<object>();
                    SaveTiming.Observed += (_, stage, elapsed, details) => { lock (stages) stages.Add(new { stage, elapsed, details }); };
                    Environment.SetEnvironmentVariable("REWIND_SAVE_TIMINGS_FILE", Path.Combine(root, "timing.jsonl"));
                    using var timing = SaveTiming.Begin(Guid.NewGuid());
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        var clip = RecordingSession.FinalizeFiles(root, manifest, new RecordingPacketExport(Run, parallelAudio: parallelAudio)).GetAwaiter().GetResult();
                        File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = true, totalMs = watch.Elapsed.TotalMilliseconds, clip, stages }, new JsonSerializerOptions { WriteIndented = true }));
                        Console.WriteLine(clip.Path);
                    }
                    catch (OperationCanceledException) when (mode == "cancel")
                    {
                        File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = true, cancelled = true, stages }));
                    }
                    catch (IOException ex) when (mode is "locked" or "no-space")
                    {
                        File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = true, retained = true, failure = ex.Message, stages }));
                    }
                    finally { locked?.Dispose(); }
                }
                else if (args[0] == "plan")
                {
                    var segments = File.ReadLines(Path.Combine(root, "segments.jsonl")).Select(s => JsonSerializer.Deserialize<Segment>(s)!)
                        .Select(s => s with { Path = Path.Combine(root, Path.GetFileName(s.Path)) }).ToArray();
                    var decision = RecordingPacketExport.Inspect(RecordingPacketExport.Snapshot.Create(segments, manifest.StartedAt,
                        manifest.StoppedAt ?? segments[^1].End, manifest.Settings.Fps, []));
                    Console.WriteLine(JsonSerializer.Serialize(new { eligible = decision.Plan != null, decision.Reason, Frames = decision.Plan?.Frames }));
                }
                else if (args[0] == "recover")
                    Console.WriteLine(Recorder.RecoverRecording(root).GetAwaiter().GetResult().Path);
                else throw new ArgumentException("Неизвестная команда проверки.");
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static async Task<int> Phase1Media(string root)
    {
        List<object> checks = [];
        await RewindVideoExportMediaTests.Run(root, (ok, name) => { checks.Add(new { ok, name }); if (!ok) throw new Exception(name); });
        File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = true, count = checks.Count, checks }));
        return 0;
    }
}
