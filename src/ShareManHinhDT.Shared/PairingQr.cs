using System.Globalization;
using System.Net;
using System.Net.Sockets;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

namespace ShareManHinhDT.Shared;

public sealed record PairingQrPayload(int Version, string Address, int Port, string Code, string Fingerprint, DateTimeOffset ExpiresAt)
{
    public void Validate(DateTimeOffset? now = null)
    {
        if (Version != 1) throw new InvalidDataException("Phiên bản QR chưa được hỗ trợ.");
        if (!IPAddress.TryParse(Address, out var address) || address.AddressFamily != AddressFamily.InterNetwork ||
            address.ToString() != Address || IPAddress.IsLoopback(address) || address.GetAddressBytes()[0] is 0 or >= 224)
            throw new InvalidDataException("QR không chứa địa chỉ IPv4 của PC hợp lệ.");
        if (Port != PairingQr.Port || Code is not { Length: 6 } || Code.Any(c => !char.IsAsciiDigit(c)))
            throw new InvalidDataException("Cổng hoặc mã ghép đôi trong QR không hợp lệ.");
        PairingQr.ValidateFingerprint(Fingerprint);
        if (ExpiresAt <= (now ?? DateTimeOffset.UtcNow)) throw new InvalidDataException("QR đã hết hạn. Hãy quét mã mới trên PC.");
    }
}

public static class PairingQr
{
    public const int Port = 48731;
    public const int MaximumTextLength = 1024;
    private static readonly string[] Keys = ["v", "ip", "port", "code", "fp", "exp"];

    public static string Encode(PairingQrPayload payload, DateTimeOffset? now = null)
    {
        payload.Validate(now);
        return $"sharemanhinhdt://pair?v={payload.Version}&ip={payload.Address}&port={payload.Port}&code={payload.Code}&fp={payload.Fingerprint.ToUpperInvariant()}&exp={payload.ExpiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}";
    }

    public static PairingQrPayload Parse(string text, DateTimeOffset? now = null)
    {
        if (text.Length > MaximumTextLength || !Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            uri.Scheme != "sharemanhinhdt" || uri.Host != "pair" || uri.UserInfo.Length != 0 || uri.Port != -1 ||
            uri.AbsolutePath is not ("" or "/") || uri.Fragment.Length != 0)
            throw new InvalidDataException("Đây không phải QR ghép đôi ShareManHinhDT.");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in uri.Query.TrimStart('?').Split('&'))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2 || !Keys.Contains(parts[0], StringComparer.Ordinal) || !fields.TryAdd(parts[0], Uri.UnescapeDataString(parts[1])))
                throw new InvalidDataException("Dữ liệu QR ghép đôi không hợp lệ.");
        }
        if (fields.Count != Keys.Length ||
            !int.TryParse(fields["v"], NumberStyles.None, CultureInfo.InvariantCulture, out int version) ||
            !int.TryParse(fields["port"], NumberStyles.None, CultureInfo.InvariantCulture, out int port) ||
            !long.TryParse(fields["exp"], NumberStyles.None, CultureInfo.InvariantCulture, out long expiry))
            throw new InvalidDataException("QR thiếu thông tin ghép đôi hoặc sai định dạng.");
        DateTimeOffset expiresAt;
        try { expiresAt = DateTimeOffset.FromUnixTimeSeconds(expiry); }
        catch (ArgumentOutOfRangeException ex) { throw new InvalidDataException("Thời hạn QR không hợp lệ.", ex); }
        var payload = new PairingQrPayload(version, fields["ip"], port, fields["code"], fields["fp"].ToUpperInvariant(), expiresAt);
        payload.Validate(now);
        return payload;
    }

    public static void ValidateFingerprint(string? fingerprint)
    {
        if (fingerprint is not { Length: 64 } || fingerprint.Any(c => !char.IsAsciiHexDigit(c)))
            throw new InvalidDataException("Vân tay chứng chỉ trong QR không hợp lệ.");
    }

    public static string? SelectFingerprint(string? storedFingerprint, string? scannedFingerprint)
    {
        if (scannedFingerprint is null) return storedFingerprint;
        ValidateFingerprint(scannedFingerprint);
        scannedFingerprint = scannedFingerprint.ToUpperInvariant();
        if (storedFingerprint is not null && !Pairing.Matches(storedFingerprint, scannedFingerprint))
            throw new InvalidDataException("Chứng chỉ PC trong QR khác chứng chỉ đã tin cậy. Kiểm tra PC và chọn Quên PC trước khi ghép đôi lại.");
        return scannedFingerprint;
    }

    public static BitMatrix CreateMatrix(PairingQrPayload payload, DateTimeOffset? now = null) =>
        new QRCodeWriter().encode(Encode(payload, now), BarcodeFormat.QR_CODE, 0, 0,
            new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 4, [EncodeHintType.ERROR_CORRECTION] = ErrorCorrectionLevel.M });

    public static string? DecodeGrayscale(byte[] pixels, int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > pixels.Length)
            throw new ArgumentException("Ảnh camera không hợp lệ.");
        var source = new RGBLuminanceSource(pixels, width, height, RGBLuminanceSource.BitmapFormat.Gray8);
        var result = new QRCodeReader().decode(new BinaryBitmap(new HybridBinarizer(source)),
            new Dictionary<DecodeHintType, object> { [DecodeHintType.TRY_HARDER] = true });
        return result?.Text;
    }
}

public sealed class QrScanGate
{
    private int completed;
    public bool IsCompleted => Volatile.Read(ref completed) != 0;
    public bool TryAccept(string text, out PairingQrPayload? payload)
    {
        payload = PairingQr.Parse(text);
        return Interlocked.CompareExchange(ref completed, 1, 0) == 0;
    }
}
