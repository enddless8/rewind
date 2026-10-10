namespace Replay;

internal enum RewindState { Off, Starting, On, Stopping, Recovering, Error }
internal enum RewindSaveState { Unavailable, Available, Saving }

// Runtime presentation is derived from capture facts, never from a label or dot color.
internal readonly record struct RewindPresentation(RewindState State, RewindSaveState SaveState)
{
    public static RewindPresentation FromCapture(bool wanted, bool running, RewindState? transition,
        bool recovering, bool recoverableLoss, bool recoveryExhausted, bool commandFailed,
        bool readableHistory, bool saving, bool busy)
    {
        var state = transition ?? (recovering || wanted && recoverableLoss && !recoveryExhausted
            ? RewindState.Recovering : commandFailed || wanted && !running
                ? RewindState.Error : running ? RewindState.On : RewindState.Off);
        var save = saving ? RewindSaveState.Saving : readableHistory && !busy
            && state is not (RewindState.Starting or RewindState.Stopping or RewindState.Recovering)
                ? RewindSaveState.Available : RewindSaveState.Unavailable;
        return new(state, save);
    }
}
