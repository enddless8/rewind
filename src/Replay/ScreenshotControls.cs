using System.Windows;

namespace Replay;

public partial class MainWindow
{
    private bool screenshotCapturing;
    private CancellationTokenSource? screenshotCancellation;
    private Task<SavedScreenshot>? screenshotOperation;
    private SavedScreenshot? lastScreenshot;

    private ScreenshotRequest ScreenshotSelection()
    {
        var mode = SelectedTag(CaptureMode);
        var game = mode == "Game" ? CaptureSource.ResolveGame(GamePicker.SelectedItem as GameWindow, foregroundGame) : null;
        var display = MonitorPicker.SelectedItem as Display ?? throw new InvalidOperationException("Нет доступного монитора.");
        string? application = game == null ? null : ClipFiles.ApplicationName(settings, game);
        if (game != null && GamePicker.SelectedItem is GameWindow selected && selected.Exe == game.Exe && !string.IsNullOrWhiteSpace(GameNameBox.Text))
            application = GameNameBox.Text.Trim();
        return new(mode, HideBorderCheck.IsChecked == true, int.Parse(SelectedTag(FpsPicker)), game, display,
            FolderBox.Text.Trim(), GroupByApplicationCheck.IsChecked == true, application);
    }

    // Shared command for the button and a future global shortcut; owns no Recorder/audio.
    private async Task TakeScreenshot()
    {
        if (screenshotCapturing || exiting) return;
        screenshotCapturing = true;
        using var cancellation = new CancellationTokenSource();
        screenshotCancellation = cancellation;
        UpdateMainPresentation();
        try
        {
            var selection = ScreenshotSelection();
            var overlayMonitor = selection.Mode == "Game" ? OverlayPlacement.Resolve(captureWindow: selection.Game?.Handle ?? 0)
                : OverlayPlacement.Resolve(selection.Display.Handle);
            screenshotOperation = ScreenshotCapture.Take(selection, cancellation.Token);
            lastScreenshot = await screenshotOperation;
            overlays?.ShowScreenshotSaved(lastScreenshot, overlayMonitor);
            LastClipText.Text = "Скриншот сохранён: " + lastScreenshot.Path;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (TimeoutException ex)
        {
            LastClipText.Text = "Скриншот не сохранён: источник не ответил. Повтори попытку.";
            ClipFiles.Log("screenshot-timeout", ex);
        }
        catch (Exception ex)
        {
            LastClipText.Text = "Скриншот не сохранён: " + ex.Message;
            ClipFiles.Log("screenshot", ex);
        }
        finally
        {
            screenshotOperation = null; screenshotCancellation = null; screenshotCapturing = false;
            UpdateMainPresentation();
        }
    }

    private void UpdateScreenshotPresentation()
    {
        ScreenshotButton.IsEnabled = !screenshotCapturing && !exiting;
        ScreenshotButton.Opacity = screenshotCapturing ? .65 : 1;
        ScreenshotButton.ToolTip = screenshotCapturing ? "Делаю скриншот…" : "Сделать скриншот выбранного источника";
        System.Windows.Automation.AutomationProperties.SetName(ScreenshotButton, "Сделать скриншот");
        System.Windows.Automation.AutomationProperties.SetItemStatus(ScreenshotButton, screenshotCapturing ? "Захватываю изображение" : "Доступно");
    }

    private async void Screenshot_Click(object sender, RoutedEventArgs e) => await TakeScreenshot();
}
