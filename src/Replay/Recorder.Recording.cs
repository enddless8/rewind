namespace Replay;

public sealed partial class Recorder
{
    private readonly SemaphoreSlim recordingCommands = new(1, 1);
    private readonly SemaphoreSlim exports = new(1, 1);
    private readonly CancellationTokenSource recordingPulseCancellation = new();
    private Task? recordingPulse;
    private RecordingSession? recordingSession;
    private int recordingStarting;
    private string? recordingStartError;
    private double rewindSince = double.NegativeInfinity;
    public bool RewindEnabled { get; private set; } = true;
    public RecordingProgress Recording
    {
        get
        {
            var session = Volatile.Read(ref recordingSession);
            if (Volatile.Read(ref recordingStarting) != 0) return new(RecordingState.Starting, 0, null, null, null);
            if (recordingStartError != null) return new(RecordingState.Error, 0, recordingStartError, null, null);
            return session == null ? new(RecordingState.Idle, 0, null, null, null)
                : new(session.State, session.Duration, session.Error, session.Result, session.Directory);
        }
    }
    internal Func<string, long> FreeSpace { get; set; } = path => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
    internal long? RewindByteLimitForTest { get; set; }
    private const long RecordingReserve = 256L * 1024 * 1024;
    public async Task SetRewindEnabled(bool enabled)
    {
        await files.WaitAsync();
        try
        {
            if (RewindEnabled == enabled) return;
            recordingSession?.Accept(await ReadSegmentsWithRetry());
            RewindEnabled = enabled; rewindSince = Native.Clock; AvailableSeconds = 0;
            foreach (var track in tracks) track.SetRetention(enabled ? settings.BufferSeconds + 20 : 2);
        }
        finally { files.Release(); }
        await Maintain();
    }
    public async Task StartRecording()
    {
        await recordingCommands.WaitAsync();
        try
        {
            if (Recording.State is RecordingState.Recording or RecordingState.Starting or RecordingState.Stopping or RecordingState.Finalizing) return;
            Volatile.Write(ref recordingStarting, 1); recordingStartError = null;
            if (!Running) throw new InvalidOperationException("Для начала записи нужен работающий захват.");
            if (FreeSpace(settings.BufferDirectory) < RecordingReserve || FreeSpace(settings.OutputDirectory) < RecordingReserve)
                throw new IOException("Недостаточно места для записи. Освободи место на диске.");
            await files.WaitAsync();
            try { recordingSession = new RecordingSession(settings, GameName, Encoder, CaptureBackend, tracks, warnings); }
            finally { files.Release(); }
            if (recordingPulse == null) recordingPulse = Task.Run(RecordingPulse);
        }
        catch (Exception ex) { recordingStartError = ex.Message; throw; }
        finally { Volatile.Write(ref recordingStarting, 0); recordingCommands.Release(); }
    }
    public async Task<SavedClip?> StopRecording(string? reason = null)
    {
        await recordingCommands.WaitAsync();
        try
        {
            var session = recordingSession;
            if (session == null || session.State is RecordingState.Idle or RecordingState.Error) return session?.Result;
            using var timing = SaveTiming.Begin(Guid.NewGuid());
            timing?.Mark("recording-stop-accepted");
            // T1 is frozen before waiting for a segment to close; later frames never extend the interval.
            await files.WaitAsync();
            try { session.Freeze(Native.Clock, reason); } finally { files.Release(); }
            if (!RewindEnabled) await StopProducer();
            var deadline = Native.Clock + 4;
            do
            {
                await files.WaitAsync();
                try { session.Accept(await ReadSegmentsWithRetry()); }
                catch (Exception ex) { ClipFiles.Log("recording-retain", ex, session.Directory); break; }
                finally { files.Release(); }
                if (!Running || session.ConfirmedEnd >= session.StoppedAt || Native.Clock >= deadline) break;
                await Task.Delay(80);
            } while (true);
            await files.WaitAsync();
            try { session.CompleteVideoOwnership(); } finally { files.Release(); }
            timing?.Mark("recording-last-segment-finalized");
            return await session.FinalizeRecording(tracks);
        }
        finally { recordingCommands.Release(); }
    }
    private async Task RecordingPulse()
    {
        while (!recordingPulseCancellation.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(200, recordingPulseCancellation.Token);
                var session = recordingSession;
                if (session?.State != RecordingState.Recording) continue;
                string? failure = null;
                await files.WaitAsync(recordingPulseCancellation.Token);
                try
                {
                    session.Accept(await ReadSegmentsWithRetry());
                    if (!Running || game != null && !Native.IsWindow(game.Handle) || Native.Clock - lastFrame > 5)
                        failure = "Захват прерван: запись завершена на доступных фрагментах.";
                    // Final output coexists with retained input. Reserve grows with video rather than waiting for ENOSPC.
                    else if (FreeSpace(settings.BufferDirectory) < RecordingReserve + session.VideoBytes * 2 || FreeSpace(settings.OutputDirectory) < RecordingReserve + session.VideoBytes * 2)
                        failure = "Мало места на диске: запись остановлена, доступные данные сохранены.";
                    else failure = session.Audio.Select(a => a.Error).FirstOrDefault(e => e != null)
                        ?? tracks.Where(t => t.Error != null).Select(t => "Источник аудио недоступен: " + t.Name + ": " + t.Error).FirstOrDefault();
                }
                finally { files.Release(); }
                if (failure != null) await StopRecording(failure);
                else await Maintain();
            }
            catch (OperationCanceledException) when (recordingPulseCancellation.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                ClipFiles.Log("recording-monitor", ex, recordingSession?.Directory);
                if (recordingSession?.State == RecordingState.Recording) await StopRecording("Запись прервана: " + ex.Message);
            }
        }
    }
    public static IReadOnlyList<IncompleteRecording> FindIncompleteRecordings(string buffer) => RecordingSession.FindIncomplete(buffer);
    internal static Task<SavedClip> RecoverRecording(string directory) => RecordingSession.Recover(directory);
}
