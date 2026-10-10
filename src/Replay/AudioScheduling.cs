using System.Runtime.InteropServices;

namespace Replay;
// MMCSS provides bounded multimedia priority; it does not elevate the whole application.
internal sealed class AudioScheduling : IDisposable
{
    private readonly nint handle;
    [DllImport("avrt.dll", CharSet = CharSet.Unicode)] private static extern nint AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);
    [DllImport("avrt.dll")] private static extern bool AvRevertMmThreadCharacteristics(nint handle);
    public AudioScheduling() { uint index = 0; handle = AvSetMmThreadCharacteristics("Audio", ref index); }
    public void Dispose() { if (handle != 0) AvRevertMmThreadCharacteristics(handle); }
}
