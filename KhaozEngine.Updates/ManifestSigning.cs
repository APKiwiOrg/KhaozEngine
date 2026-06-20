using System;
using System.Collections.Generic;
using System.Security.Cryptography;

#nullable enable

namespace KhaozEngine.Updates;

/// <summary>An RSA key pair in PEM form: a PKCS#1 private key and a SubjectPublicKeyInfo public key.</summary>
public sealed record ManifestKeyPair(string PrivateKeyPem, string PublicKeyPem);

/// <summary>
/// Publish-side manifest signing. The private key signs the exact manifest bytes with RSA-2048
/// PKCS#1 v1.5 over SHA-256; the detached signature ships as <c>manifest.json.sig</c> (base64).
/// Pure BCL, so the Updates package keeps its near-zero-dependency footprint.
/// </summary>
public static class ManifestSigner
{
    /// <summary>Generates a fresh RSA-2048 key pair (private PKCS#1 PEM + public SPKI PEM).</summary>
    public static ManifestKeyPair GenerateKeyPair()
    {
        using RSA rsa = RSA.Create(2048);
        return new ManifestKeyPair(rsa.ExportRSAPrivateKeyPem(), rsa.ExportSubjectPublicKeyInfoPem());
    }

    /// <summary>Signs <paramref name="data"/> with the PKCS#1 PEM private key. Returns the raw signature.</summary>
    public static byte[] Sign(byte[] data, string privateKeyPem)
    {
        using RSA rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        return rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }
}

/// <summary>
/// Client-side manifest verification. A signature is accepted if it validates against ANY of the
/// trusted public keys, which is what makes key rotation a config change (ship the new key alongside
/// the old, switch the signer, drop the old key later). Never throws: any malformed key or signature
/// is a verification failure.
/// </summary>
public static class ManifestVerifier
{
    /// <summary>
    /// True when <paramref name="signature"/> is a valid RSA-2048/SHA-256/PKCS#1 signature of
    /// <paramref name="data"/> under at least one key in <paramref name="trustedPublicKeysPem"/>.
    /// </summary>
    public static bool Verify(byte[] data, byte[] signature, IEnumerable<string> trustedPublicKeysPem)
    {
        foreach (string pem in trustedPublicKeysPem)
        {
            try
            {
                using RSA rsa = RSA.Create();
                rsa.ImportFromPem(pem);
                if (rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException)
            {
                // Malformed key or signature: treat as a non-match, try the next key.
            }
        }

        return false;
    }
}
