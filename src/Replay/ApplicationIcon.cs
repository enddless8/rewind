using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Image = System.Windows.Controls.Image;

namespace Replay;

// Presentation only: the selector still contains its original GameWindow/string items.
public static class ApplicationIcon
{
    public static readonly DependencyProperty EntryProperty = DependencyProperty.RegisterAttached(
        "Entry", typeof(object), typeof(ApplicationIcon), new PropertyMetadata(null, EntryChanged));
    public static object? GetEntry(DependencyObject target) => target.GetValue(EntryProperty);
    public static void SetEntry(DependencyObject target, object? value) => target.SetValue(EntryProperty, value);

    private static readonly ConditionalWeakTable<GameWindow, Lazy<Task<BitmapSource?>>> requests = new();
    private static readonly ConcurrentDictionary<ExecutableIdentity, Lazy<BitmapSource?>> executables = new();
    private static readonly ConcurrentQueue<ExecutableIdentity> cacheOrder = new();
    private static readonly ConcurrentDictionary<WindowIconIdentity, Lazy<BitmapSource>> windowIcons = new();
    private static readonly ConcurrentQueue<WindowIconIdentity> windowCacheOrder = new();
    private static readonly SemaphoreSlim workers = new(2);
    private readonly record struct ExecutableIdentity(string Path, long Modified, long Length);
    private readonly record struct WindowIconIdentity(nint Window, int Process, nint Icon);

    private static async void EntryChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not Image image) return;
        image.Source = null;
        image.Visibility = args.NewValue is GameWindow ? Visibility.Visible : Visibility.Collapsed;
        if (args.NewValue is not GameWindow entry) return;
        try
        {
            var source = await requests.GetValue(entry, game => new(() => LoadAsync(game))).Value;
            // A refresh/recycled template may have replaced this item during extraction.
            if (ReferenceEquals(GetEntry(image), entry)) image.Source = source;
        }
        catch { /* An optional icon must never interrupt selection or capture. */ }
    }

    private static async Task<BitmapSource?> LoadAsync(GameWindow entry)
    {
        await workers.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(() => Resolve(entry)).ConfigureAwait(false); }
        finally { workers.Release(); }
    }

    private static BitmapSource? Resolve(GameWindow entry)
    {
        try
        {
            GetWindowThreadProcessId(entry.Handle, out var windowProcess);
            if (windowProcess != entry.ProcessId) return null;
            using var process = Process.GetProcessById(entry.ProcessId);
            if (!process.ProcessName.Equals(entry.Exe, StringComparison.OrdinalIgnoreCase)) return null;
            try
            {
                if (process.MainModule?.FileName is { } path)
                {
                    var file = new FileInfo(path);
                    var identity = new ExecutableIdentity(path.ToUpperInvariant(), file.LastWriteTimeUtc.Ticks, file.Length);
                    var cached = executables.GetOrAdd(identity, key =>
                    {
                        cacheOrder.Enqueue(key);
                        return new Lazy<BitmapSource?>(() => ExtractExecutable(path));
                    });
                    var icon = cached.Value;
                    while (executables.Count > 256 && cacheOrder.TryDequeue(out var old)) executables.TryRemove(old, out _);
                    if (icon != null) return icon;
                }
            }
            catch { /* Protected processes/missing files can still expose a window icon. */ }
            return ExtractWindow(entry);
        }
        catch { return null; }
    }

    private static BitmapSource? ExtractExecutable(string path)
    {
        var small = new nint[1];
        try
        {
            return ExtractIconEx(path, 0, 0, small, 1) > 0 && small[0] != 0 ? Copy(small[0]) : null;
        }
        catch { return null; }
        finally { if (small[0] != 0) DestroyIcon(small[0]); }
    }

    private static BitmapSource? ExtractWindow(GameWindow entry)
    {
        GetWindowThreadProcessId(entry.Handle, out var process);
        if (process != entry.ProcessId) return null;
        // WM_GETICON handles are borrowed. Bounded calls cannot hang on a stalled app.
        foreach (var kind in new nint[] { 2, 0 })
            if (SendMessageTimeout(entry.Handle, 0x007F, kind, 0, 2, 100, out var icon) != 0 && icon != 0)
                return CopyWindowIcon(entry, icon);
        var classIcon = GetClassLongPtr(entry.Handle, -34 /* GCLP_HICONSM */);
        return classIcon != 0 ? CopyWindowIcon(entry, classIcon) : null;
    }

    private static BitmapSource CopyWindowIcon(GameWindow entry, nint icon)
    {
        // Re-query the borrowed handle on refresh, so a changed window icon gets
        // a new bitmap while the same live window reuses its previous copy.
        var identity = new WindowIconIdentity(entry.Handle, entry.ProcessId, icon);
        var cached = windowIcons.GetOrAdd(identity, key =>
        {
            windowCacheOrder.Enqueue(key);
            return new Lazy<BitmapSource>(() => Copy(icon));
        });
        var source = cached.Value;
        while (windowIcons.Count > 256 && windowCacheOrder.TryDequeue(out var old)) windowIcons.TryRemove(old, out _);
        return source;
    }

    private static BitmapSource Copy(nint icon)
    {
        var bitmap = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(16, 16));
        bitmap.Freeze();
        return bitmap;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string path, int index, nint large, [Out] nint[] small, uint count);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static extern nint SendMessageTimeout(nint window, uint message, nint command, nint data, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static extern nint GetClassLongPtr(nint window, int index);
}
