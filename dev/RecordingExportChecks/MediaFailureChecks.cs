using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Replay;

internal static class MediaFailureChecks
{
    internal static async Task Run(string root)
    {
        var baseline = Process.GetProcessesByName("ffmpeg").Select(p => { using (p) return p.Id; }).ToHashSet();
        List<string> checks = [];
        string[] args = ["-v", "error", "-re", "-f", "lavfi", "-i", "color=s=64x64:r=30", "-f", "null", "-"];
        try { await MediaTools.Run("ffmpeg", args, 1, bindLifetime: true); throw new Exception("Timeout не сработал."); }
        catch (TimeoutException) { checks.Add("Real FFmpeg timeout reaps child"); }
        using var cancellation = new CancellationTokenSource(400);
        try { await MediaTools.Run("ffmpeg", args, 30, cancellation.Token, bindLifetime: true); throw new Exception("Cancellation не сработала."); }
        catch (OperationCanceledException) { checks.Add("Real FFmpeg cancellation reaps child"); }
        var result = await MediaTools.Run("ffmpeg", ["-v", "error", "-i", Path.Combine(root, "absent.mkv"), "-f", "null", "-"], bindLifetime: true);
        if (result.Code == 0) throw new Exception("Отсутствующий source принят.");
        checks.Add("Real FFmpeg input error returned");
        if (Process.GetProcessesByName("ffmpeg").Any(p => { using (p) return !baseline.Contains(p.Id); })) throw new Exception("Остался FFmpeg process.");
        checks.Add("No child process leak");
        File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = true, count = checks.Count, checks }));
    }
}
