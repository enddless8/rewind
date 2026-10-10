namespace Replay;
internal sealed class CaptureRecovery
{
    public int Attempts { get; private set; }
    private double nextAttempt;
    public bool Exhausted => Attempts >= 3;
    public bool CanAttempt(double now) => !Exhausted && now >= nextAttempt;
    public void Attempt(double now) { Attempts++; nextAttempt = now + 5; }
    public void Reset() { Attempts = 0; nextAttempt = 0; }
    public static bool IsAccessLost(string line) => line.Contains("887a0026", StringComparison.OrdinalIgnoreCase) || line.Contains("DXGI_ERROR_ACCESS_LOST", StringComparison.OrdinalIgnoreCase);
}
