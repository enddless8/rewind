using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace Replay;

// Explicit opt-in. Bounded background logging never waits on disk in a window procedure.
internal static class HotkeyDiagnostics
{
    private sealed record Entry(string Path, string Json);
    private static readonly Channel<Entry> entries = Channel.CreateBounded<Entry>(new BoundedChannelOptions(1024)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    internal static event Action<string, object?>? Observed;
    static HotkeyDiagnostics() => _ = Task.Run(async () =>
    {
        await foreach (var entry in entries.Reader.ReadAllAsync())
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(entry.Path))!);
                if (File.Exists(entry.Path) && new FileInfo(entry.Path).Length > 2 * 1024 * 1024)
                    File.Move(entry.Path, entry.Path + ".old", true);
                await File.AppendAllTextAsync(entry.Path, entry.Json + "\n");
            }
            catch (Exception ex) { Debug.WriteLine("Hotkey diagnostics: " + ex.Message); }
    });
    internal static void Mark(string stage, object? details = null)
    {
        try { Observed?.Invoke(stage, details); } catch { /* Test observers cannot break input. */ }
        var path = Environment.GetEnvironmentVariable("REWIND_HOTKEY_DIAGNOSTICS_FILE");
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            entries.Writer.TryWrite(new(path, JsonSerializer.Serialize(new { Stage = stage,
                Time = DateTimeOffset.Now, Tick = Stopwatch.GetTimestamp(), Process = Environment.ProcessId,
                Thread = Environment.CurrentManagedThreadId, Details = details })));
        }
        catch (Exception ex) { Debug.WriteLine("Hotkey diagnostics: " + ex.Message); }
    }
}
