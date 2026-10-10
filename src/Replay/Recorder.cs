using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Replay;
public record Segment(string Path, double Start, double End);
public record SavedClip(string Path, double Duration, IReadOnlyList<string> Warnings);
public sealed partial class Recorder : IAsyncDisposable
{
    private readonly Settings settings;
    private readonly GameWindow? game;
    private readonly Display display;
    private readonly MicrophoneMuteControl microphoneMute;
    private readonly List<AudioTrack> tracks = new();
    private readonly SemaphoreSlim files = new(1, 1);
    private readonly Queue<string> logTail = new();
    private readonly List<string> warnings = new();
    private Process? video;
    private ProcessLifetime? lifetime;
    private Task? logTask, progressTask;
    private double origin = double.NaN;
    private double lastFrame = Native.Clock;
    private double videoTime;
    private bool stopping;
    private bool accessLost;
    public bool RecoverableCaptureLoss => accessLost && !Running;
    public bool PreserveBuffer { get; set; }
    private readonly List<(string Directory, double Origin)> epochs = new();
    private string videoDirectory = "";
    // Protected by files. A save consumes logical history, never the shared producer.
    private double rewindSaveCutoff = double.NegativeInfinity;
    private double? rewindRollbackCutoff;
    private double RewindHistoryStart => Math.Max(rewindSince, rewindSaveCutoff);
    public async Task RestartCapture()
    {
        if (Recording.State is RecordingState.Recording) await StopRecording("Захват прерван; запись завершена до восстановления.");
        await files.WaitAsync();
        try
        {
            if (Running) return;
            if (logTask != null) await logTask;
            if (progressTask != null) await progressTask;
            if (!double.IsNaN(origin)) epochs.Add((videoDirectory, origin));
            origin = double.NaN;
            lifetime?.Dispose(); video?.Dispose(); video = null;
            accessLost = false; Error = null;
            try { await StartVideo(); }
            catch
            {
                if (video is { HasExited: false }) { video.Kill(true); await video.WaitForExitAsync(); }
                if (logTask != null) await logTask;
                if (progressTask != null) await progressTask;
                accessLost = true; throw;
            }
        }
        finally { files.Release(); }
    }
    internal async Task SimulateAccessLossForTest()
    {
        accessLost = true;
        video!.Kill(true); await video.WaitForExitAsync();
        if (logTask != null) await logTask;
    }
    public string SessionDirectory { get; }
    public string Encoder { get; private set; } = "";
    public string CaptureBackend { get; private set; } = "WGC";
    public string GameName { get; }
    internal OverlayMonitor OverlayTarget => settings.CaptureMode == "Game"
        ? OverlayPlacement.Resolve(captureWindow: game?.Handle ?? 0) : OverlayPlacement.Resolve(display.Handle);
    public string? Error { get; private set; }
    public bool Running => video is { HasExited: false } && !stopping;
    public AudioTrack? Microphone => tracks.FirstOrDefault(x => x.Name == "Microphone");
    public double AvailableSeconds { get; private set; }
    // Availability only: completed media after the logical checkpoint.
    public bool HasReadableBufferData
    {
        get
        {
            if (!files.Wait(0)) return false;
            try
            {
                var directories = epochs.Select(e => (e.Directory, e.Origin));
                if (!double.IsNaN(origin)) directories = directories.Append((videoDirectory, origin));
                if (!RewindEnabled) return false;
                foreach (var segment in directories.SelectMany(e => ReadSegments(e.Directory).Select(s => s with { Start = s.Start + e.Origin, End = s.End + e.Origin })).Where(s => s.End > Math.Max(s.Start, Math.Max(RewindHistoryStart, Native.Clock - settings.BufferSeconds))))
                {
                    try
                    {
                        using var stream = new FileStream(segment.Path, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete);
                        if (stream.Length > 0) return true;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                return false;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (FormatException) { return false; }
            catch (OverflowException) { return false; }
            finally { files.Release(); }
        }
    }
    public string? Warning => Error ?? tracks.Select(x => x.Health is { } h ? $"{x.Name}: {h}" : null).FirstOrDefault(x => x != null)
        ?? (game != null && settings.CaptureMode == "Game" && Native.IsIconic(game.Handle) ? "Игра свёрнута: новые кадры могут не поступать" : null)
        ?? (Running && Native.Clock - lastFrame > 5 ? "Нет новых видеокадров больше 5 секунд" : null)
        ?? (Microphone is { } mic && mic.SignalSilenceSeconds > settings.SilenceWarningSeconds ? $"Микрофон: нет сигнала {mic.SignalSilenceSeconds:F0} с" : null)
        ?? warnings.FirstOrDefault();

    public Recorder(Settings settings, GameWindow? game, Display display, MicrophoneMuteControl? microphoneMute = null)
    {
        this.microphoneMute = microphoneMute ?? new();
        this.settings = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!; this.game = game; this.display = display;
        GameName = ClipFiles.ApplicationName(settings, game);
        SessionDirectory = Path.Combine(settings.BufferDirectory, Guid.NewGuid().ToString("N"));
    }
    public async Task Start()
    {
        settings.Validate();
        if (settings.CaptureMode == "Game" && (game == null || !Native.IsWindow(game.Handle))) throw new InvalidOperationException("Выбери запущенную игру.");
        if (settings.AudioMode == "Game" && game == null) throw new InvalidOperationException("Для звука игры выбери её окно или переключи звук на системный.");
        try
        {
            Directory.CreateDirectory(SessionDirectory);
            using var probe = new FileStream(Path.Combine(SessionDirectory, ".write-test"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            probe.WriteByte(0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new IOException($"Папка буфера недоступна для записи: {settings.BufferDirectory}. Выбери другую папку.", ex); }
        Encoder = await MediaTools.SelectEncoder(settings.Codec);
        File.WriteAllText(Path.Combine(SessionDirectory, "session.json"), JsonSerializer.Serialize(new { GameName, settings, Started = DateTimeOffset.Now }));
        try
        {
            await AddAudio(new(settings.AudioMode == "Game" ? "Game" : "System", false, null, settings.AudioMode == "Game" ? game!.ProcessId : null, settings.BufferSeconds + 20, settings.SilenceDb));
            if (settings.MicrophoneId != "off") await AddAudio(new("Microphone", true, settings.MicrophoneId, null, settings.BufferSeconds + 20, settings.SilenceDb, microphoneMute) { MicrophoneChannelPreference = settings.MicrophoneChannelMode });
            if (settings.Discord)
            {
                if (Native.DiscordProcess() is { } pid) await AddAudio(new("Discord", false, null, pid, settings.BufferSeconds + 20, settings.SilenceDb));
                else warnings.Add("Discord не запущен: отдельная дорожка недоступна до перезапуска записи");
            }
            await StartVideo();
        }
        catch { await Stop(); throw; }
    }
    private async Task StartVideo()
    {
        var currentDisplay = CaptureSource.CurrentDisplay(display);
        videoDirectory = Path.Combine(SessionDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(videoDirectory);
        origin = double.NaN; videoTime = 0;
            // gfxcapture scales on the GPU. NVENC accepts the resulting D3D11 BGRA textures directly.
            var width = settings.Height * 16 / 9; width -= width % 2;
            var capture = CaptureGraph.Build(settings.CaptureMode, settings.HideDisplayBorder, settings.Fps, game, currentDisplay, width, settings.Height);
            var graph = capture.Graph; var deviceArgs = capture.DeviceArgs;
            CaptureBackend = capture.Backend;
            List<string> args = ["-hide_banner", "-nostats", "-debug_ts", "-stats_period", "0.2", "-progress", "pipe:1", "-filter_complex", graph, "-map", "[v]", "-an", "-c:v", Encoder,
                "-preset", "p4", "-tune", "hq", "-rc", "vbr", "-cq", settings.Quality.ToString(), "-b:v", "0", "-maxrate", $"{settings.MaxBitrateMbps}M", "-bufsize", $"{settings.MaxBitrateMbps * 2}M",
                "-bf", "0", "-g", (settings.Fps * 2).ToString(), "-force_key_frames", "expr:gte(t,n_forced*2)", "-fps_mode", "cfr", "-r", settings.Fps.ToString(),
                "-f", "segment", "-segment_time", "2", "-segment_format", "matroska", "-reset_timestamps", "1", "-segment_list", Path.Combine(videoDirectory, "segments.csv"), "-segment_list_size", (settings.BufferSeconds / 2 + 32).ToString(), "-segment_list_type", "csv", Path.Combine(videoDirectory, "part-%09d.mkv")];
            args.InsertRange(0, deviceArgs);
            video = MediaTools.Create("ffmpeg", args); video.Start(); lifetime = new ProcessLifetime(video);
            logTask = ReadLog(); progressTask = ReadProgress();
            var deadline = Native.Clock + 12;
            while (double.IsNaN(origin) && !video.HasExited && Native.Clock < deadline) await Task.Delay(50);
            if (video.HasExited || double.IsNaN(origin)) throw new InvalidOperationException("Захват не запустился. " + Tail());
    }
    private async Task AddAudio(AudioTrack track)
    {
        track.Gain = track.Name == "Microphone" ? settings.MicrophoneGain : track.Name is "Game" or "System" ? settings.SystemVolume / 100 : 1;
        try { if (!RewindEnabled) track.SetRetention(2); await track.Start(); tracks.Add(track); if (track.ChannelModeFeedback is { } feedback) warnings.Add(feedback); }
        catch (Exception ex) { track.Dispose(); warnings.Add($"{track.Name}: запись недоступна — {ex.Message}"); }
    }
    private async Task ReadLog()
    {
        while (await video!.StandardError.ReadLineAsync() is { } line)
        {
            if (CaptureRecovery.IsAccessLost(line)) accessLost = true;
            var match = Regex.Match(line, @"filter_raw -> pts:.*?pts_time:([\d.]+)");
            if (match.Success && double.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var pts))
            {
                lastFrame = Native.Clock;
                // fps buffers one frame. Estimate the shared QPC epoch before encode/mux latency.
                // A user-visible offset remains available for device-specific calibration.
                if (pts < 2) { var estimate = Native.Clock - pts - 1.0 / settings.Fps; origin = double.IsNaN(origin) ? estimate : Math.Min(origin, estimate); }
            }
            if (!line.Contains(" -> ") && !line.Contains(" <- ") && !line.Contains("exact:"))
                lock (logTail) { logTail.Enqueue(line); while (logTail.Count > 30) logTail.Dequeue(); }
        }
        if (!stopping)
        {
            Error = accessLost ? "Захват дисплея потерян при смене состояния экрана (DXGI)." : "Процесс записи остановился. Подробности сохранены в журнале Rewind.";
            ClipFiles.Log("capture", new IOException(FullTail()), SessionDirectory);
        }
        try { File.WriteAllText(Path.Combine(videoDirectory, "ffmpeg.log"), FullTail()); } catch { }
    }
    private async Task ReadProgress()
    {
        while (await video!.StandardOutput.ReadLineAsync() is { } line)
            if (line.StartsWith("out_time_us=") && long.TryParse(line[12..], out var us)) videoTime = us / 1000000.0;
    }
    private string Tail() { lock (logTail) return string.Join("\n", logTail.TakeLast(10)); }
    private string FullTail() { lock (logTail) return string.Join("\n", logTail); }
    public static List<Segment> ReadSegments(string directory)
    {
        var file = Path.Combine(directory, "segments.csv");
        if (!File.Exists(file)) return [];
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        List<Segment> result = [];
        while (reader.ReadLine() is { } line)
        {
            var match = Regex.Match(line, "^(.*),([0-9.]+),([0-9.]+)$");
            if (!match.Success) continue;
            var name = match.Groups[1].Value.Trim('"').Replace("\"\"", "\"");
            var path = Path.Combine(directory, Path.GetFileName(name));
            if (File.Exists(path)) result.Add(new(path, double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture)));
        }
        return result;
    }
    private async Task<List<Segment>> ReadSegmentsWithRetry()
    {
        List<Segment> result = [];
        await ClipFiles.RetrySharing(() =>
        {
            result = epochs.SelectMany(e => ReadSegments(e.Directory).Select(s => s with { Start = s.Start + e.Origin, End = s.End + e.Origin })).ToList();
            if (!double.IsNaN(origin)) result.AddRange(ReadSegments(videoDirectory).Select(s => s with { Start = s.Start + origin, End = s.End + origin }));
        });
        return result;
    }
    public async Task Maintain()
    {
        if (!await files.WaitAsync(0)) return;
        bool lowDisk = false;
        try
        {
            var segments = await ReadSegmentsWithRetry();
            recordingSession?.Accept(segments);
            var bytes = segments.Sum(s => new FileInfo(s.Path).Length);
            foreach (var segment in segments.ToArray())
                if (!RewindEnabled || segment.End <= Math.Max(rewindSince, rewindRollbackCutoff ?? rewindSaveCutoff) || segment.End < Native.Clock - settings.BufferSeconds - 2 || bytes > (RewindByteLimitForTest ?? settings.MaxBufferMegabytes * 1024L * 1024))
                { bytes -= new FileInfo(segment.Path).Length; File.Delete(segment.Path); segments.Remove(segment); }
            // Empty completed epochs no longer belong to the ring; keep the active writer's directory.
            foreach (var epoch in epochs.ToArray())
                if (!segments.Any(s => Path.GetDirectoryName(s.Path) == epoch.Directory))
                {
                    try { Directory.Delete(epoch.Directory, true); epochs.Remove(epoch); } catch { }
                }
            AvailableSeconds = RewindEnabled ? Math.Min(settings.BufferSeconds, segments.Sum(s => Math.Max(0, s.End - Math.Max(s.Start, Math.Max(RewindHistoryStart, Native.Clock - settings.BufferSeconds))))) : 0;
            lowDisk = new DriveInfo(Path.GetPathRoot(SessionDirectory)!).AvailableFreeSpace < 256L * 1024 * 1024;
        }
        finally { files.Release(); }
        if (lowDisk) { Error = "Мало места на диске буфера. Запись остановлена."; await Stop(); }
    }
    public async Task<SavedClip> Save()
    {
        if (!RewindEnabled) throw new InvalidOperationException("Буфер Rewind выключен.");
        if (!await exports.WaitAsync(0)) throw new InvalidOperationException("Предыдущий клип ещё сохраняется. Повтори через секунду.");
        SaveTiming.Current?.Mark("export-guard-acquired");
        bool locked = false;
        string? staging = null;
        bool readyToPublish = false, published = false;
        bool checkpointAccepted = false;
        double previousCutoff = double.NegativeInfinity, requested = 0, requestedEpoch = rewindSince;
        var stage = "read-buffer";
        try
        {
            if (double.IsNaN(origin) && epochs.Count == 0) throw new InvalidOperationException("Буфер ещё не готов.");
            requested = Native.Clock;
            SaveTiming.Current?.Mark("history-boundary-frozen", new { Requested = requested });
            // The wait never holds the metadata semaphore. Active recording keeps acquiring closed segments.
            var deadline = Native.Clock + 4;
            List<Segment> segments;
            do
            {
                await files.WaitAsync(); locked = true;
                segments = await ReadSegmentsWithRetry();
                if (segments.Count > 0 && segments[^1].End >= requested || !Running || Native.Clock >= deadline) break;
                locked = false; files.Release(); await Task.Delay(80);
            } while (true);
            SaveTiming.Current?.Mark("closed-segment-ready", new { Count = segments.Count });
            if (!RewindEnabled || rewindSince != requestedEpoch) throw new InvalidOperationException("Состояние буфера Rewind изменилось. Повтори сохранение.");
            if (segments.Count == 0) throw new InvalidOperationException("Ещё нет завершённых сегментов. Дай буферу 2–3 секунды.");
            var end = Math.Min(requested, segments[^1].End);
            var lower = Math.Max(requested - settings.BufferSeconds, RewindHistoryStart);
            var selected = segments.Where(x => x.End > lower && x.Start < end).ToList();
            if (selected.Count == 0) throw new InvalidOperationException("Нет доступного видео для клипа.");
            var rawStart = selected[0].Start;
            var intervals = selected.Select(s => s with { Start = Math.Max(s.Start, lower), End = Math.Min(s.End, end) }).ToList();
            var start = intervals[0].Start;
            var duration = intervals.Sum(s => s.End - s.Start);
            SaveTiming.Current?.Mark("segment-selection-completed", new { Count = selected.Count, Duration = duration, Start = start, End = end, RawStart = rawStart, Encoder, settings.Height, settings.Fps });
            var clipWarnings = new List<string>(warnings);
            if (requested - end > 0.5) clipWarnings.Add($"Последние {requested - end:F1} с недоступны: нет завершённого видеосегмента");
            if (Error != null) clipWarnings.Add(Error);
            var folder = ClipFiles.OutputFolder(settings.OutputDirectory, settings.GroupByApplication, GameName); Directory.CreateDirectory(folder);
            var id = ClipFiles.NewId();
            staging = Path.Combine(settings.BufferDirectory, ".saving-" + id); Directory.CreateDirectory(staging);
            selected = selected.Select((segment, index) =>
            {
                var owned = Path.Combine(staging, $"owned-{index:D9}.mkv"); MediaOwnership.Retain(segment.Path, owned);
                return segment with { Path = owned };
            }).ToList();
            SaveTiming.Current?.Mark("video-ownership-secured");
            var concat = Path.Combine(staging, "video.txt");
            File.WriteAllLines(concat, selected.SelectMany(x => new[] { "file '" + x.Path.Replace('\\', '/').Replace("'", "'\\''") + "'", "duration " + (x.End - x.Start).ToString("F6", CultureInfo.InvariantCulture) }), new UTF8Encoding(false));
            var snapshots = tracks.Select(track => (Track: track, Packets: JoinAudio(track.Snapshot(start - 2, end + 2), intervals, end, settings.AudioOffsetMs / 1000.0))).ToList();
            // Commit only after video ownership and PCM snapshots are secured. Keep rollback
            // history in the bounded ring until publication; recording owns its own links.
            previousCutoff = rewindSaveCutoff;
            rewindRollbackCutoff = previousCutoff;
            rewindSaveCutoff = requested;
            checkpointAccepted = true;
            SaveTiming.Current?.Mark("snapshot-cutoff-established", new { Tracks = snapshots.Count, PcmBytes = snapshots.Sum(s => s.Packets.Sum(p => (long)p.Data.Length)) });
            AvailableSeconds = 0;
            locked = false; files.Release(); // Own video directory entries and audio references now; export is independent.
            var reports = new Dictionary<string, SignalReport>();
            stage = "write-audio";
            foreach (var (track, packets) in snapshots)
            {
                SaveTiming.Current?.Mark("audio-wav-started", new { track.Name });
                var report = await Task.Run(() => AudioTrack.WriteWave(Path.Combine(staging, track.Name + ".wav"), packets, 0, duration, settings.SilenceDb));
                SaveTiming.Current?.Mark("audio-wav-completed", new { track.Name });
                reports[track.Name] = report;
                if (track.Health is { } h) clipWarnings.Add(track.Name + ": " + h);
                if (track.Name == "Microphone" && report.LongestUnintentionalSilenceSeconds >= Math.Min(settings.SilenceWarningSeconds, duration * .95)) clipWarnings.Add($"Микрофон: нет сигнала {report.LongestUnintentionalSilenceSeconds:F0} с");
            }
            SaveTiming.Current?.Mark("audio-pcm-processing-completed");
            var temp = Path.Combine(staging, "clip.mkv");
            List<string> args = ["-hide_banner", "-v", "error", "-f", "concat", "-safe", "0", "-i", concat];
            foreach (var track in tracks) args.AddRange(["-i", Path.Combine(staging, track.Name + ".wav")]);
            var mixInputs = tracks.Select((t, i) => (t, i)).Where(x => !(settings.AudioMode == "System" && x.t.Name == "Discord")).Select(x => $"[{x.i + 1}:a]").ToList();
            var filters = $"[0:v]trim=start={(start - rawStart).ToString("F6", CultureInfo.InvariantCulture)}:duration={duration.ToString("F6", CultureInfo.InvariantCulture)},setpts=PTS-STARTPTS[v]";
            if (mixInputs.Count > 0) filters += ";" + string.Join("", mixInputs) + $"amix=inputs={mixInputs.Count}:normalize=0,alimiter=limit=0.95:latency=1[mix]";
            args.AddRange(["-filter_complex", filters, "-map", "[v]"]);
            if (mixInputs.Count > 0) args.AddRange(["-map", "[mix]"]);
            for (var i = 0; i < tracks.Count; i++) args.AddRange(["-map", $"{i + 1}:a:0"]);
            // Decode the partial GOP: stream copy would leak pre-checkpoint frames.
            args.AddRange(["-c:v", Encoder, "-preset", "p4", "-tune", "hq", "-rc", "vbr", "-cq", settings.Quality.ToString(), "-b:v", "0", "-maxrate", $"{settings.MaxBitrateMbps}M", "-bufsize", $"{settings.MaxBitrateMbps * 2}M", "-bf", "0", "-r", settings.Fps.ToString(), "-c:a", "aac", "-b:a", "160k", "-t", duration.ToString("F6", CultureInfo.InvariantCulture)]);
            if (mixInputs.Count > 0) args.AddRange(["-metadata:s:a:0", "title=Mix", "-disposition:a:0", "default"]);
            for (var i = 0; i < tracks.Count; i++) args.AddRange([$"-metadata:s:a:{i + 1}", $"title={tracks[i].Name}", $"-disposition:a:{i + 1}", "0"]);
            args.AddRange(["-y", temp]);
            stage = "mux";
            SaveTiming.Current?.Mark("video-processing-started");
            var result = await MediaTools.Run("ffmpeg", args, 180);
            SaveTiming.Current?.Mark("video-aac-mux-finalization-completed");
            if (result.Code != 0) throw new IOException("Не удалось собрать клип: " + result.Error);
            stage = "verify";
            var probe = await MediaTools.Run("ffprobe", ["-v", "error", "-show_streams", "-show_format", "-of", "json", temp]);
            using var document = JsonDocument.Parse(probe.Output);
            var streams = document.RootElement.GetProperty("streams");
            if (probe.Code != 0 || !streams.EnumerateArray().Any(s => s.GetProperty("codec_type").GetString() == "video") || streams.GetArrayLength() != 1 + tracks.Count + (mixInputs.Count > 0 ? 1 : 0)) throw new IOException("Проверка дорожек готового клипа не пройдена.");
            var final = Path.Combine(folder, id + ".mkv");
            SaveTiming.Current?.Mark("media-validation-completed");
            SaveTiming.Current?.Associate(final);
            var metadata = new { GameName, Encoder, CaptureBackend, HideDisplayBorder = settings.HideDisplayBorder, Duration = duration, CaptureMode = settings.CaptureMode, AudioMode = settings.AudioMode, AudioOffsetMs = settings.AudioOffsetMs, Sync = "WASAPI frame position anchored to QPC; video epoch estimated from pre-encode frame arrival", Warnings = clipWarnings, RemovedCaptureGapSeconds = Math.Max(0, end - start - duration), Tracks = reports,
                ActualStart = start, ActualEnd = end, AcceptedSaveBoundary = requested, PreviousSaveCutoff = double.IsFinite(previousCutoff) ? (double?)previousCutoff : null,
                CaptureDiagnostics = tracks.ToDictionary(t => t.Name, t => new { t.DeviceName, t.CaptureFormat, t.MissingFrames, t.Discontinuities }) };
            readyToPublish = true; stage = "publish";
            var metadataWarning = await ClipFiles.Publish(temp, final, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
            published = true;
            SaveTiming.Current?.Mark("final-media-published", new { Path = final, Duration = duration });
            if (metadataWarning != null) clipWarnings.Add(metadataWarning);
            return new(final, duration, clipWarnings);
        }
        catch (Exception ex)
        {
            ClipFiles.Log(stage, ex, staging);
            if (readyToPublish && !published) throw new IOException($"Не удалось перенести готовый клип. Он оставлен для восстановления: {Path.Combine(staging!, "clip.mkv")}", ex);
            throw;
        }
        finally
        {
            if (checkpointAccepted)
            {
                if (!locked) { await files.WaitAsync(); locked = true; }
                // OFF/ON owns a new history epoch; a late failed export cannot revive it.
                if (!published && rewindSince == requestedEpoch && rewindSaveCutoff == requested) rewindSaveCutoff = previousCutoff;
                rewindRollbackCutoff = null;
            }
            if (staging != null && (published || !readyToPublish)) try { Directory.Delete(staging, true); } catch { }
            if (locked) files.Release();
            exports.Release();
            SaveTiming.Current?.Mark("export-cleanup-completed");
        }
    }
    internal static List<AudioPacket> JoinAudio(List<AudioPacket> packets, List<Segment> segments, double end, double offset)
    {
        List<AudioPacket> result = [];
        double cursor = 0;
        foreach (var segment in segments)
        {
            var length = Math.Min(segment.End, end) - segment.Start;
            var from = segment.Start + offset;
            foreach (var packet in packets)
            {
                var first = Math.Max(0, (int)Math.Round((from - packet.Start) * 48000));
                var last = Math.Min(packet.Data.Length / 4, (int)Math.Round((from + length - packet.Start) * 48000));
                if (last > first) result.Add(new(cursor + packet.Start + first / 48000.0 - from, packet.Data.AsSpan(first * 4, (last - first) * 4).ToArray()) { IntentionallyMuted = packet.IntentionallyMuted });
            }
            cursor += length;
        }
        return result;
    }
    public async Task Stop()
    {
        // Also join an already running finalization before disposing shared synchronization/resources.
        await StopRecording();
        await StopProducer();
    }
    private async Task StopProducer()
    {
        stopping = true;
        if (video is { HasExited: false })
        {
            try { await video.StandardInput.WriteLineAsync("q"); await video.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4)); }
            catch { try { video.Kill(true); await video.WaitForExitAsync(); } catch { } }
        }
        if (logTask != null) await logTask;
        if (progressTask != null) await progressTask;
        foreach (var track in tracks) track.Dispose();
    }
    public async ValueTask DisposeAsync()
    {
        await Stop();
        recordingPulseCancellation.Cancel();
        if (recordingPulse != null) await recordingPulse;
        await exports.WaitAsync(); exports.Release();
        lifetime?.Dispose(); video?.Dispose(); files.Dispose();
        recordingPulseCancellation.Dispose(); recordingCommands.Dispose(); exports.Dispose();
        // Only this session's generated directory; unfinished crash sessions remain recoverable.
        if (!PreserveBuffer) try { Directory.Delete(SessionDirectory, true); } catch { }
    }
}
