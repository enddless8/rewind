using System.Runtime.InteropServices;

namespace Replay;

internal static class MediaOwnership
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string newName, string existingName, nint security);
    // Called only for a CLOSED segment while the producer metadata lock is held.
    // A new directory entry is ownership: unlinking Rewind's entry cannot remove this consumer's media.
    public static void Retain(string source, string target)
    {
        if (!CreateHardLink(target, source, 0))
        { SaveTiming.Current?.Mark("ownership-copy-fallback"); File.Copy(source, target, false); }
    }
}
