using ShareManHinhDT.Shared;

namespace ShareManHinhDT.Android;

internal static class EncoderSelection
{
    public static (int Width, int Height)? FindSize(int sourceWidth, int sourceHeight, int widthAlignment,
        int heightAlignment, Func<int, int, bool> supports)
    {
        var target = H264.Fit720p(sourceWidth, sourceHeight);
        int stepWidth = LeastCommonMultiple(2, widthAlignment);
        int stepHeight = LeastCommonMultiple(2, heightAlignment);
        (int Width, int Height)? best = null;
        double bestScore = double.MaxValue;
        double aspect = (double)sourceWidth / sourceHeight;
        for (int w = stepWidth; w <= target.Width; w += stepWidth)
            for (int h = stepHeight; h <= target.Height; h += stepHeight)
            {
                if (w < 48 || h < 48) continue;
                double error = Math.Abs(Math.Log((double)w / h / aspect));
                if (error > 0.03 || !supports(w, h)) continue;
                double score = error * 4 + 1 - (double)(w * h) / (target.Width * target.Height);
                if (score < bestScore) { best = (w, h); bestScore = score; }
            }
        return best;
    }

    private static int LeastCommonMultiple(int a, int b)
    {
        if (b <= 0 || b > 1280) throw new ArgumentOutOfRangeException(nameof(b));
        return b % a == 0 ? b : checked(a * b);
    }

    public static T ConfigureWithFpsFallback<T>(Func<bool, T> configure, Action<Exception> retry)
    {
        try { return configure(true); }
        catch (Exception ex) { retry(ex); return configure(false); }
    }
}
