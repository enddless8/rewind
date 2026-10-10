using System.Text;
using System.Text.RegularExpressions;

namespace Replay;
internal static class ClipFiles
{
    // Standalone diagnostics harness only; normal application logging is unchanged.
    internal static string? DiagnosticDirectoryForTests { get; set; }
    public static string ApplicationName(Settings settings, GameWindow? game) => game == null ? "Desktop" : settings.GameNames.GetValueOrDefault(game.Exe, game.Exe);
    public static string OutputFolder(string output, bool grouped, string? application) => grouped && application != null
        ? Path.Combine(output, SafeName(application)) : output;
    public static string NewId() => DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff") + "_" + Guid.NewGuid().ToString("N")[..4];
    internal static string SafeName(string value)
    { var name = string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.'); if (Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase)) name = "Game-" + name; return string.IsNullOrEmpty(name) || name is "." or ".." ? "Unknown" : name[..Math.Min(name.Length, 100)]; }

    internal static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xffff) is 32 or 33;
    internal static async Task RetrySharing(Action operation, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { operation(); return; }
            catch (IOException ex) when (IsSharingViolation(ex) && attempt < 7)
            { await Task.Delay(Math.Min(50 * (1 << attempt), 400), cancellationToken); }
        }
    }
    public static async Task PublishFile(string temporary, string final, CancellationToken cancellationToken = default)
    {
        // Publish by rename on the destination volume; never overwrite an existing result.
        var incoming = Path.Combine(Path.GetDirectoryName(final)!, ".publishing-" + Guid.NewGuid().ToString("N") + Path.GetExtension(final));
        if (string.Equals(Path.GetPathRoot(temporary), Path.GetPathRoot(final), StringComparison.OrdinalIgnoreCase))
            await RetrySharing(() => File.Move(temporary, final), cancellationToken);
        else
        {
            try
            {
                using (var input = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                using (var output = new FileStream(incoming, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    await input.CopyToAsync(output, cancellationToken);
                await RetrySharing(() => File.Move(incoming, final), cancellationToken);
            }
            finally { try { File.Delete(incoming); } catch { } }
        }
    }
    public static async Task<string?> Publish(string temporaryVideo, string finalVideo, string metadata)
    {
        await PublishFile(temporaryVideo, finalVideo);
        SaveTiming.Current?.Mark("playable-original-available", new { Path = finalVideo });
        try
        {
            // No rename of freshly written JSON: indexers/AV can lock it on removable/network drives.
            await RetrySharing(() => File.WriteAllText(Path.ChangeExtension(finalVideo, ".json"), metadata, new UTF8Encoding(false)));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log("metadata", ex, finalVideo);
            return "Видео сохранено, но не удалось записать JSON с диагностикой. Подробности в журнале Rewind.";
        }
    }
    public static void Log(string stage, Exception exception, string? path = null)
    {
        try
        {
            var directory = DiagnosticDirectoryForTests ?? Settings.DataDirectory;
            Directory.CreateDirectory(directory);
            var log = Path.Combine(directory, "errors.log");
            if (File.Exists(log) && new FileInfo(log).Length > 2 * 1024 * 1024) File.Move(log, log + ".old", true);
            File.AppendAllText(log, $"{DateTimeOffset.Now:O} [{stage}] {path}\n{exception}\n\n");
        }
        catch { /* Logging must never turn a saved clip into a failed save. */ }
    }
}
