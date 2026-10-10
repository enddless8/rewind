namespace Replay;

public partial class MainWindow
{
    // Shared serialized commands for buttons and global hotkeys.
    private readonly SemaphoreSlim conventionalCommands = new(1, 1);
    private RecordingProgress lastConventionalRecording = new(RecordingState.Idle, 0, null, null, null);
    private string? lastConventionalNotice;
    private bool conventionalStarting;
    public RecordingProgress ConventionalRecording => conventionalStarting ? new(RecordingState.Starting, 0, null, null, null) : recorder?.Recording ?? lastConventionalRecording;
    public IReadOnlyList<IncompleteRecording> IncompleteRecordings { get; private set; } = [];
    private async Task StartRecording()
    {
        await conventionalCommands.WaitAsync();
        bool ownsBusy = false;
        try
        {
            if (exiting || busy || micTestStarting || ConventionalRecording.State == RecordingState.Recording) return;
            conventionalStarting = true;
            lastConventionalNotice = null;
            recordingFeedback = "";
            UpdateMainPresentation();
            await StopMicrophoneTest();
            if (recorder?.Running != true)
            {
                busy = ownsBusy = true;
                if (recorder != null) { await recorder.DisposeAsync(); recorder = null; }
                ReadSettings();
                var game = CaptureSource.ResolveGame(GamePicker.SelectedItem as GameWindow, foregroundGame);
                var display = MonitorPicker.SelectedItem as Display ?? throw new InvalidOperationException("Нет доступного монитора.");
                recorder = new Recorder(settings, game, display, microphoneMute);
                await recorder.SetRewindEnabled(recordingWanted);
                try { await recorder.Start(); UpdateCaptureMicrophoneGain(); }
                catch { await recorder.DisposeAsync(); recorder = null; throw; }
            }
            await recorder.StartRecording(); SetCaptureSettingsEnabled(false);
        }
        catch (Exception ex)
        {
            lastConventionalRecording = new(RecordingState.Error, 0, ex.Message, null, recorder?.Recording.SessionDirectory);
            if (!recordingWanted && recorder != null)
            { await recorder.DisposeAsync(); recorder = null; SetCaptureSettingsEnabled(true); }
            throw;
        }
        finally { conventionalStarting = false; if (ownsBusy) busy = false; conventionalCommands.Release(); UpdateMainPresentation(); }
    }
    private async Task StopRecording()
    {
        await conventionalCommands.WaitAsync();
        try
        {
            if (recorder == null) return;
            var stopping = recorder.StopRecording();
            UpdateMainPresentation();
            await stopping;
            lastConventionalRecording = recorder.Recording;
            ObserveRecordingOverlay(lastConventionalRecording);
            if (!recordingWanted) { await recorder.DisposeAsync(); recorder = null; SetCaptureSettingsEnabled(true); }
        }
        finally { conventionalCommands.Release(); UpdateMainPresentation(); }
    }

    private string recordingFeedback = "";
    private async void Recording_Click(object sender, System.Windows.RoutedEventArgs e) => await ToggleRecording();

    private async Task ToggleRecording()
    {
        var state = ConventionalRecording.State;
        if (!ToggleButton.IsEnabled || state is RecordingState.Starting or RecordingState.Stopping or RecordingState.Finalizing) return;
        try
        {
            if (state == RecordingState.Recording) await StopRecording();
            else await StartRecording();
        }
        catch (Exception ex)
        {
            // Preserve backend session/result information; the UI reports command errors only.
            ClipFiles.Log("recording-command", ex, ConventionalRecording.SessionDirectory);
            UpdateMainPresentation();
        }
    }

    private void UpdateRecordingPresentation()
    {
        var progress = ConventionalRecording;
        ObserveRecordingOverlay(progress);
        // Retain automatic completion/error when a later Rewind command releases the producer.
        if (progress.State == RecordingState.Error || progress.Result != null) lastConventionalRecording = progress;
        var presentation = RecordingPresentation.From(progress, !exiting && !busy && !micTestStarting && !recovering);
        ToggleButton.IsEnabled = presentation.Enabled;
        ToggleButton.ToolTip = presentation.Status;
        System.Windows.Automation.AutomationProperties.SetName(ToggleButton, presentation.Status);
        System.Windows.Automation.AutomationProperties.SetItemStatus(ToggleButton, presentation.Status);
        RecordingLabel.Text = presentation.Label;
        RecordingIcon.Source = (System.Windows.Media.ImageSource)FindResource(presentation.StopIcon ? "MainRecordingStopIcon" : "MainFigmaPlayIcon");
        RecordingIcon.Height = presentation.StopIcon ? 71 : 86;
        RecordingIcon.RenderTransform = new System.Windows.Media.TranslateTransform(presentation.StopIcon ? 0 : 9, presentation.StopIcon ? 0 : .5);
        RecordingIcon.Opacity = presentation.Transient ? .55 : 1;
        RecordingDurationText.Text = presentation.Duration;
        RecordingDurationText.Visibility = presentation.ShowDuration ? System.Windows.Visibility.Visible : System.Windows.Visibility.Hidden;
        System.Windows.Automation.AutomationProperties.SetName(RecordingDurationText, "Длительность записи: " + presentation.Duration);

        if (presentation.Feedback.Length != 0) recordingFeedback = presentation.Feedback;
        if (progress.State == RecordingState.Error && presentation.Feedback != lastConventionalNotice)
        {
            lastConventionalNotice = presentation.Feedback;
            Notify(progress.Result != null ? "Часть записи сохранена" : "Ошибка записи", presentation.Feedback, true);
        }
        var feedback = presentation.Transient ? presentation.Status : recordingFeedback;
        if (feedback.Length != 0) MainStateText.Text = MainStateText.Text.Length == 0 ? feedback : MainStateText.Text + " · " + feedback;
    }
}
