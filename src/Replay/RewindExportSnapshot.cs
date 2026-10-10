using System.Collections.ObjectModel;

namespace Replay;

// Created once after video ownership and the PCM snapshot have been secured.
// Strategies never read capture state, accept a command, or select another interval.
internal sealed record RewindExportSnapshot
{
    internal sealed record Range(string Path, double SourceStart, double SourceEnd, double Start, double End);
    internal double Accepted { get; }
    internal double HistoryStart { get; }
    internal double PreviousCutoff { get; }
    internal double Epoch { get; }
    internal double Duration { get; }
    internal int Fps { get; }
    internal ReadOnlyCollection<Range> Ranges { get; }
    internal ReadOnlyCollection<string> AudioFiles { get; }
    internal RewindExportSnapshot(double accepted, double historyStart, double previousCutoff, double epoch,
        int fps, IEnumerable<Range> ranges, IEnumerable<string> audioFiles)
    {
        Accepted = accepted; HistoryStart = historyStart; PreviousCutoff = previousCutoff; Epoch = epoch; Fps = fps;
        Ranges = Array.AsReadOnly(ranges.ToArray()); AudioFiles = Array.AsReadOnly(audioFiles.ToArray());
        Duration = Ranges.Sum(r => r.End - r.Start);
    }
}
