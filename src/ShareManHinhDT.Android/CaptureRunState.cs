namespace ShareManHinhDT.Android;

internal sealed record CaptureOutcome(string Reason, bool IsError, string? Details);

internal sealed class CaptureRunState
{
    private readonly object sync = new();
    private readonly Queue<string> entries = new();
    private CaptureOutcome? outcome;
    public CaptureOutcome? Outcome { get { lock (sync) return outcome; } }

    public void Trace(string message)
    {
        lock (sync)
        {
            entries.Enqueue($"{DateTimeOffset.UtcNow:O} {message}");
            while (entries.Count > 200) entries.Dequeue();
        }
    }

    public bool End(string reason, bool isError = false, string? details = null)
    {
        lock (sync)
        {
            if (outcome is not null) return false;
            string? report = isError ? string.Join(System.Environment.NewLine, entries) + System.Environment.NewLine + details : null;
            outcome = new(reason, isError, report);
            return true;
        }
    }
}

internal sealed class CapturePermissionGate
{
    private string? pending;
    public void Begin(string connectionId) => pending = connectionId;
    public bool Consume(string? connectionId)
    {
        string? requested = pending;
        pending = null;
        return requested is not null && requested == connectionId;
    }
}
