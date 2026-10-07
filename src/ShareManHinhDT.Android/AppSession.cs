namespace ShareManHinhDT.Android;

internal static class AppSession
{
    public static SenderSession? Current { get; set; }
    public static bool Capturing { get; set; }
    public static string Status { get; private set; } = "Mở ứng dụng Windows và bấm Bắt đầu nhận.";
    public static string? ErrorDetails { get; private set; }
    public static event Action? Changed;
    private static long generation;
    public static void Update(string status)
    {
        Status = status;
        Changed?.Invoke();
    }

    public static void Attach(SenderSession session)
    {
        Current = session;
        Interlocked.Increment(ref generation);
        ErrorDetails = null;
        session.Ended += reason => new Handler(Looper.MainLooper!).Post(async () =>
        {
            if (ReferenceEquals(Current, session))
                global::Android.App.Application.Context.StopService(new Intent(global::Android.App.Application.Context, typeof(CaptureService)));
            await DisconnectAsync(reason, session);
        });
    }

    public static async Task DisconnectAsync(string reason, SenderSession? expected = null)
    {
        var session = Current;
        long disconnectGeneration = Volatile.Read(ref generation);
        if (expected is not null && !ReferenceEquals(session, expected))
        {
            try { await expected.DisposeAsync(); }
            catch (Exception ex) { global::Android.Util.Log.Error("ShareManHinhDT.Capture", ex.ToString()); }
            return;
        }
        Current = null;
        Capturing = false;
        session?.Diagnostics.End(reason);
        try { if (session is not null) await session.DisposeAsync(); }
        catch (Exception ex) { global::Android.Util.Log.Error("ShareManHinhDT.Capture", ex.ToString()); }
        if (Current is null && Volatile.Read(ref generation) == disconnectGeneration)
        {
            var outcome = session?.Diagnostics.Outcome;
            ErrorDetails = outcome?.Details;
            Update(outcome?.Reason ?? reason);
        }
    }

    public static void PublishFailure(SenderSession session)
    {
        if (!ReferenceEquals(Current, session)) return;
        ErrorDetails = session.Diagnostics.Outcome?.Details;
        Update(session.Diagnostics.Outcome?.Reason ?? "Không thể chia sẻ màn hình.");
    }
}
