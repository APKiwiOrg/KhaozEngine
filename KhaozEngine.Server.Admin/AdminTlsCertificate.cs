using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace KhaozEngine.Server.Admin;

/// <summary>
/// TLS material for the admin endpoint. A pinned self-signed certificate is the expected default (the consumer's
/// console pins its thumbprint); load a real one from PFX/PEM when you have it.
/// </summary>
public sealed class AdminTlsCertificate
{
    private AdminTlsCertificate(X509Certificate2 cert) => Certificate = cert;

    /// <summary>The certificate (with private key) Kestrel binds for TLS.</summary>
    public X509Certificate2 Certificate { get; }

    public static AdminTlsCertificate FromCertificate(X509Certificate2 certificate)
        => new(certificate ?? throw new ArgumentNullException(nameof(certificate)));

    public static AdminTlsCertificate FromPfx(string path, string? password = null)
        => new(X509CertificateLoader.LoadPkcs12FromFile(path, password));

    public static AdminTlsCertificate FromPfxBytes(byte[] pfx, string? password = null)
        => new(X509CertificateLoader.LoadPkcs12(pfx, password));

    public static AdminTlsCertificate FromPem(string certPath, string keyPath)
        => new(X509Certificate2.CreateFromPemFile(certPath, keyPath));

    public static AdminTlsCertificate FromPemBytes(byte[] certPem, byte[] keyPem)
        => new(X509Certificate2.CreateFromPem(Encoding.UTF8.GetString(certPem), Encoding.UTF8.GetString(keyPem)));

    /// <summary>Generates a self-signed RSA-2048 certificate (default 10-year lifetime). Re-imports through a PFX so
    /// the private key is persisted and Kestrel can use it for TLS on every platform.</summary>
    public static AdminTlsCertificate CreateSelfSigned(string subjectName, TimeSpan? lifetime = null)
    {
        using RSA rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={subjectName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using X509Certificate2 generated = req.CreateSelfSigned(now.AddDays(-1), now.Add(lifetime ?? TimeSpan.FromDays(3650)));
        byte[] pfx = generated.Export(X509ContentType.Pfx);
        return new AdminTlsCertificate(X509CertificateLoader.LoadPkcs12(pfx, null));
    }
}
