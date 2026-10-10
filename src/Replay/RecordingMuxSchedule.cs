namespace Replay;

// Bounded independent AAC encoders and a packet-copy worker, followed by remux.
// Source selection, filter arithmetic, encoder quality and full validation stay
// with RecordingPacketExport. No producer or SaveClip scheduling is changed.
internal static class RecordingMuxSchedule
{
    internal static bool Suitable(RecordingPacketExport.Snapshot snapshot, string working)
    {
        if (snapshot.Duration < 45 || snapshot.AudioFiles.Count == 0) return false;
        try
        {
            // Intermediates coexist with final output. Do not consume the
            // recording reserve just to choose a faster optional strategy.
            var video = snapshot.Segments.Sum(s => new FileInfo(s.Path).Length);
            var audio = snapshot.Duration * 160_000 / 8 * (snapshot.AudioFiles.Count + 1);
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(working))!).AvailableFreeSpace >
                256L * 1024 * 1024 + 2 * (video + audio) * 1.1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    internal static async Task Run(RecordingPacketExport.Snapshot snapshot, int frames, IReadOnlyList<string> combined,
        string working, string temporary, Func<IReadOnlyList<string>, Task> command)
    {
        var filter = combined.ToList().IndexOf("-filter_complex");
        if (filter < 0) throw new IOException("Не найден граф аудио записи.");
        var graph = combined[filter + 1];
        for (var i = 0; i < snapshot.AudioFiles.Count; i++) graph = graph.Replace($"[{i + 1}:a]", $"[{i}:a]");
        var outputs = combined.Select((v, i) => (v, i)).Where(p => p.v == "-map")
            .Select(p => combined[p.i + 1]).Where(v => v.StartsWith('[')).ToArray();
        if (outputs.Length is < 1 or > 5) throw new IOException("Не подтверждён набор дорожек записи.");
        var input = combined[combined.ToList().IndexOf("-i") + 1];
        var duration = combined[combined.ToList().LastIndexOf("-t") + 1];
        var bsf = combined[combined.ToList().IndexOf("-bsf:v") + 1];
        var video = Path.Combine(working, "video.mkv");
        var audio = outputs.Select((_, i) => Path.Combine(working, $"audio-{i}.mka")).ToArray();
        List<string> pcm = [];
        foreach (var path in snapshot.AudioFiles) pcm.AddRange(["-f", "s16le", "-ar", "48000", "-ac", "2", "-i", path]);
        using var workers = new SemaphoreSlim(3, 3); // at most 3 AAC + 1 copy process
        async Task Audio(int index)
        {
            await workers.WaitAsync();
            try
            {
                SaveTiming.Current?.Mark("recording-aac-started", new { Track = index, Map = outputs[index] });
                var filters = graph + string.Concat(outputs.Where((_, i) => i != index).Select(v => ";" + v + "anullsink"));
                List<string> args = ["-hide_banner", "-v", "error", "-xerror"];
                args.AddRange(pcm);
                args.AddRange(["-filter_complex", filters, "-map", outputs[index], "-c:a", "aac", "-b:a", "160k",
                    "-t", duration, "-y", audio[index]]);
                await command(args);
                SaveTiming.Current?.Mark("recording-aac-completed", new { Track = index });
            }
            finally { workers.Release(); }
        }
        async Task Video()
        {
            SaveTiming.Current?.Mark("recording-video-copy-started");
            // A standalone copy's -t can include one extra packet at a
            // fractional tail. The verified plan defines an exact shown-frame
            // prefix (one packet per frame); never round the accepted cutoff.
            await command(["-hide_banner", "-v", "error", "-xerror", "-f", "concat", "-safe", "0", "-i", input,
                "-map", "0:v:0", "-an", "-c:v", "copy", "-frames:v", frames.ToString(System.Globalization.CultureInfo.InvariantCulture), "-bsf:v", bsf, "-y", video]);
            SaveTiming.Current?.Mark("recording-video-copy-completed");
        }
        // Join ALL workers even on error/cancellation before the caller removes
        // attempt-owned files or retries its original frozen finalizer.
        await Task.WhenAll(outputs.Select((_, i) => Audio(i)).Append(Video()));
        SaveTiming.Current?.Mark("recording-remux-started");
        List<string> mux = ["-hide_banner", "-v", "error", "-xerror", "-copyts", "-i", video];
        foreach (var path in audio) mux.AddRange(["-i", path]);
        mux.AddRange(["-map", "0:v:0"]);
        for (var i = 0; i < audio.Length; i++) mux.AddRange(["-map", $"{i + 1}:a:0"]);
        for (var i = 0; i < combined.Count - 1; i++)
            if (combined[i].StartsWith("-metadata:s:a:", StringComparison.Ordinal) || combined[i].StartsWith("-disposition:a:", StringComparison.Ordinal))
                mux.AddRange([combined[i], combined[i + 1]]);
        // Preserve AAC priming and discard padding from the encoded tracks.
        // An additional -t or start_at_zero here would change packet selection.
        mux.AddRange(["-c", "copy", "-avoid_negative_ts", "disabled", "-y", temporary]);
        await command(mux);
        SaveTiming.Current?.Mark("recording-remux-completed");
    }
}
