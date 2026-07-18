using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Updates;
using Xunit;

namespace KhaozEngine.Tests.Updates;

public sealed class ManifestSigningTests
{
    private static (string privPem, string pubPem) NewKeyPair()
    {
        using RSA rsa = RSA.Create(2048);
        return (rsa.ExportRSAPrivateKeyPem(), rsa.ExportSubjectPublicKeyInfoPem());
    }

    [Fact]
    public void SignedBytes_VerifyAgainstMatchingKey()
    {
        (string priv, string pub) = NewKeyPair();
        byte[] data = Encoding.UTF8.GetBytes("{\"version\":\"2.0.0\"}");

        byte[] sig = ManifestSigner.Sign(data, priv);

        Assert.True(ManifestVerifier.Verify(data, sig, new[] { pub }));
    }

    [Fact]
    public void WrongKey_FailsVerification()
    {
        (string priv, _) = NewKeyPair();
        (_, string otherPub) = NewKeyPair();
        byte[] data = Encoding.UTF8.GetBytes("payload");

        byte[] sig = ManifestSigner.Sign(data, priv);

        Assert.False(ManifestVerifier.Verify(data, sig, new[] { otherPub }));
    }

    [Fact]
    public void TamperedData_FailsVerification()
    {
        (string priv, string pub) = NewKeyPair();
        byte[] sig = ManifestSigner.Sign(Encoding.UTF8.GetBytes("original"), priv);

        Assert.False(ManifestVerifier.Verify(Encoding.UTF8.GetBytes("tampered"), sig, new[] { pub }));
    }

    [Fact]
    public void RotationKeyList_AcceptsAnyTrustedKey()
    {
        (string oldPriv, string oldPub) = NewKeyPair();
        (string newPriv, string newPub) = NewKeyPair();
        byte[] data = Encoding.UTF8.GetBytes("payload");

        byte[] sigByNew = ManifestSigner.Sign(data, newPriv);

        var trusted = new List<string> { oldPub, newPub };
        Assert.True(ManifestVerifier.Verify(data, sigByNew, trusted));
        Assert.True(ManifestVerifier.Verify(data, ManifestSigner.Sign(data, oldPriv), trusted));
    }

    [Fact]
    public void GenerateKeyPair_RoundTripsThroughSignVerify()
    {
        ManifestKeyPair kp = ManifestSigner.GenerateKeyPair();
        byte[] data = Encoding.UTF8.GetBytes("payload");

        byte[] sig = ManifestSigner.Sign(data, kp.PrivateKeyPem);

        Assert.True(ManifestVerifier.Verify(data, sig, new[] { kp.PublicKeyPem }));
    }

    [Fact]
    public void Verify_NoKeys_ReturnsFalse()
    {
        byte[] data = Encoding.UTF8.GetBytes("payload");
        Assert.False(ManifestVerifier.Verify(data, new byte[] { 1, 2, 3 }, System.Array.Empty<string>()));
    }

    [Fact]
    public void Verify_GarbageSignature_ReturnsFalseNotThrow()
    {
        (_, string pub) = NewKeyPair();
        Assert.False(ManifestVerifier.Verify(Encoding.UTF8.GetBytes("x"), new byte[] { 0xFF }, new[] { pub }));
    }
}
