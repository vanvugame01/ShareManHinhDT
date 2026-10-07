using System.Security.Cryptography;
using System.Text;

namespace ShareManHinhDT.Shared;

public static class Pairing
{
    public static string CreateCode() => RandomNumberGenerator.GetInt32(1_000_000).ToString("D6");
    public static string Fingerprint(ReadOnlySpan<byte> certificate) => Convert.ToHexString(SHA256.HashData(certificate));
    public static string DisplayFingerprint(string fingerprint) => string.Join(" ", Enumerable.Range(0, fingerprint.Length / 4).Select(i => fingerprint.Substring(i * 4, 4)));
    public static bool Matches(string expected, string actual) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
}
