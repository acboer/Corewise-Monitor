using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HwMon.Collector;

/// <summary>
/// Certificado autoassinado do coletor (ECDSA P-256, 20 anos), criado na primeira execução. O cliente não confia
/// numa autoridade: ele fixa a impressão digital recebida no pareamento.
/// </summary>
public static class CertificateStore
{
    public static X509Certificate2 LoadOrCreate(string path, string hostName)
    {
        if (!File.Exists(path))
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest($"CN=HwMon {hostName}", key, HashAlgorithmName.SHA256);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(hostName);
            san.AddDnsName("localhost");
            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
            File.WriteAllBytes(path, created.Export(X509ContentType.Pkcs12));
        }

        // Chave não efêmera: o TLS do Windows (SChannel) não aceita chave efêmera no lado servidor.
        return X509CertificateLoader.LoadPkcs12FromFile(path, password: null, X509KeyStorageFlags.DefaultKeySet);
    }
}
