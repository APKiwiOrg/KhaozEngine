using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

public class SignedTokenPersistenceKeyTests
{
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("super-secret-signing-key");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddSeconds(1_700_000_000);

    [Fact]
    public void Mint_v3_RoundTrips_All_Verified_Claims()
    {
        string token = SignedToken.Mint("acct:42", "Ada", "character:9001", Now.AddHours(1), Secret);
        string[] parts = token.Split('.');

        Assert.Equal(6, parts.Length);
        Assert.Equal("v3", parts[0]);
        Assert.True(SignedToken.TryVerify(token, Secret, Now, out string subject, out string displayName,
            out string persistenceKey, out string reason));
        Assert.Equal("acct:42", subject);
        Assert.Equal("Ada", displayName);
        Assert.Equal("character:9001", persistenceKey);
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void V3_Tampered_Persistence_Key_Is_Rejected()
    {
        string original = SignedToken.Mint("acct:42", "Ada", "character:9001", Now.AddHours(1), Secret);
        string other = SignedToken.Mint("acct:42", "Ada", "character:9002", Now.AddHours(1), Secret);
        string[] originalParts = original.Split('.');
        string[] otherParts = other.Split('.');
        originalParts[3] = otherParts[3];
        string tampered = string.Join('.', originalParts);

        Assert.False(SignedToken.TryVerify(tampered, Secret, Now, out string subject, out string displayName,
            out string persistenceKey, out string reason));
        Assert.Equal(string.Empty, subject);
        Assert.Equal(string.Empty, displayName);
        Assert.Equal(string.Empty, persistenceKey);
        Assert.Equal("bad signature", reason);
    }

    [Fact]
    public void V3_Malformed_Persistence_Key_Under_Valid_Signature_Is_Rejected()
    {
        long expiry = Now.AddHours(1).ToUnixTimeSeconds();
        string signed = string.Concat("v3.acct:42.QWRh.*.", expiry.ToString(CultureInfo.InvariantCulture));
        string token = string.Concat(signed, ".", Sign(signed));

        Assert.False(SignedToken.TryVerify(token, Secret, Now, out string subject, out string displayName,
            out string persistenceKey, out string reason));
        Assert.Equal(string.Empty, subject);
        Assert.Equal(string.Empty, displayName);
        Assert.Equal(string.Empty, persistenceKey);
        Assert.Equal("malformed", reason);
    }

    [Fact]
    public void V3_Structural_Parse_Accepts_The_Shape_Without_Surfacing_The_Persistence_Key()
    {
        DateTimeOffset expiry = Now.AddHours(1);
        string token = SignedToken.Mint("acct:42", "Ada", "character:9001", expiry, Secret);

        Assert.True(SignedToken.TryParseUnverified(token, out string subject, out long expUnix,
            out string? displayName));
        Assert.Equal("acct:42", subject);
        Assert.Equal(expiry.ToUnixTimeSeconds(), expUnix);
        Assert.Equal("Ada", displayName);
    }

    [Fact]
    public void V3_Malformed_Persistence_Key_Fails_Structural_Parse()
    {
        Assert.False(SignedToken.TryParseUnverified("v3.acct:42.QWRh.*.1700003600.mac", out string subject,
            out long expUnix, out string? displayName));
        Assert.Equal(string.Empty, subject);
        Assert.Equal(0L, expUnix);
        Assert.Null(displayName);
    }

    [Fact]
    public void V3_Verifies_Through_Older_Overloads_That_Discard_The_Persistence_Key()
    {
        string token = SignedToken.Mint("acct:42", "Ada", "character:9001", Now.AddHours(1), Secret);

        Assert.True(SignedToken.TryVerify(token, Secret, Now, out string legacySubject, out string legacyReason));
        Assert.Equal("acct:42", legacySubject);
        Assert.Equal(string.Empty, legacyReason);

        Assert.True(SignedToken.TryVerify(token, Secret, Now, out string subject, out string displayName,
            out string reason));
        Assert.Equal("acct:42", subject);
        Assert.Equal("Ada", displayName);
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void Widest_Verify_Preserves_V1_And_V2_Behavior()
    {
        string v1 = SignedToken.Mint("acct:42", Now.AddHours(1), Secret);
        Assert.True(SignedToken.TryVerify(v1, Secret, Now, out string v1Subject, out string v1DisplayName,
            out string v1PersistenceKey, out string v1Reason));
        Assert.Equal("acct:42", v1Subject);
        Assert.Equal(string.Empty, v1DisplayName);
        Assert.Equal(string.Empty, v1PersistenceKey);
        Assert.Equal(string.Empty, v1Reason);

        string v2 = SignedToken.Mint("acct:42", "Ada", Now.AddHours(1), Secret);
        Assert.True(SignedToken.TryVerify(v2, Secret, Now, out string v2Subject, out string v2DisplayName,
            out string v2PersistenceKey, out string v2Reason));
        Assert.Equal("acct:42", v2Subject);
        Assert.Equal("Ada", v2DisplayName);
        Assert.Equal(string.Empty, v2PersistenceKey);
        Assert.Equal(string.Empty, v2Reason);
    }

    private static string Sign(string message)
    {
        using var hmac = new HMACSHA256(Secret);
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(message)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
