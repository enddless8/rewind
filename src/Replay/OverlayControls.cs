namespace Replay;

public partial class MainWindow
{
    private OverlayNotificationService? overlays;
    private bool overlayRewindWasStarted, overlayRewindNotified;
    private OverlayMonitor? overlayRewindMonitor;
    private string? overlayRecordingSession, overlaySavedRecording;
    private readonly Dictionary<string, (Guid Operation, OverlayMonitor Monitor)> overlayCompressionJobs = new();

    private void CompressionOverlayStarted(CompressionJob job)
    {
        if (!exiting && overlayCompressionJobs.TryGetValue(job.Id, out var link))
            overlays?.BeginCompression(link.Operation, job.Id, link.Monitor);
    }
    private void CompressionOverlayFinished(CompressionJob job)
    {
        if (overlayCompressionJobs.Remove(job.Id)) overlays?.FinishCompression(job.Id, job.State == "Completed");
    }

    private OverlayMonitor OverlayTarget()
    {
        // Read the producer's frozen source, never a later editable UI selection.
        if (recorder != null) return recorder.OverlayTarget;
        return OverlayPlacement.Resolve();
    }

    private string? SaveRewindShortcut() => settings.HotkeyEnabled && settings.HotkeyKey != 0
        ? HotkeyText(settings.HotkeyModifiers, settings.HotkeyKey) : null;

    private void ObserveRewindOverlay()
    {
        if (exiting || overlays == null) return;
        if (recordingWanted && recorder?.Running == true && rewindTransition != RewindState.Stopping)
        {
            overlayRewindWasStarted = true;
            overlayRewindMonitor = recorder.OverlayTarget;
            // Wait for a usable ring buffer rather than announcing the start request.
            if (!overlayRewindNotified && recorder.HasReadableBufferData)
            { overlayRewindNotified = true; overlays.ShowRewindEnabled(SaveRewindShortcut()); }
        }
        else if (overlayRewindWasStarted && !recordingWanted && rewindTransition == null && rewindCommandError == null)
        {
            overlayRewindWasStarted = overlayRewindNotified = false;
            overlays.ShowRewindDisabled(overlayRewindMonitor);
        }
    }

    private void ObserveRecordingOverlay(RecordingProgress progress)
    {
        if (exiting || overlays == null) return;
        if (progress.State == RecordingState.Recording && progress.SessionDirectory != overlayRecordingSession)
        {
            overlayRecordingSession = progress.SessionDirectory;
            overlays.ShowRecordingStarted();
        }
        if (progress.State == RecordingState.Idle && progress.Result is { } clip && clip.Path != overlaySavedRecording)
        {
            overlaySavedRecording = clip.Path;
            overlays.ShowRecordingSaved(clip);
        }
    }
}
