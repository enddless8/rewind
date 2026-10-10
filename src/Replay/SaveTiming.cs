using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Threading;

namespace Replay;

// Opt-in diagnostics only; neither settings nor the publication contract change.
internal sealed class SaveTiming : IDisposable
{
    private static readonly AsyncLocal<SaveTiming?> ambient = new();
    private static readonly ConcurrentDictionary<Guid, SaveTiming> operations = new();
    private static readonly ConcurrentDictionary<string, SaveTiming> media = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim logGate = new(1);
    internal static event Action<Guid, string, double, object?>? Observed;
    internal static SaveTiming? Current => ambient.Value;
    private readonly SaveTiming? previous;
    private readonly long origin = Stopwatch.GetTimestamp();
    private readonly string log;
    private readonly Guid operation;

    private SaveTiming(Guid operation, string log)
    { this.operation = operation; this.log = log; previous = ambient.Value; ambient.Value = this; }
    internal static SaveTiming? Begin(Guid operation)
    {
        var path = Environment.GetEnvironmentVariable("REWIND_SAVE_TIMINGS_FILE");
        if (string.IsNullOrWhiteSpace(path)) return null;
        var result = new SaveTiming(operation, path);
        operations[operation] = result;
        if (operations.Count > 64)
            foreach (var old in operations.Values.OrderBy(t => t.origin).Take(operations.Count - 64))
            {
                operations.TryRemove(old.operation, out _);
                foreach (var pair in media.Where(p => p.Value == old)) media.TryRemove(pair.Key, out _);
            }
        return result;
    }
    internal static SaveTiming? ForOperation(Guid? id) => id is { } key && operations.TryGetValue(key, out var value) ? value : null;
    internal static SaveTiming? ForMedia(string path) => media.GetValueOrDefault(path);
    internal void Associate(string path) => media[path] = this;
    internal void Mark(string stage, object? details = null)
    {
        try
        {
            var tick = Stopwatch.GetTimestamp();
            var elapsed = (tick - origin) * 1000.0 / Stopwatch.Frequency;
            var line = JsonSerializer.Serialize(new { Operation = operation, Stage = stage, ElapsedMs = elapsed, Tick = tick, Frequency = Stopwatch.Frequency, Details = details });
            // Keep file I/O off the save/UI path. Tick, rather than append order, defines ordering.
            _ = Task.Run(async () =>
            {
                await logGate.WaitAsync();
                try { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(log))!); await File.AppendAllTextAsync(log, line + "\n"); }
                catch { }
                finally { logGate.Release(); }
            });
            try { Observed?.Invoke(operation, stage, elapsed, details); } catch { }
        }
        catch { /* Diagnostics must not turn valid media into a failed save. */ }
    }
    internal IDisposable Enter()
    { var old = ambient.Value; ambient.Value = this; return new Restore(old); }
    private sealed class Restore(SaveTiming? old) : IDisposable
    { public void Dispose() => ambient.Value = old; }
    public void Dispose() => ambient.Value = previous;

    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    internal void OverlayDisplayed(Dispatcher dispatcher)
    {
        Mark("success-notification-applied");
        EventHandler? rendered = null;
        rendered = (_, _) =>
        {
            CompositionTarget.Rendering -= rendered;
            Mark("success-first-wpf-render");
            // Rendering fires before submission; flush after WPF has returned to idle.
            _ = dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
            {
                _ = Task.Run(() => { var code = DwmFlush(); Mark("success-compositor-flushed", new { Result = code }); });
            });
        };
        CompositionTarget.Rendering += rendered;
    }
}
