using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HwMon.Core.Telemetry;

/// <summary>Impressão digital SHA-256 do certificado do coletor, usada para fixar (pinning) a conexão.</summary>
public static class Fingerprint
{
    public static string Of(X509Certificate cert) => Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));

    /// <summary>Forma curta para conferir a olho: "AB12-CD34-EF56-7890".</summary>
    public static string Short(string fingerprint)
        => fingerprint.Length < 16 ? fingerprint : string.Join('-', Enumerable.Range(0, 4).Select(i => fingerprint.Substring(i * 4, 4)));
}
