using System.Globalization;

namespace Replay;

// Presentation only: state and elapsed interval always come from the shared backend.
public readonly record struct RecordingPresentation(bool Enabled, bool StopIcon, bool Transient,
    string Label, string Status, bool ShowDuration, string Duration, string Feedback)
{
    public static RecordingPresentation From(RecordingProgress progress, bool canStart)
    {
        var state = progress.State;
        var transient = state is RecordingState.Starting or RecordingState.Stopping or RecordingState.Finalizing;
        var stop = state is RecordingState.Recording or RecordingState.Stopping or RecordingState.Finalizing;
        var label = stop ? "Остановить запись" : "Начать запись";
        var status = state switch
        {
            RecordingState.Starting => "Запускаю запись…",
            RecordingState.Stopping => "Останавливаю запись…",
            RecordingState.Finalizing => "Сохраняю запись…",
            _ => label
        };
        var feedback = state == RecordingState.Error
            ? progress.Result is { } partial
                ? $"Часть записи сохранена · {Path.GetFileName(partial.Path)}. {progress.Error}"
                : $"Запись не сохранена: {progress.Error}" + (progress.SessionDirectory != null ? ". Временные данные оставлены для восстановления." : "")
            : state == RecordingState.Idle && progress.Result is { } result
                ? $"Запись сохранена · {Path.GetFileName(result.Path)}"
                : "";
        return new(!transient && (state == RecordingState.Recording || canStart), stop, transient,
            label, status, stop, FormatDuration(progress.Duration), feedback);
    }

    public static string FormatDuration(double duration)
    {
        return DurationFormatting.Recording(duration);
    }
}
