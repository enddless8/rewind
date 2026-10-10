namespace Replay;

public enum MicrophoneRuntimeState { Enabled, Muted, Unavailable }

// Shared capture intent. Audio readers only load a reference; allocation happens on commands.
public sealed class MicrophoneMuteControl
{
    private sealed record State(bool Muted, double UnmutedAt);
    private State state = new(false, Native.Clock);
    public bool Muted => Volatile.Read(ref state).Muted;
    public double UnmutedAt => Volatile.Read(ref state).UnmutedAt;
    public void SetMuted(bool muted)
    {
        while (true)
        {
            var current = Volatile.Read(ref state);
            if (current.Muted == muted) return;
            var next = new State(muted, muted ? current.UnmutedAt : Native.Clock);
            if (ReferenceEquals(Interlocked.CompareExchange(ref state, next, current), current)) return;
        }
    }
}
