namespace ShareManHinhDT.Android;

internal sealed class QrDecodeWorker(Func<byte[], int, int, string?> decode, Action<string> decoded,
    Action<Exception> failed)
{
    private readonly object sync = new();
    private bool busy;
    private bool stopped;

    public bool IsBusy { get { lock (sync) return busy || stopped; } }

    public bool TryDecode(byte[] pixels, int width, int height)
    {
        lock (sync)
        {
            if (busy || stopped) return false;
            busy = true;
        }
        _ = Task.Run(() =>
        {
            try
            {
                string? text = decode(pixels, width, height);
                lock (sync)
                    if (!stopped && text is not null) decoded(text);
            }
            catch (Exception ex)
            {
                lock (sync)
                    if (!stopped) failed(ex);
            }
            finally { lock (sync) busy = false; }
        });
        return true;
    }

    public void Stop() { lock (sync) stopped = true; }
}
