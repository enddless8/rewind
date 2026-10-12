using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Replay;

internal static class CompressionQueueChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Gate(CompressionQueue q) => typeof(CompressionQueue).GetField("gate", Private)!.GetValue(q)!;
    private static List<CompressionJob> Jobs(CompressionQueue q) => (List<CompressionJob>)typeof(CompressionQueue).GetField("jobs", Private)!.GetValue(q)!;
    private static void Persist(CompressionQueue q) => typeof(CompressionQueue).GetMethod("Persist", Private)!.Invoke(q, null);
    private static void Trim(CompressionQueue q) => typeof(CompressionQueue).GetMethod("TrimHistory", Private)!.Invoke(q, null);
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static CompressionJob[] Read(string path) => JsonSerializer.Deserialize<CompressionJob[]>(File.ReadAllText(path))!;
    private static bool Terminal(CompressionJob j) => j.State is "Completed" or "Failed" or "Cancelled";

    // Stop only this fixture's worker; use the production gate and persistence.
    // No production seam or default/private queue is constructed.
    private static async Task<CompressionQueue> Stopped(string path)
    {
        var q = new CompressionQueue(path);
        ((CancellationTokenSource)typeof(CompressionQueue).GetField("cancel", Private)!.GetValue(q)!).Cancel();
        await ((Task)typeof(CompressionQueue).GetField("worker", Private)!.GetValue(q)!).WaitAsync(TimeSpan.FromSeconds(10));
        return q;
    }

    internal static async Task<int> Run(string root, string? decoder = null)
    {
        if (Directory.EnumerateFileSystemEntries(root).Any()) throw new IOException("Use a new, empty fixture directory.");
        var privateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "Replay");
        Dictionary<string, string> PrivateFiles() => Directory.Exists(privateDirectory)
            ? Directory.GetFiles(privateDirectory, "*", SearchOption.AllDirectories).ToDictionary(p => p, Hash) : [];
        var privateBefore = PrivateFiles();
        var checks = new List<string>(); var measurements = new List<object>();
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks.Add(name); }
        string FileAt(string name) => Path.Combine(root, name);
        CompressionJob Job(int n, string state) => new() { Id = $"fixture-{n:D6}", State = state,
            Source = FileAt($"source-{n}.mkv"), Output = FileAt($"output-{n}.mp4"), Error = state == "Failed" ? "fixture failure" : null };
        string? error = null;
        try
        {
            Check(ClipFiles.DiagnosticDirectoryForTests == FileAt("diagnostics"), "Fault logging is isolated before any queue work");
            var largeFile = FileAt("large.json");
            File.WriteAllText(largeFile, JsonSerializer.Serialize(Enumerable.Range(0, 1000).Select(n => Job(n, "Completed"))));
            await using (var q = await Stopped(largeFile))
            {
                Check(q.Snapshot().Select(j => j.Id).SequenceEqual(Enumerable.Range(950, 50).Select(n => $"fixture-{n:D6}")),
                    "Loading 1,000 completed jobs retains exactly the latest 50 in enqueue order");
                lock (Gate(q)) Persist(q);
                Check(Read(largeFile).Select(j => j.Id).SequenceEqual(q.Snapshot().Select(j => j.Id)), "Memory and persisted history agree");
            }
            await using (var q = await Stopped(largeFile)) Check(q.Snapshot().Count == 50, "Retention survives reload");

            var mixedFile = FileAt("mixed.json");
            await using (var q = await Stopped(mixedFile))
            {
                var fixture = Enumerable.Range(0, 1000).Select(n => Job(n, (n % 5) switch
                { 0 => "Pending", 1 => "Running", 2 => "Completed", 3 => "Failed", _ => "Cancelled" })).ToArray();
                var expected = fixture.Where(j => !Terminal(j)).Concat(fixture.Where(Terminal).TakeLast(50)).Select(j => j.Id).ToHashSet();
                lock (Gate(q)) { Jobs(q).AddRange(fixture); Persist(q); }
                var snapshot = q.Snapshot();
                Check(snapshot.Count == 450 && snapshot.Count(Terminal) == 50, "Mixed terminal history is bounded, including Cancelled entries");
                Check(snapshot.Select(j => j.Id).SequenceEqual(fixture.Where(j => expected.Contains(j.Id)).Select(j => j.Id)), "Mixed retention preserves original ordering");
                Check(snapshot.Count(j => j.State == "Pending") == 200 && snapshot.Count(j => j.State == "Running") == 200,
                    "All pending and running jobs survive pruning");
                Check(Read(mixedFile).Select(j => (j.Id, j.State, j.Error)).SequenceEqual(snapshot.Select(j => (j.Id, j.State, j.Error))),
                    "Persistence retains identical mixed states and errors");
                snapshot[0].State = "Failed";
                Check(q.Snapshot()[0].State == "Pending", "Snapshots do not mutate live job state");
            }
            var terminalFile = FileAt("mixed-terminal-reload.json");
            var mixedTerminal = Read(mixedFile).Where(Terminal).ToArray();
            File.WriteAllText(terminalFile, JsonSerializer.Serialize(mixedTerminal));
            await using (var q = await Stopped(terminalFile))
                Check(q.Snapshot().Select(j => (j.Id, j.State, j.Error)).SequenceEqual(mixedTerminal.Select(j => (j.Id, j.State, j.Error))),
                    "Completed, Failed and Cancelled terminal metadata survive reload identically");

            var concurrentFile = FileAt("concurrent.json");
            await using (var q = await Stopped(concurrentFile))
            {
                lock (Gate(q)) Jobs(q).AddRange(Enumerable.Range(0, 25).Select(n => Job(n, n < 20 ? "Pending" : "Running")));
                using var done = new CancellationTokenSource();
                long snapshots = 0;
                var readers = Enumerable.Range(0, 4).Select(reader => Task.Run(() =>
                {
                    do
                    {
                        var s = q.Snapshot();
                        if (s.Count(Terminal) > 50 || s.Count(j => !Terminal(j)) != 25 || s.Select(j => j.Id).Distinct().Count() != s.Count)
                            throw new Exception("Concurrent snapshot lost active jobs or exceeded retention.");
                        _ = q.Status; Interlocked.Increment(ref snapshots); Thread.Yield();
                    } while (!done.IsCancellationRequested);
                })).ToArray();
                var watch = Stopwatch.StartNew();
                try
                {
                    await Task.WhenAll(Enumerable.Range(0, 4).Select(writer => Task.Run(() =>
                    {
                        for (var n = 0; n < 2500; n++)
                            lock (Gate(q))
                            {
                                var job = Job(1000 + writer * 2500 + n, "Running");
                                Jobs(q).Add(job);
                                job.State = (n % 3) switch { 0 => "Completed", 1 => "Failed", _ => "Cancelled" };
                                // Model simultaneous completions under the same gate
                                // and use the production transition-time trim. Real
                                // worker persistence is exercised by the native suite.
                                Trim(q);
                            }
                    })));
                }
                finally { done.Cancel(); await Task.WhenAll(readers); }
                Check(snapshots > 0 && q.Snapshot().Count == 75, "10,000 concurrent terminal transitions and snapshots retain only 50 terminal + 25 active jobs");
                lock (Gate(q)) Persist(q);
                Check(Read(concurrentFile).Select(j => (j.Id, j.State)).SequenceEqual(q.Snapshot().Select(j => (j.Id, j.State))),
                    "Persistence agrees with memory after concurrent transitions");
                measurements.Add(new { transitions = 10000, snapshots, seconds = watch.Elapsed.TotalSeconds, retained = q.Snapshot().Count });

                // Weak references prove pruned records are collectible, rather than
                // merely hidden from Snapshot or left in another live history list.
                var weak = new List<WeakReference>();
                void AddHistory(int start, int count)
                {
                    lock (Gate(q))
                    {
                        for (var n = start; n < start + count; n++)
                        {
                            var job = Job(n, "Completed") with { Error = new string('x', 4096) };
                            weak.Add(new WeakReference(job)); Jobs(q).Add(job);
                        }
                        Persist(q);
                    }
                }
                AddHistory(20000, 1000);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                Check(weak.Count(w => w.IsAlive) <= 50, "Pruned terminal records are collectible; retained metadata does not grow with history");
                measurements.Add(new { seeded = weak.Count, liveRecords = weak.Count(w => w.IsAlive), retained = q.Snapshot().Count });
            }

            var blockedFile = FileAt("blocked.json");
            await using (var q = await Stopped(blockedFile))
            {
                Directory.CreateDirectory(blockedFile + ".tmp");
                lock (Gate(q))
                {
                    Jobs(q).AddRange(Enumerable.Range(0, 1000).Select(n => Job(n, "Completed")));
                    Jobs(q).Add(Job(1000, "Pending")); Jobs(q).Add(Job(1001, "Running"));
                    typeof(CompressionQueue).GetMethod("SaveStateSafely", Private)!.Invoke(q, null);
                }
                Check(q.Snapshot().Count == 52 && q.Snapshot().Count(Terminal) == 50,
                    "Persistence failure leaves bounded metadata and retains both active jobs");
                Check(File.Exists(FileAt("diagnostics/errors.log")), "Persistence failure is logged in isolated diagnostics");
                Directory.Delete(blockedFile + ".tmp"); // Empty test-owned blocker only.
                lock (Gate(q)) Persist(q);
                Check(Read(blockedFile).Length == 52, "Later persistence recovers after a fixture write failure");
            }

            // Actual FFmpeg/NVENC lifecycle, including failure, retry and recovery.
            var source = FileAt("original.mkv");
            var generated = await MediaTools.Run("ffmpeg", ["-v", "error", "-f", "lavfi", "-i", "testsrc2=s=256x144:r=30:d=2",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-f", "lavfi", "-i", "sine=frequency=880:duration=2",
                "-map", "0:v", "-map", "1:a", "-map", "2:a", "-c:v", "mpeg4", "-c:a", "aac",
                "-metadata:s:a:0", "title=System", "-metadata:s:a:1", "title=Microphone", source]);
            Check(generated.Code == 0, "Generated isolated MKV with video and two audio tracks");
            var sourceHash = Hash(source); var profile = new CompressionProfile { Codec = "h264", Height = 0, Fps = 0 };
            var nativeFile = FileAt("native.json");
            File.WriteAllText(nativeFile, JsonSerializer.Serialize(Enumerable.Range(0, 50).Select(n => Job(n, "Completed"))));
            string failedId, retryId, secondId;
            var finished = new List<CompressionJob>();
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            await using (var q = new CompressionQueue(nativeFile))
            {
                q.Started += _ => { if (!entered.IsSet) { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Fixture start gate"); } };
                q.Finished += j => { lock (finished) finished.Add(j); };
                failedId = q.Enqueue(source, profile);
                Check(entered.Wait(TimeSpan.FromSeconds(10)), "Owned worker reaches Running");
                Check(q.Snapshot().Single(j => j.Id == failedId).State == "Running", "Running job is retained beside full history");
                var temporary = FileAt(".compressing-" + failedId + ".mp4");
                using (var locked = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    release.Set();
                    await Until(() => { lock (finished) return finished.Any(j => j.Id == failedId); });
                    Check(q.Snapshot().Single(j => j.Id == failedId).State == "Failed" && Hash(source) == sourceHash,
                        "Real FFmpeg sharing failure preserves Failed state and original MKV");
                }
                File.Delete(temporary); // Only the deliberately locked fixture file.
                using var watching = new CancellationTokenSource();
                var observer = Task.Run(() =>
                {
                    while (!watching.IsCancellationRequested)
                    {
                        if (q.Snapshot().Count(Terminal) > 50) throw new Exception("Worker exposes over-limit history.");
                        _ = q.Status; Thread.Yield();
                    }
                });
                try
                {
                    retryId = q.Enqueue(source, profile); secondId = q.Enqueue(source, profile);
                    await Until(() => { lock (finished) return finished.Count == 3; });
                }
                finally { watching.Cancel(); await observer; }
                lock (finished)
                {
                    Check(finished.Select(j => j.Id).SequenceEqual(new[] { failedId, retryId, secondId }), "Failure and retries preserve FIFO ordering and Finished delivery");
                    Check(finished.Skip(1).All(j => j.State == "Completed" && j.Percent == 100 && j.CompressedBytes > 0), "Failed compression does not block subsequent successful jobs");
                }
                Check(q.Snapshot().Count == 50, "Real worker completion and failure trim history atomically");
            }
            var outputs = finished.Where(j => j.State == "Completed").Select(j => j.Output).ToDictionary(p => p, Hash);
            Check(Hash(source) == sourceHash && outputs.Count == 2, "Retry preserves original and creates distinct MP4 outputs");
            var independent = decoder ?? MediaTools.Find("ffmpeg");
            foreach (var output in outputs.Keys)
            {
                using var decode = MediaTools.Create("ffmpeg", ["-v", "error", "-xerror", "-i", output, "-map", "0", "-f", "null", "-"]);
                decode.StartInfo.FileName = independent;
                decode.Start(); var stdout = decode.StandardOutput.ReadToEndAsync(); var stderr = decode.StandardError.ReadToEndAsync();
                await decode.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); await stdout;
                Check(decode.ExitCode == 0 && string.IsNullOrWhiteSpace(await stderr), "Decoder accepts every MP4 stream: " + independent);
                var probe = await MediaTools.Run("ffprobe", ["-v", "error", "-show_entries", "stream=codec_type:stream_tags=handler_name", "-of", "json", output]);
                using var json = JsonDocument.Parse(probe.Output);
                var audio = json.RootElement.GetProperty("streams").EnumerateArray().Where(s => s.GetProperty("codec_type").GetString() == "audio").ToArray();
                Check(audio.Length == 2 && audio.Select(s => s.GetProperty("tags").GetProperty("handler_name").GetString()).SequenceEqual(new[] { "System", "Microphone" }),
                    "Compression preserves both named audio tracks");
            }

            // Running at restart must resume; an already published MP4 must not be overwritten.
            var recovered = finished.Last() with { State = "Running", Percent = 17 };
            var recoveryFile = FileAt("recovery.json");
            File.WriteAllText(recoveryFile, JsonSerializer.Serialize(Enumerable.Range(0, 1000).Select(n => Job(n, "Completed")).Append(recovered)));
            await using (var q = new CompressionQueue(recoveryFile))
            {
                await Until(() => q.Snapshot().Any(j => j.Id == recovered.Id && j.State == "Completed"));
                Check(q.Snapshot().Count == 50 && q.Snapshot().Last().Percent == 100, "Running recovery resumes with bounded history and validated existing publication");
            }
            await using (var q = await Stopped(recoveryFile))
            {
                lock (Gate(q)) { Jobs(q).AddRange(Enumerable.Range(30000, 50).Select(n => Job(n, "Completed"))); Persist(q); }
                Check(!q.Snapshot().Any(j => j.Id == recovered.Id), "Completed media job can leave metadata retention window");
            }
            Check(Hash(source) == sourceHash && outputs.All(p => File.Exists(p.Key) && Hash(p.Key) == p.Value),
                "Pruning and restart preserve original MKV and all generated MP4 bytes");

            // Deterministic cancellation at Started (before Compress) stays Pending,
            // then replays normally on restart, alongside a full terminal history.
            var cancelFile = FileAt("cancel.json");
            File.WriteAllText(cancelFile, JsonSerializer.Serialize(Enumerable.Range(0, 50).Select(n => Job(n, "Completed"))));
            using var cancelEntered = new ManualResetEventSlim(); using var cancelRelease = new ManualResetEventSlim();
            var cancelledQueue = new CompressionQueue(cancelFile);
            string cancelledId;
            var disposed = false;
            try
            {
                cancelledQueue.Started += _ => { cancelEntered.Set(); if (!cancelRelease.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Fixture cancellation gate"); };
                cancelledId = cancelledQueue.Enqueue(source, profile);
                Check(cancelEntered.Wait(TimeSpan.FromSeconds(10)), "Cancellation fixture reaches owned Running job");
                var disposing = cancelledQueue.DisposeAsync().AsTask(); cancelRelease.Set(); await disposing; disposed = true;
                Check(Read(cancelFile).Single(j => j.Id == cancelledId).State == "Pending" && Read(cancelFile).Length == 51,
                    "Shutdown cancellation preserves Pending retry and full terminal window");
            }
            finally { cancelRelease.Set(); if (!disposed) await cancelledQueue.DisposeAsync(); }
            await using (var q = new CompressionQueue(cancelFile))
            {
                await Until(() => q.Snapshot().Any(j => j.Id == cancelledId && j.State == "Completed"));
                Check(q.Snapshot().Count == 50 && Hash(source) == sourceHash, "Cancelled work retries on restart without source deletion");
            }
            Check(!Directory.GetFiles(root, ".compressing-*").Any(), "Owned compression staging files are cleaned up");
        }
        catch (Exception ex) { error = ex.ToString(); Console.Error.WriteLine(ex); }
        var privateAfter = PrivateFiles();
        if (privateBefore.Count != privateAfter.Count || privateBefore.Any(p => privateAfter.GetValueOrDefault(p.Key) != p.Value))
            error = (error ?? "") + "\nPrivate runtime files changed.";
        else checks.Add("Actual private runtime files and queue are byte-identical before/after");
        File.WriteAllText(FileAt("results.json"), JsonSerializer.Serialize(new { passed = error == null, count = checks.Count, checks, measurements,
            privateFiles = privateBefore.Count, error }, new JsonSerializerOptions { WriteIndented = true }));
        return error == null ? 0 : 1;
    }

    private static async Task Until(Func<bool> predicate)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException("Owned compression fixture did not finish.");
            await Task.Delay(25);
        }
    }
}
