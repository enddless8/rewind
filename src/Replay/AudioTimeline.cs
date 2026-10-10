namespace Replay;

// A device's frame counter is the sample clock. QPC anchors it to the video clock;
// it must not independently reposition every PCM packet (that inserts clicks).
internal sealed class AudioTimeline
{
    private readonly int rate;
    public AudioTimeline(int rate = AudioTrack.Rate) { this.rate = rate; }
    private bool initialized;
    private long expectedPosition;
    private double nextTime;
    public long MissingFrames { get; private set; }
    public int Discontinuities { get; private set; }

    public double Place(long position, int frames, double timestamp, bool timestampValid, bool discontinuity, double arrival)
    {
        var anchor = timestampValid ? timestamp : arrival - frames / (double)rate;
        double start;
        if (!initialized) { start = anchor; initialized = true; }
        else if (position >= expectedPosition)
        {
            var lost = position - expectedPosition;
            start = nextTime + lost / (double)rate;
            MissingFrames += (long)Math.Round(lost * (double)AudioTrack.Rate / rate);
            if (lost > 0 || discontinuity) Discontinuities++;
        }
        else if (discontinuity)
        {
            // Some virtual devices restart their frame counter after a discontinuity.
            start = Math.Max(nextTime, anchor);
            MissingFrames += (long)Math.Round((start - nextTime) * AudioTrack.Rate);
            Discontinuities++;
        }
        else start = nextTime; // Broken/non-monotonic counter: keep samples contiguous.
        expectedPosition = position + frames;
        nextTime = start + frames / (double)rate;
        return start;
    }
}
