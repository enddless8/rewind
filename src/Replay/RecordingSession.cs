using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Replay;

public enum RecordingState { Idle, Starting, Recording, Stopping, Finalizing, Error }
public sealed record RecordingProgress(RecordingState State, double Duration, string? Error, SavedClip? Result, string? SessionDirectory);
public sealed record IncompleteRecording(string Directory, RecordingState State, double StartedAt, double ConfirmedEnd, string? Error);

internal sealed class RecordingManifest
{
    public int Version { get; set; } = 1;
    public Settings Settings { get; set; } = new();
    public string ApplicationName { get; set; } = "";
    public string Encoder { get; set; } = "";
    public string Backend { get; set; } = "";
    public string[] Tracks { get; set; } = [];
    public string[] Warnings { get; set; } = [];
    public double StartedAt { get; set; }
    public double? StoppedAt { get; set; }
    public RecordingState State { get; set; }
    public string? Error { get; set; }
    public string? PublishedPath { get; set; }
}

internal sealed class RecordingSession
{
    private readonly RecordingManifest manifest;
    private readonly StreamWriter segments;
    private double acceptedEnd = double.NegativeInfinity;
    private int count;
    private long videoBytes;
    private int state;
    public string Directory { get; }
    public RecordingState State { get => (RecordingState)Volatile.Read(ref state); private set => Volatile.Write(ref state, (int)value); }
    public string? Error => manifest.Error;
    public SavedClip? Result { get; private set; }
    public double StartedAt => manifest.StartedAt;
    public double StoppedAt => manifest.StoppedAt ?? Native.Clock;
    public double ConfirmedEnd => Volatile.Read(ref acceptedEnd);
    public long VideoBytes => Interlocked.Read(ref videoBytes);
    public double Duration => Math.Max(0, StoppedAt - StartedAt);
    public IReadOnlyList<RecordingAudioWriter> Audio { get; }
    public RecordingSession(Settings settings, string applicationName, string encoder, string backend, IReadOnlyList<AudioTrack> tracks, IReadOnlyList<string>? warnings = null)
    {
        Directory = Path.Combine(settings.BufferDirectory, ".recording-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        manifest = new() { Settings = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!, ApplicationName = applicationName,
            Encoder = encoder, Backend = backend, StartedAt = Native.Clock, Tracks = tracks.Select(t => t.Name).ToArray(), Warnings = warnings?.ToArray() ?? [], State = RecordingState.Starting };
        Persist();
        segments = new StreamWriter(new FileStream(Path.Combine(Directory, "segments.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        List<RecordingAudioWriter> writers = [];
        try
        {
            foreach (var track in tracks)
            {
                var writer = new RecordingAudioWriter(Directory, track.Name, StartedAt + settings.AudioOffsetMs / 1000.0); writers.Add(writer);
                track.AttachRecording(writer, StartedAt + settings.AudioOffsetMs / 1000.0);
            }
            Audio = writers; State = RecordingState.Recording; manifest.State = State; Persist();
        }
        catch
        {
            foreach (var track in tracks) track.DetachRecording();
            foreach (var writer in writers) writer.Complete(StartedAt).GetAwaiter().GetResult();
            segments.Dispose(); throw;
        }
    }
    private void Persist()
    {
        var path = Path.Combine(Directory, "recording.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
    private void PersistState()
    {
        // A full/locked disk must not prevent stopping the producer or closing audio writers.
        // The original active manifest + flushed segment journal remain recoverable.
        try { Persist(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { manifest.Error ??= "Не удалось обновить журнал записи; временные данные сохранены: " + ex.Message; ClipFiles.Log("recording-journal", ex, Directory); }
    }
    // Metadata lock serializes this with Rewind unlinking and recording stop. No whole-session collection in RAM.
    public void Accept(IEnumerable<Segment> closed)
    {
        if (State is not (RecordingState.Recording or RecordingState.Stopping)) return;
        foreach (var segment in closed)
        {
            if (segment.End <= StartedAt || segment.End <= acceptedEnd + .001 || segment.Start >= StoppedAt) continue;
            if (count > 0 && segment.Start > acceptedEnd + .15)
                throw new IOException("Обнаружен разрыв захвата: запись завершена на последнем целостном фрагменте.");
            var target = Path.Combine(Directory, $"video-{count:D9}.mkv");
            MediaOwnership.Retain(segment.Path, target);
            Interlocked.Add(ref videoBytes, new FileInfo(target).Length);
            // Flush journal AFTER acquiring ownership. Orphan entry from a crash is preserved too.
            segments.WriteLine(JsonSerializer.Serialize(segment with { Path = Path.GetFileName(target) }));
            count++; Volatile.Write(ref acceptedEnd, segment.End);
        }
    }
    public void Freeze(double stop, string? reason)
    {
        if (manifest.StoppedAt != null) return;
        manifest.StoppedAt = Math.Max(StartedAt, stop); manifest.Error = reason;
        State = manifest.State = RecordingState.Stopping; PersistState();
    }
    public void CompleteVideoOwnership()
    {
        State = manifest.State = RecordingState.Finalizing; PersistState();
        segments.Dispose();
    }
    public async Task<SavedClip?> FinalizeRecording(IReadOnlyList<AudioTrack> tracks)
    {
        foreach (var track in tracks) track.DetachRecording();
        foreach (var audio in Audio) await audio.Complete(StoppedAt + manifest.Settings.AudioOffsetMs / 1000.0);
        try
        {
            Result = await FinalizeFiles(Directory, manifest);
            State = manifest.State = manifest.Error == null ? RecordingState.Idle : RecordingState.Error;
            manifest.PublishedPath = Result.Path; PersistState();
            // Only our generated session, after confirmed publication. Failure always retains source files.
            try { System.IO.Directory.Delete(Directory, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ClipFiles.Log("recording-cleanup", ex, Directory);
                // A scanner/AV may keep recording.json open after publication. A separate marker
                // prevents treating the leftover directory as an unfinished recording on next launch.
                try { File.WriteAllText(Path.Combine(Directory, "completed.json"), JsonSerializer.Serialize(Result)); }
                catch (Exception markerError) when (markerError is IOException or UnauthorizedAccessException) { ClipFiles.Log("recording-complete-marker", markerError, Directory); }
            }
            return Result;
        }
        catch (Exception ex)
        {
            manifest.Error = (manifest.Error == null ? "" : manifest.Error + "\n") + "Запись оставлена для восстановления: " + ex.Message;
            State = manifest.State = RecordingState.Error; PersistState(); ClipFiles.Log("recording-finalize", ex, Directory); return null;
        }
    }
    private static async Task<SavedClip> FinalizeFiles(string directory, RecordingManifest manifest)
    {
        double first = double.NaN, last = double.NaN;
        var concat = Path.Combine(directory, "video.txt");
        using (var writer = new StreamWriter(concat, false, new UTF8Encoding(false)))
            foreach (var line in File.ReadLines(Path.Combine(directory, "segments.jsonl")))
            {
                Segment? segment;
                try { segment = JsonSerializer.Deserialize<Segment>(line); } catch (JsonException) { break; } // Torn final journal line.
                if (segment == null) continue;
                var path = Path.Combine(directory, Path.GetFileName(segment.Path));
                if (!File.Exists(path)) throw new IOException("Фрагмент записи недоступен: " + path);
                if (!double.IsNaN(last) && segment.Start > last + .15) break;
                if (double.IsNaN(first)) first = segment.Start;
                last = segment.End;
                writer.WriteLine("file '" + path.Replace('\\', '/').Replace("'", "'\\''") + "'");
                writer.WriteLine("duration " + Number(segment.End - segment.Start));
            }
        var end = Math.Min(manifest.StoppedAt ?? last, last);
        var start = Math.Max(first, manifest.StartedAt);
        var duration = end - start;
        if (!double.IsFinite(duration) || duration <= 0) throw new IOException("Нет завершённого видео; временные данные сохранены.");
        var settings = manifest.Settings;
        // Re-encode the selected interval: stream-copy would silently start on a preceding 2s GOP.
        // Only finalization adds an encoder; active recording uses the sole shared capture/NVENC process.
        List<string> args = ["-hide_banner", "-v", "error", "-f", "concat", "-safe", "0", "-i", concat];
        foreach (var track in manifest.Tracks) args.AddRange(["-f", "s16le", "-ar", "48000", "-ac", "2", "-i", Path.Combine(directory, track + ".pcm")]);
        var audioStart = start - manifest.StartedAt;
        List<string> filters = [];
        for (int i = 0; i < manifest.Tracks.Length; i++)
            filters.Add($"[{i + 1}:a]atrim=start={Number(audioStart)}:duration={Number(duration)},asetpts=PTS-STARTPTS,apad=whole_dur={Number(duration)},asplit=2[a{i}][m{i}]");
        var mixed = manifest.Tracks.Select((name, index) => (name, index)).Where(x => !(settings.AudioMode == "System" && x.name == "Discord")).ToArray();
        if (mixed.Length > 0) filters.Add(string.Concat(mixed.Select(x => $"[m{x.index}]")) + $"amix=inputs={mixed.Length}:normalize=0,alimiter=limit=0.95:latency=1[mix]");
        foreach (var x in manifest.Tracks.Select((name, index) => (name, index)).Where(x => settings.AudioMode == "System" && x.name == "Discord")) filters.Add($"[m{x.index}]anullsink");
        filters.Add($"[0:v]trim=start={Number(start - first)}:duration={Number(duration)},setpts=PTS-STARTPTS[v]");
        args.AddRange(["-filter_complex", string.Join(";", filters), "-map", "[v]"]);
        if (mixed.Length > 0) args.AddRange(["-map", "[mix]", "-metadata:s:a:0", "title=Mix", "-disposition:a:0", "default"]);
        for (int i = 0; i < manifest.Tracks.Length; i++) args.AddRange(["-map", $"[a{i}]", $"-metadata:s:a:{i + (mixed.Length > 0 ? 1 : 0)}", "title=" + manifest.Tracks[i], $"-disposition:a:{i + (mixed.Length > 0 ? 1 : 0)}", "0"]);
        var temp = Path.Combine(directory, "recording.mkv");
        args.AddRange(["-c:v", manifest.Encoder, "-preset", "p4", "-tune", "hq", "-rc", "vbr", "-cq", settings.Quality.ToString(), "-b:v", "0",
            "-maxrate", settings.MaxBitrateMbps + "M", "-bufsize", settings.MaxBitrateMbps * 2 + "M", "-bf", "0", "-r", settings.Fps.ToString(),
            "-c:a", "aac", "-b:a", "160k", "-t", Number(duration), "-y", temp]);
        var result = await MediaTools.Run("ffmpeg", args, Math.Max(180, (int)Math.Min(int.MaxValue, duration * 3 + 60)), bindLifetime: true);
        if (result.Code != 0) throw new IOException("Не удалось собрать запись: " + result.Error);
        var probe = await MediaTools.Run("ffprobe", ["-v", "error", "-show_streams", "-of", "json", temp], bindLifetime: true);
        using var json = JsonDocument.Parse(probe.Output);
        if (probe.Code != 0 || json.RootElement.GetProperty("streams").GetArrayLength() != 1 + manifest.Tracks.Length + (mixed.Length > 0 ? 1 : 0)) throw new IOException("Проверка записи не пройдена.");
        var folder = ClipFiles.OutputFolder(settings.OutputDirectory, settings.GroupByApplication, manifest.ApplicationName); System.IO.Directory.CreateDirectory(folder);
        var final = Path.Combine(folder, "recording-" + ClipFiles.NewId() + ".mkv");
        List<string> warnings = manifest.Warnings.ToList();
        if (manifest.Error != null) warnings.Add(manifest.Error);
        if ((manifest.StoppedAt ?? end) - end > .1) warnings.Add($"Последние {(manifest.StoppedAt ?? end) - end:F2} с недоступны после сбоя захвата.");
        var warning = await ClipFiles.Publish(temp, final, JsonSerializer.Serialize(new { Kind = "Recording", Duration = duration, manifest, ActualStart = start, ActualEnd = end,
            Boundary = "Decoded trim to frame grid; video QPC epoch remains arrival-estimated", Warnings = warnings }, new JsonSerializerOptions { WriteIndented = true }));
        if (warning != null) warnings.Add(warning);
        return new(final, duration, warnings);
    }
    private static string Number(double value) => value.ToString("F6", CultureInfo.InvariantCulture);
    public static IReadOnlyList<IncompleteRecording> FindIncomplete(string buffer)
    {
        List<IncompleteRecording> result = [];
        if (!System.IO.Directory.Exists(buffer)) return result;
        foreach (var directory in System.IO.Directory.EnumerateDirectories(buffer, ".recording-*"))
        {
            try
            {
                if (File.Exists(Path.Combine(directory, "completed.json")))
                {
                    var complete = JsonSerializer.Deserialize<SavedClip>(File.ReadAllText(Path.Combine(directory, "completed.json")));
                    if (complete != null && File.Exists(complete.Path)) continue;
                }
                var manifest = JsonSerializer.Deserialize<RecordingManifest>(File.ReadAllText(Path.Combine(directory, "recording.json"))) ?? throw new JsonException("Пустой журнал записи.");
                if (manifest.PublishedPath != null && File.Exists(manifest.PublishedPath)) continue;
                double end = manifest.StartedAt;
                foreach (var line in File.ReadLines(Path.Combine(directory, "segments.jsonl")))
                    try { end = Math.Max(end, JsonSerializer.Deserialize<Segment>(line)!.End); } catch (JsonException) { break; }
                result.Add(new(directory, manifest.State, manifest.StartedAt, end, manifest.Error));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            { result.Add(new(directory, RecordingState.Error, 0, 0, "Журнал повреждён; данные сохранены: " + ex.Message)); }
        }
        return result;
    }
    // Internal/manual recovery only; startup detects and reports without deleting potentially recoverable data.
    public static async Task<SavedClip> Recover(string directory)
    {
        var manifest = JsonSerializer.Deserialize<RecordingManifest>(File.ReadAllText(Path.Combine(directory, "recording.json")))!;
        if (manifest.Version != 1) throw new IOException("Неизвестная версия журнала записи.");
        if (manifest.PublishedPath != null && File.Exists(manifest.PublishedPath)) return new(manifest.PublishedPath, 0, []);
        var clip = await FinalizeFiles(directory, manifest);
        manifest.PublishedPath = clip.Path; manifest.State = RecordingState.Idle;
        File.WriteAllText(Path.Combine(directory, "recording.json"), JsonSerializer.Serialize(manifest));
        System.IO.Directory.Delete(directory, true); return clip;
    }
}
