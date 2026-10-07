using ShareManHinhDT.Shared;

namespace ShareManHinhDT.Windows;

internal sealed class PairingPresentation
{
    public PairingInfo? Pairing { get; set; }
    public string? Address { get; set; }
    public bool Listening { get; set; }
    public bool Connected { get; set; }

    public PairingQrPayload? GetPayload(DateTimeOffset now)
    {
        if (!Listening || Connected || Address is null || Pairing is null || Pairing.ExpiresAt <= now) return null;
        return new(1, Address, PairingQr.Port, Pairing.Code, Pairing.Fingerprint, Pairing.ExpiresAt);
    }
}

internal sealed record ReceiverAddress(string Address, string AdapterName, bool Preferred)
{
    public string Label => $"{Address} — {AdapterName}";
}
