using System.IO;

namespace Replay;

public enum SaveClipStage { Queued, Preparing, Exporting, Completed, Failed, Cancelled }
public sealed record AcceptedSave(double AcceptedQpc, Task<SavedClip> Completion, Task<bool> ExportStarted)
{
    private int stage;
    public SaveClipStage Stage => (SaveClipStage)Volatile.Read(ref stage);
    public Task<bool> PreparationStarted { get; internal init; } = Task.FromResult(false);
    internal void SetStage(SaveClipStage value) => Volatile.Write(ref stage, (int)value);
}

public sealed partial class Recorder
{
    // Budgets count shared hard links conservatively, once per accepted request.
    // Scratch WAV/encode files also need free-space reserve. These are not settings.
    internal int SaveJobLimit { get; set; } = 8;
    internal long SaveVideoLimit { get; set; } = 2L * 1024 * 1024 * 1024;
    internal long SavePcmLimit { get; set; } = 512L * 1024 * 1024;
    private const double SaveClosureTimeoutSeconds = 12;
    private const long ClosingSegmentReserve = 64L * 1024 * 1024;
    private readonly List<SaveRequest> saveRequests = new(); // files protects all queue metadata
    private readonly Queue<SaveRequest> saveQueue = new();
    private Task? saveWorker;
    private bool saveWorkerRunning;
    private volatile bool saveClosing;
    private SaveRequest? lastSaveRequest;
    private long saveVideoReserved, savePcmReserved;
    private readonly object saveAdmissionGate = new();
    private Task saveAdmissionTail = Task.CompletedTask;
    private int pendingSaveAdmissions;
    public bool CanAcceptSave
    {
        get
        {
            if (saveClosing || stopping || !RewindEnabled || SaveQueueFull) return false;
            if (Volatile.Read(ref currentEpochSaves) > 0 && Native.Clock > rewindAcceptedCutoff) return true;
            if (!files.Wait(0)) return lastSaveAvailability;
            try
            {
                if (saveClosing || stopping || !RewindEnabled || SaveQueueFull) return false;
                // While a predecessor is pending, its candidate history remains readable.
                // New QPC time may lie in the active segment; acceptance does not wait for closure.
                var lower = Math.Max(Native.Clock - settings.BufferSeconds, Math.Max(rewindSince, rewindSaveCutoff));
                return lastSaveAvailability = ReadSaveSources(epochs.Concat(double.IsNaN(origin) ? Array.Empty<(string, double)>() : new[] { (videoDirectory, origin) }).ToArray())
                    .Any(s => s.End > lower) && Native.Clock > Math.Max(rewindSince, rewindAcceptedCutoff);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (FormatException) { return false; }
            catch (OverflowException) { return false; }
            finally { files.Release(); }
        }
    }
    private bool lastSaveAvailability;
    private int currentEpochSaves;
    public int OutstandingSaves => Volatile.Read(ref outstandingSaves);
    public bool SaveQueueFull => OutstandingSaves >= SaveJobLimit;
    private int outstandingSaves;
    public int ProcessingSaves => Volatile.Read(ref processingSaves);
    private int processingSaves;
    public int PreparingSaves => Volatile.Read(ref preparingSaves);
    private int preparingSaves;
    internal (long Video, long Pcm) SaveReservations => (Interlocked.Read(ref saveVideoReserved), Interlocked.Read(ref savePcmReserved));

    private sealed class SaveAudio(AudioTrack track, AudioTrack.PcmLease lease)
    {
        internal readonly AudioTrack Track = track;
        internal readonly AudioTrack.PcmLease Lease = lease;
        internal List<AudioPacket> Packets = new();
        internal string? Health;
        internal object? Diagnostics;
    }
    private sealed class SaveRequest
    {
        internal required double Accepted, Epoch, Lower, InitialCutoff;
        internal required string Id, Staging, Encoder, Backend;
        internal required (string Directory, double Origin)[] Sources;
        internal required CancellationToken Cancellation;
        internal SaveTiming? Timing;
        internal SaveRequest? Previous;
        internal double FinalCutoff;
        internal readonly List<Segment> Segments = new();
        internal readonly HashSet<string> Retained = new(StringComparer.OrdinalIgnoreCase);
        internal readonly List<SaveAudio> Audio = new();
        internal required string[] Warnings;
        internal long VideoReserve, VideoBytes, PcmReserve;
        internal bool Ready, Published, Recoverable;
        internal Exception? Failure;
        internal Task Preparation = Task.CompletedTask;
        internal Task VideoPreflight = Task.CompletedTask;
        internal AcceptedSave AcceptedSave = null!;
        internal readonly TaskCompletionSource<bool> Preparing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<SavedClip> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public Task<AcceptedSave> AcceptSave(CancellationToken cancellationToken = default)
    {
        // Capture command QPC before scheduling, locks, segment closure or FIFO waiting.
        var accepted = Native.Clock;
        var frozenTick = System.Diagnostics.Stopwatch.GetTimestamp();
        var epoch = rewindSince;
        var timing = SaveTiming.Current;
        timing?.MarkAt("request-capture-timestamp-frozen", frozenTick, new { AcceptedQpc = accepted });
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous;
        lock (saveAdmissionGate)
        {
            // Bound callers waiting for admission too, before allocating snapshots.
            // Closing is checked before touching the metadata semaphore, including
            // calls made after DisposeAsync has completed.
            if (saveClosing) return Task.FromException<AcceptedSave>(new InvalidOperationException("Rewind останавливается; новый клип не принят."));
            if (pendingSaveAdmissions >= SaveJobLimit)
                return Task.FromException<AcceptedSave>(new InvalidOperationException("Очередь приёма Rewind заполнена; новый клип не принят."));
            pendingSaveAdmissions++;
            previous = saveAdmissionTail; saveAdmissionTail = done.Task;
        }
        return Admit();
        async Task<AcceptedSave> Admit()
        {
            try
            {
                await previous.ConfigureAwait(false);
                timing?.Mark("admission-turn-available");
                return await Task.Run(() => AcceptSaveCore(accepted, epoch, timing, cancellationToken)).ConfigureAwait(false);
            }
            finally
            {
                lock (saveAdmissionGate) pendingSaveAdmissions--;
                done.TrySetResult();
            }
        }
    }

    private async Task<AcceptedSave> AcceptSaveCore(double accepted, double epoch, SaveTiming? timing, CancellationToken cancellation)
    {
        using var scope = timing?.Enter();
        timing?.Mark("admission-started");
        await files.WaitAsync(cancellation).ConfigureAwait(false);
        timing?.Mark("admission-metadata-acquired");
        SaveRequest? request = null;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (saveClosing || stopping) throw new InvalidOperationException("Rewind останавливается; новый клип не принят.");
            if (!RewindEnabled || epoch != rewindSince) throw new InvalidOperationException("Состояние буфера Rewind изменилось; клип не принят.");
            if (saveRequests.Count >= SaveJobLimit) throw new InvalidOperationException("Очередь Rewind заполнена. Новый клип не принят; дождись завершения экспорта.");
            var lower = Math.Max(accepted - settings.BufferSeconds, Math.Max(epoch, rewindSaveCutoff));
            var sources = epochs.ToList();
            if (!double.IsNaN(origin)) sources.Add((videoDirectory, origin));
            var segments = ReadSaveSources(sources.ToArray()).Where(s => s.End > lower && s.Start < accepted).ToList();
            if (segments.Count == 0) throw new InvalidOperationException("Нет доступного видео для клипа; запрос не принят.");
            var videoReserve = checked(segments.Sum(s => new FileInfo(s.Path).Length) + ClosingSegmentReserve);
            var perTrack = checked((long)Math.Ceiling((accepted - lower + 4) * AudioTrack.Rate * AudioTrack.BytesPerFrame) + 1024 * 1024);
            var pcmReserve = checked(perTrack * tracks.Count);
            // Recovery files outlive terminal requests; include them on subsequent
            // admission instead of silently freeing their disk-retention budget.
            var recoverableBytes = Directory.GetDirectories(settings.BufferDirectory, ".saving-*")
                .Where(d => !saveRequests.Any(r => string.Equals(r.Staging, d, StringComparison.OrdinalIgnoreCase)))
                .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
                .Sum(p => new FileInfo(p).Length);
            if (videoReserve > SaveVideoLimit - saveVideoReserved - recoverableBytes || pcmReserve > SavePcmLimit - savePcmReserved)
                throw new InvalidOperationException("Лимит удерживаемых данных Rewind достигнут. Новый клип не принят; дождись экспорта.");
            if (FreeSpace(settings.BufferDirectory) < RecordingReserve + saveVideoReserved + savePcmReserved * 3 + videoReserve + pcmReserve * 3)
                throw new IOException("Недостаточно свободного места для очереди Rewind; новый клип не принят.");
            if (FreeSpace(settings.OutputDirectory) < RecordingReserve + saveVideoReserved + videoReserve + savePcmReserved + pcmReserve)
                throw new IOException("Недостаточно свободного места в папке клипов; новый клип не принят.");
            var id = ClipFiles.NewId();
            request = new() { Accepted = accepted, Epoch = epoch, Lower = lower, InitialCutoff = rewindSaveCutoff,
                FinalCutoff = rewindSaveCutoff, Sources = sources.ToArray(), Id = id, Encoder = Encoder, Backend = CaptureBackend,
                Staging = Path.Combine(settings.BufferDirectory, ".saving-" + id), Cancellation = cancellation,
                Timing = timing, Previous = lastSaveRequest, VideoReserve = videoReserve, PcmReserve = pcmReserve,
                Warnings = warnings.Concat(Error is { } error ? new[] { error } : Array.Empty<string>()).ToArray() };
            Directory.CreateDirectory(request.Staging);
            RetainSaveSegments(request, segments);
            timing?.Mark("admission-video-ownership-completed", new { Count = request.Segments.Count });
            if (request.Failure != null) throw request.Failure;
            foreach (var track in tracks)
            {
                var lease = track.RetainPcm(lower - 2, accepted + 2, perTrack);
                request.Audio.Add(new(track, lease));
                if (lease.Overflow) throw new IOException("Лимит PCM превышен; клип не принят.");
            }
            request.AcceptedSave = new(accepted, request.Result.Task, request.Started.Task) { PreparationStarted = request.Preparing.Task };
            request.AcceptedSave.SetStage(saveRequests.Count > 0 ? SaveClipStage.Queued : SaveClipStage.Preparing);
            timing?.Mark("pcm-lease-ownership-secured", new { Tracks = request.Audio.Count });
            saveRequests.Add(request); saveQueue.Enqueue(request); lastSaveRequest = request;
            saveVideoReserved += videoReserve; savePcmReserved += pcmReserve;
            Volatile.Write(ref outstandingSaves, saveRequests.Count);
            rewindAcceptedCutoff = accepted; AvailableSeconds = 0;
            Volatile.Write(ref currentEpochSaves, saveRequests.Count(r => r.Epoch == rewindSince));
            timing?.Mark("history-boundary-frozen", new { Requested = accepted, Epoch = double.IsFinite(epoch) ? (double?)epoch : null, Lower = lower });
            timing?.Mark("save-request-accepted", new { Accepted = accepted, Outstanding = saveRequests.Count, VideoReserve = videoReserve, PcmReserve = pcmReserve });
            timing?.Mark("admission-completed", new { WaitingBehindJobs = saveRequests.Count - 1, WorkerRunning = saveWorkerRunning });
            timing?.WatchCapture(request.Sources.Select(s => s.Directory));
            // With no predecessor, the final start is already known. An inside-GOP
            // start cannot enter packet-copy, so overlap its normal NVDEC probe
            // with the capture-tail wait. Queue jobs and possibly eligible copies
            // retain their existing lazy checks, avoiding speculative repeated work.
            if (saveRequests.Count == 1 && request.Encoder == "av1_nvenc" &&
                Math.Max(request.Lower, segments[0].Start) > segments[0].Start &&
                !segments.Any(s => s.End >= accepted))
            {
                var sample = request.Segments[0].Path; // Already owned CLOSED media.
                request.VideoPreflight = Task.Run(async () =>
                {
                    using var probeTiming = request.Timing?.Enter();
                    using var probeCancellation = MediaTools.EnterCancellation(request.Cancellation);
                    request.Timing?.Mark("nvdec-preflight-started");
                    await VideoExport.Preflight(request.Encoder, sample).ConfigureAwait(false);
                    request.Timing?.Mark("nvdec-preflight-completed");
                });
            }
            request.Preparation = Task.Run(() => PrepareSave(request));
            if (!saveWorkerRunning) { saveWorkerRunning = true; saveWorker = Task.Run(ExportSaveQueue); }
            return request.AcceptedSave;
        }
        catch
        {
            if (request != null && !saveRequests.Contains(request))
            {
                foreach (var audio in request.Audio) audio.Lease.Dispose();
                try { Directory.Delete(request.Staging, true); } catch { }
            }
            throw;
        }
        finally { files.Release(); }
    }

    private static List<Segment> ReadSaveSources((string Directory, double Origin)[] sources) => sources
        .SelectMany(e => ReadSegments(e.Directory).Select(s => s with { Start = s.Start + e.Origin, End = s.End + e.Origin }))
        .OrderBy(s => s.Start).ToList();

    private void RetainSaveSegments(SaveRequest request, IEnumerable<Segment> segments)
    {
        if (request.Failure != null || request.Ready) return;
        try
        {
            foreach (var segment in segments.Where(s => s.End > request.Lower && s.Start < request.Accepted &&
                request.Sources.Any(e => string.Equals(Path.GetDirectoryName(s.Path), e.Directory, StringComparison.OrdinalIgnoreCase))))
            {
                if (request.Retained.Contains(segment.Path)) continue;
                var length = new FileInfo(segment.Path).Length;
                if (length > request.VideoReserve - request.VideoBytes) throw new IOException("Закрытый сегмент превысил резерв очереди Rewind.");
                var owned = Path.Combine(request.Staging, $"owned-{request.Segments.Count:D9}.mkv");
                MediaOwnership.Retain(segment.Path, owned);
                request.Retained.Add(segment.Path); request.Segments.Add(segment with { Path = owned }); request.VideoBytes += length;
            }
        }
        catch (Exception ex) { request.Failure = ex; }
    }

    private async Task PrepareSave(SaveRequest request)
    {
        using var scope = request.Timing?.Enter();
        request.Timing?.Mark("snapshot-preparation-started");
        try
        {
            var deadline = request.Accepted + SaveClosureTimeoutSeconds;
            int polls = 0;
            while (true)
            {
                request.Cancellation.ThrowIfCancellationRequested();
                await files.WaitAsync(request.Cancellation).ConfigureAwait(false);
                try
                {
                    // Only the frozen source epochs are examined, solely to finish ownership
                    // of segments intersecting this original accepted interval.
                    RetainSaveSegments(request, ReadSaveSources(request.Sources));
                    polls++;
                    if (request.Failure != null) throw request.Failure;
                    if (request.Segments.Any(s => s.End >= request.Accepted) || !Running || Native.Clock >= deadline)
                    {
                        if (!request.Segments.Any(s => s.End >= request.Accepted))
                            throw new TimeoutException("Сегмент для принятого момента не завершён. Клип не опубликован; история не списана.");
                        var tail = request.Segments.First(s => s.End >= request.Accepted);
                        request.Timing?.Mark("video-tail-closure-observed", new { TailEndQpc = tail.End, AcceptedQpc = request.Accepted, ObservedQpc = Native.Clock, Polls = polls });
                        request.Timing?.Mark("video-snapshot-ownership-completed", new { Count = request.Segments.Count });
                        foreach (var audio in request.Audio)
                        {
                            audio.Lease.Dispose();
                            if (audio.Lease.Overflow) throw new IOException("Лимит PCM принятого клипа превышен.");
                            audio.Packets = audio.Lease.Packets;
                            audio.Health = audio.Track.Health;
                            audio.Diagnostics = new { audio.Track.DeviceName, audio.Track.CaptureFormat, audio.Track.MissingFrames, audio.Track.Discontinuities };
                        }
                        request.Timing?.Mark("pcm-snapshot-ownership-completed", new { Tracks = request.Audio.Count, Bytes = request.Audio.Sum(a => a.Lease.Bytes) });
                        request.Ready = true;
                        request.Timing?.Mark("closed-segment-ready", new { Count = request.Segments.Count });
                        request.Timing?.Mark("video-ownership-secured");
                        return;
                    }
                }
                finally { files.Release(); }
                await Task.Delay(40, request.Cancellation).ConfigureAwait(false);
            }
        }
        catch (Exception ex) { request.Failure = ex; }
        finally { request.Timing?.StopWatchingCapture(); foreach (var audio in request.Audio) audio.Lease.Dispose(); }
    }

    private async Task ExportSaveQueue()
    {
        while (true)
        {
            SaveRequest request;
            await files.WaitAsync().ConfigureAwait(false);
            try
            {
                if (saveQueue.Count == 0) { saveWorkerRunning = false; return; }
                request = saveQueue.Dequeue();
                request.Timing?.Mark("export-worker-available");
                request.Timing?.Mark("save-job-dequeued");
                request.AcceptedSave.SetStage(SaveClipStage.Preparing);
                Volatile.Write(ref preparingSaves, 1);
                request.Preparing.TrySetResult(true);
            }
            finally { files.Release(); }
            SavedClip? clip = null; Exception? error = null;
            try
            {
                await request.Preparation.ConfigureAwait(false);
                request.Timing?.Mark("export-worker-preparation-joined");
                // Resolve the outcome of the FIFO predecessor using only this request's
                // already owned candidate snapshot. Never select live history at dequeue.
                var previous = request.Previous;
                request.FinalCutoff = previous?.Epoch == request.Epoch ? previous.FinalCutoff : request.InitialCutoff;
                request.Previous = null;
                if (request.Failure != null) throw request.Failure;
                await request.VideoPreflight.ConfigureAwait(false);
                request.Cancellation.ThrowIfCancellationRequested();
                request.Timing?.Mark("queued-export-started", new { Accepted = request.Accepted, PreviousCutoff = double.IsFinite(request.FinalCutoff) ? (double?)request.FinalCutoff : null });
                Volatile.Write(ref processingSaves, 1);
                Volatile.Write(ref preparingSaves, 0);
                request.AcceptedSave.SetStage(SaveClipStage.Exporting);
                request.Started.TrySetResult(true);
                clip = await ExportSave(request, request.FinalCutoff).ConfigureAwait(false);
                request.FinalCutoff = request.Accepted;
            }
            catch (Exception ex) { error = ex; ClipFiles.Log("queued-save", ex, request.Staging); }
            finally
            {
                // A cancelled/failed preparation must also reap its preflight
                // child before releasing the owned sample or advancing FIFO.
                try { await request.VideoPreflight.ConfigureAwait(false); } catch { /* Reported by this request's terminal result. */ }
                await files.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (request.Published && rewindSince == request.Epoch) rewindSaveCutoff = request.Accepted;
                    saveRequests.Remove(request);
                    Volatile.Write(ref processingSaves, 0);
                    Volatile.Write(ref preparingSaves, 0);
                    saveVideoReserved -= request.VideoReserve; savePcmReserved -= request.PcmReserve;
                    Volatile.Write(ref outstandingSaves, saveRequests.Count);
                    Volatile.Write(ref currentEpochSaves, saveRequests.Count(r => r.Epoch == rewindSince));
                    rewindAcceptedCutoff = saveRequests.Where(r => r.Epoch == rewindSince).Select(r => r.Accepted).DefaultIfEmpty(double.NegativeInfinity).Max();
                    foreach (var audio in request.Audio) audio.Lease.Dispose();
                    request.Audio.Clear(); request.Segments.Clear(); request.Retained.Clear(); request.Sources = Array.Empty<(string, double)>();
                    // Media retained after a publication failure belongs to recovery,
                    // not to a pending job. Reject further work if disk is exhausted.
                    if (!request.Recoverable) try { Directory.Delete(request.Staging, true); } catch (Exception ex) { ClipFiles.Log("queued-save-cleanup", ex, request.Staging); }
                }
                finally { files.Release(); }
                request.Started.TrySetResult(false);
                request.Preparing.TrySetResult(false);
                request.Timing?.Mark("export-cleanup-completed");
            }
            if (clip != null) { request.AcceptedSave.SetStage(SaveClipStage.Completed); request.Result.TrySetResult(clip); }
            else if (error is OperationCanceledException) { request.AcceptedSave.SetStage(SaveClipStage.Cancelled); request.Result.TrySetCanceled(request.Cancellation); }
            else { request.AcceptedSave.SetStage(SaveClipStage.Failed); request.Result.TrySetException(error ?? new IOException("Клип не опубликован.")); }
        }
    }

    private async Task CloseSaveAcceptance()
    {
        await files.WaitAsync().ConfigureAwait(false);
        try { saveClosing = true; } finally { files.Release(); }
        Task admissions;
        lock (saveAdmissionGate) admissions = saveAdmissionTail;
        await admissions.ConfigureAwait(false);
    }
    private async Task DrainSaves()
    {
        await CloseSaveAcceptance().ConfigureAwait(false);
        if (saveWorker != null) await saveWorker.ConfigureAwait(false);
    }
}
