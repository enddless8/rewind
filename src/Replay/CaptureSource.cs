namespace Replay;

internal static class CaptureSource
{
    // Explicit selection wins; Auto remembers the last eligible foreground application.
    public static GameWindow? ResolveGame(GameWindow? selected, GameWindow? lastActive)
    {
        var game = selected ?? lastActive;
        if (game != null && !Native.IsWindow(game.Handle))
            game = Native.Windows().FirstOrDefault(x => x.Exe == game.Exe);
        return game;
    }

    public static Display CurrentDisplay(Display display) => Native.Displays().FirstOrDefault(d => d.Device == display.Device)
        ?? throw new IOException("Выбранный монитор недоступен.");
}
