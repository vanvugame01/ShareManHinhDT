using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ShareManHinhDT.Windows;

public static class CertificateIdentity
{
    private const string Subject = "CN=ShareManHinhDT Local Receiver";

    public static X509Certificate2 LoadOrCreate()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        var existing = store.Certificates.Cast<X509Certificate2>().FirstOrDefault(c => c.Subject == Subject &&
            c.HasPrivateKey && c.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(1));
        if (existing is not null) return existing;
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(Subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(2));
        var persisted = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null,
            X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);
        store.Add(persisted);
        return persisted;
    }
}
