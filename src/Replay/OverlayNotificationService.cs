using System.Windows;
using System.Windows.Threading;

namespace Replay;

// Commands supply accepted/published operations; this service owns only presentation.
internal sealed class OverlayNotificationService : IAsyncDisposable
{
    private sealed class Pending(OverlayNotification notification, OverlayMonitor monitor)
    {
        public OverlayNotification Notification { get; set; } = notification;
        public OverlayMonitor Monitor { get; } = monitor;
        public bool Removed { get; set; }
    }
    private sealed class Visible(Pending entry, OverlayWindow window, int slot, DispatcherTimer timer, CancellationTokenSource cancellation)
    {
        public Pending Entry { get; } = entry;
        public OverlayWindow Window { get; } = window;
        public OverlayMonitor Monitor => Entry.Monitor;
        public int Slot { get; set; } = slot;
        public DispatcherTimer Timer { get; } = timer;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public int Revision { get; set; }
    }
    private readonly Dispatcher dispatcher;
    private readonly Func<OverlayMonitor> monitor;
    private readonly List<Pending> pending = new();
    private readonly List<Visible> visible = new();
    private readonly HashSet<Pending> entries = new();
    private readonly HashSet<Task> thumbnailJobs = new();
    private readonly SemaphoreSlim thumbnailWorkers = new(2);
    private readonly CancellationTokenSource shutdown = new();
    private bool stopped, draining, drainScheduled;
    private Task? disposal;
    internal IReadOnlyList<OverlayWindow> Windows => visible.Select(v => v.Window).ToArray();
    internal int PendingCount => pending.Count;
    // Includes updates, so existing semantic publication observers still see completion.
    internal event Action<OverlayNotification>? Shown;

    public OverlayNotificationService(Dispatcher dispatcher, Func<OverlayMonitor> monitor)
    {
        this.dispatcher = dispatcher; this.monitor = monitor;
        if (Application.Current != null) Application.Current.Exit += ApplicationExit;
    }
    public void ShowRewindEnabled(string? shortcut) => Enqueue(new(OverlayNotificationKind.RewindEnabled, "Rewind активен",
        shortcut == null ? "Мгновенный повтор включён" : $"Нажмите {shortcut},\nчтобы сохранить Rewind", Emphasis: shortcut));
    public void ShowRewindDisabled(OverlayMonitor? target = null) => Enqueue(new(OverlayNotificationKind.RewindDisabled, "Rewind отключён"), target);
    public void ShowRecordingStarted() => Enqueue(new(OverlayNotificationKind.RecordingStarted, "Rewind", "Запись началась"));
    public void ShowMicrophoneEnabled() => Enqueue(new(OverlayNotificationKind.MicrophoneEnabled, "Rewind", "Микрофон включён"));
    public void ShowMicrophoneMuted() => Enqueue(new(OverlayNotificationKind.MicrophoneMuted, "Rewind", "Микрофон отключён"));
    public void BeginRewindSave(Guid operation, OverlayMonitor? target = null, bool queued = false, SaveClipStage? stage = null) => OnDispatcher(() =>
    {
        if (entries.Any(e => e.Notification.SaveOperationId == operation)) return;
        var state = stage ?? (queued ? SaveClipStage.Queued : SaveClipStage.Exporting);
        var body = state == SaveClipStage.Queued ? "Клип в очереди" : state == SaveClipStage.Preparing ? "Подготовка клипа" : "Сохранение клипа";
        Enqueue(new(OverlayNotificationKind.RewindSaving, "Rewind", body, SaveOperationId: operation), target);
    });
    public void PrepareRewindSave(Guid operation) => OnDispatcher(() =>
    {
        var entry = entries.FirstOrDefault(e => e.Notification.SaveOperationId == operation && e.Notification.Kind == OverlayNotificationKind.RewindSaving);
        if (entry?.Notification.Body == "Клип в очереди") Update(entry, entry.Notification with { Body = "Подготовка клипа" });
    });
    public void ExportRewindSave(Guid operation) => OnDispatcher(() =>
    {
        var entry = entries.FirstOrDefault(e => e.Notification.SaveOperationId == operation && e.Notification.Kind == OverlayNotificationKind.RewindSaving);
        if (entry != null && entry.Notification.Body != "Сохранение клипа") Update(entry, entry.Notification with { Body = "Сохранение клипа" });
    });
    public void CompleteRewindSave(Guid operation, SavedClip clip) => OnDispatcher(() =>
    {
        var entry = entries.FirstOrDefault(e => e.Notification.SaveOperationId == operation && !e.Notification.IsMicro);
        if (entry == null) return;
        if (!Published(clip.Path, out _)) { Remove(entry); return; }
        Update(entry, Saved(clip) with { SaveOperationId = operation });
    });
    public void FailRewindSave(Guid operation) => OnDispatcher(() =>
    {
        var entry = entries.FirstOrDefault(e => e.Notification.SaveOperationId == operation && e.Notification.Kind == OverlayNotificationKind.RewindSaving);
        if (entry != null) Remove(entry);
    });
    public void BeginCompression(Guid operation, string jobId, OverlayMonitor? target = null) => OnDispatcher(() =>
    {
        if (entries.Any(e => e.Notification.CompressionJobId == jobId ||
            e.Notification.SaveOperationId == operation && e.Notification.Kind == OverlayNotificationKind.RewindSaving)) return;
        var parent = entries.FirstOrDefault(e => e.Notification.SaveOperationId == operation && !e.Notification.IsMicro);
        Enqueue(new(OverlayNotificationKind.CompressionStarted, "Rewind", "Сжатие видео...", SaveOperationId: operation, CompressionJobId: jobId), parent?.Monitor ?? target);
    });
    public void FinishCompression(string jobId, bool success) => OnDispatcher(() =>
    {
        var entry = entries.FirstOrDefault(e => e.Notification.CompressionJobId == jobId);
        if (entry == null) return;
        if (!success) { Remove(entry); return; }
        Update(entry, entry.Notification with { Kind = OverlayNotificationKind.CompressionCompleted, Body = "Готово" });
    });
    private static OverlayNotification Saved(SavedClip clip)
    {
        var duration = DurationFormatting.HumanSeconds((int)Math.Round(clip.Duration, MidpointRounding.AwayFromZero));
        return new(OverlayNotificationKind.RewindSaved, "Rewinded!", "Сохранены\nпоследние " + duration,
            MediaPath: clip.Path, Duration: clip.Duration, Emphasis: duration);
    }
    public void ShowRewindSaved(SavedClip clip) { if (Published(clip.Path, out _)) Enqueue(Saved(clip)); }
    public void ShowRecordingSaved(SavedClip clip)
    {
        if (!Published(clip.Path, out var size)) return;
        Enqueue(new(OverlayNotificationKind.RecordingSaved, "Rewind", "Запись сохранена",
            DurationFormatting.Recording(clip.Duration, padMinutes: true) + " · " + OverlayNotification.FileSize(size), clip.Path, clip.Duration));
    }
    public void ShowScreenshotSaved(SavedScreenshot screenshot, OverlayMonitor? target = null)
    {
        if (!Published(screenshot.Path, out var size)) return;
        Enqueue(new(OverlayNotificationKind.ScreenshotSaved, "Rewind", "Скриншот сохранён", OverlayNotification.FileSize(size), screenshot.Path), target);
    }
    private static bool Published(string path, out long size)
    {
        size = 0;
        try { var file = new FileInfo(path); if (!file.Exists || file.Length <= 0) return false; size = file.Length; return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    private void OnDispatcher(Action action)
    {
        if (!dispatcher.CheckAccess()) { _ = dispatcher.BeginInvoke(() => OnDispatcher(action)); return; }
        if (stopped) return;
        try { action(); } catch (Exception ex) { ClipFiles.Log("overlay-notification", ex); }
    }
    private void Enqueue(OverlayNotification notification, OverlayMonitor? target = null) => OnDispatcher(() =>
    {
        SaveTiming.ForOperation(notification.SaveOperationId)?.Mark("save-notification-emitted", new { notification.Kind, notification.Body });
        var entry = new Pending(notification, target ?? monitor());
        entries.Add(entry); pending.Add(entry); RequestDrain();
    });
    private void Update(Pending entry, OverlayNotification notification)
    {
        SaveTiming.ForOperation(notification.SaveOperationId)?.Mark("save-notification-emitted", new { notification.Kind, notification.Body });
        entry.Notification = notification;
        var item = visible.FirstOrDefault(v => v.Entry == entry);
        if (item == null) return; // Queued operations retain the latest state until their FIFO turn.
        item.Revision++; item.Timer.Stop();
        item.Window.Revive(); item.Window.ApplyNotification(notification);
        SaveTiming.ForOperation(notification.SaveOperationId)?.NotificationDisplayed(dispatcher, item.Window, notification);
        StartLifetime(item); HotkeyDiagnostics.Mark("overlay-shown", new { Kind = notification.Kind.ToString(), notification.Title, notification.Body }); Shown?.Invoke(notification);
        if (notification.Kind == OverlayNotificationKind.RewindSaved) SaveTiming.ForOperation(notification.SaveOperationId)?.OverlayDisplayed(dispatcher);
        if (notification.Kind == OverlayNotificationKind.RecordingSaved && notification.MediaPath is { } recorded)
            SaveTiming.ForMedia(recorded)?.OverlayDisplayed(dispatcher);
        if (notification.HasThumbnail) StartThumbnail(item);
    }
    private void RequestDrain()
    {
        if (stopped || drainScheduled) return;
        drainScheduled = true;
        // HWND creation may pump messages. Never show inside WM_HOTKEY/state updates.
        _ = dispatcher.BeginInvoke(DispatcherPriority.Background, () => { drainScheduled = false; Drain(); });
    }
    private void Drain()
    {
        if (stopped || draining || visible.Any(v => v.Window.IsMoving)) return;
        draining = true;
        try
        {
            while (!stopped && visible.Count < 3 && pending.Count > 0)
            {
                var next = pending[0];
                var occupied = visible.Where(v => v.Monitor.Handle == next.Monitor.Handle).Select(v => v.Slot).ToHashSet();
                var slot = Enumerable.Range(0, OverlayPlacement.Capacity(next.Monitor)).FirstOrDefault(s => !occupied.Contains(s), -1);
                var parent = visible.FirstOrDefault(v => Related(v.Entry.Notification, next.Notification));
                if (parent != null) slot = parent.Slot;
                if (slot < 0) break;
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
                OverlayWindow? window = null;
                Visible? item = null;
                try
                {
                    window = new OverlayWindow(next.Notification);
                    window.PlacementSettled += RequestDrain;
                    item = new(next, window, slot, timer, cancellation);
                    var layout = Layout(visible.Append(item), next.Monitor);
                    var bounds = layout[item];
                    var margin = OverlayPlacement.Margin * next.Monitor.Scale * bounds.ContentScale;
                    if (layout.Any(p => p.Value.Top + p.Value.Height > next.Monitor.Bottom - margin + 1))
                    { window.Close(); cancellation.Dispose(); break; }
                    pending.RemoveAt(0);
                    // Register before Show: reentrant callbacks can update/cancel this operation.
                    visible.Add(item);
                    foreach (var old in layout.Where(p => p.Key != item)) old.Key.Window.Reposition(old.Value, false);
                    window.ShowAt(bounds);
                    if (next.Removed || stopped) { Remove(item); continue; }
                    window.ApplyNotification(next.Notification);
                    SaveTiming.ForOperation(next.Notification.SaveOperationId)?.NotificationDisplayed(dispatcher, window, next.Notification);
                    timer.Tick += (_, _) =>
                    {
                        timer.Stop(); var revision = item.Revision;
                        window.FadeOut(() => { if (item.Revision == revision) Remove(item); });
                    };
                    StartLifetime(item); HotkeyDiagnostics.Mark("overlay-shown", new { Kind = next.Notification.Kind.ToString(), next.Notification.Title, next.Notification.Body }); Shown?.Invoke(next.Notification);
                    if (next.Notification.Kind == OverlayNotificationKind.RewindSaved) SaveTiming.ForOperation(next.Notification.SaveOperationId)?.OverlayDisplayed(dispatcher);
                    if (next.Notification.Kind == OverlayNotificationKind.RecordingSaved && next.Notification.MediaPath is { } recorded)
                        SaveTiming.ForMedia(recorded)?.OverlayDisplayed(dispatcher);
                    if (next.Notification.HasThumbnail) StartThumbnail(item);
                }
                catch (Exception ex)
                {
                    if (item != null && visible.Contains(item)) Remove(item);
                    else { window?.Close(); cancellation.Dispose(); pending.Remove(next); entries.Remove(next); }
                    ClipFiles.Log("overlay-window", ex);
                }
            }
        }
        finally { draining = false; }
    }
    private static bool Related(OverlayNotification primary, OverlayNotification micro) =>
        !primary.IsMicro && micro.IsMicro && primary.SaveOperationId != null && primary.SaveOperationId == micro.SaveOperationId;
    private static Dictionary<Visible, OverlayBounds> Layout(IEnumerable<Visible> items, OverlayMonitor monitor)
    {
        var all = items.Where(v => v.Monitor.Handle == monitor.Handle).OrderBy(v => v.Slot).ToList();
        var ordered = new List<Visible>();
        foreach (var item in all.Where(v => !v.Entry.Notification.IsMicro || !all.Any(p => Related(p.Entry.Notification, v.Entry.Notification))))
        {
            ordered.Add(item);
            ordered.AddRange(all.Where(v => Related(item.Entry.Notification, v.Entry.Notification)));
        }
        var result = new Dictionary<Visible, OverlayBounds>();
        double bottom = 0;
        Visible? previous = null;
        foreach (var item in ordered)
        {
            var linked = previous != null && Related(previous.Entry.Notification, item.Entry.Notification);
            var top = linked ? bottom + 20 : Math.Max(item.Slot * (OverlayPlacement.CardHeight + OverlayPlacement.Gap), previous == null ? 0 : bottom + OverlayPlacement.Gap);
            var bounds = OverlayPlacement.BoundsAt(monitor, top, item.Entry.Notification.Height);
            // Do not clamp an overflowing stack into another card; Drain keeps it queued.
            var scale = monitor.Scale * bounds.ContentScale;
            bounds = bounds with { Top = monitor.Top + (int)Math.Round((OverlayPlacement.Margin + top) * scale) };
            result[item] = bounds; bottom = top + item.Entry.Notification.Height; previous = item;
        }
        return result;
    }
    private static void StartLifetime(Visible item)
    {
        item.Timer.Interval = item.Entry.Notification.Lifetime;
        if (!item.Entry.Notification.IsOngoing) item.Timer.Start();
    }
    private void StartThumbnail(Visible item)
    {
        var job = UpdateThumbnail(item, item.Entry.Notification);
        thumbnailJobs.Add(job); _ = ForgetThumbnail(job);
    }
    private async Task UpdateThumbnail(Visible item, OverlayNotification notification)
    {
        var timing = SaveTiming.ForOperation(notification.SaveOperationId);
        using var timingScope = timing?.Enter();
        timing?.Mark("thumbnail-queued");
        var token = item.Cancellation.Token;
        try
        {
            await thumbnailWorkers.WaitAsync(token);
            timing?.Mark("thumbnail-started");
            try
            {
                var image = await Task.Run(() => OverlayThumbnail.Load(notification, token), token);
                timing?.Mark("thumbnail-completed", new { Loaded = image != null });
                if (image != null && !token.IsCancellationRequested && !stopped && visible.Contains(item) && item.Entry.Notification == notification)
                    item.Window.SetThumbnail(image);
            }
            finally { thumbnailWorkers.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { ClipFiles.Log("overlay-thumbnail", ex, notification.MediaPath); }
    }
    private async Task ForgetThumbnail(Task job) { await job; thumbnailJobs.Remove(job); }
    private void Remove(Pending entry)
    {
        entry.Removed = true; entries.Remove(entry); pending.Remove(entry);
        var item = visible.FirstOrDefault(v => v.Entry == entry);
        if (item != null) Remove(item); else RequestDrain();
    }
    private void Remove(Visible item)
    {
        if (!visible.Remove(item)) return;
        item.Entry.Removed = true; entries.Remove(item.Entry);
        item.Timer.Stop(); item.Cancellation.Cancel(); item.Window.Close(); item.Cancellation.Dispose();
        // A linked micro inherits its parent's slot, moving independently into the vacancy.
        foreach (var pair in Layout(visible, item.Monitor)) pair.Key.Window.Reposition(pair.Value);
        RequestDrain();
    }
    private void ApplicationExit(object? sender, ExitEventArgs e) => Stop();
    private void Stop()
    {
        if (stopped) return;
        stopped = true; shutdown.Cancel(); pending.Clear(); entries.Clear();
        foreach (var item in visible.ToArray()) Remove(item);
        if (Application.Current != null) Application.Current.Exit -= ApplicationExit;
    }
    public ValueTask DisposeAsync() => new(disposal ??= DisposeCore());
    private async Task DisposeCore()
    {
        Stop();
        try { await Task.WhenAll(thumbnailJobs.ToArray()).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException ex) { ClipFiles.Log("overlay-shutdown", ex); }
    }
}
