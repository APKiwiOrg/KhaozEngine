using System;
using System.Text;
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

public class SignedTokenParseUnverifiedTests
{
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("super-secret-signing-key");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddSeconds(1_700_000_000);

    [Fact]
    public void V1_RoundTrips_SubjectAndExpiry_NoName()
    {
        DateTimeOffset exp = Now.AddHours(2);
        string token = SignedToken.Mint("player-7", exp, Secret);

        Assert.True(SignedToken.TryParseUnverified(token, out string subject, out long expUnix, out string? name));
        Assert.Equal("player-7", subject);
        Assert.Equal(exp.ToUnixTimeSeconds(), expUnix);
        Assert.Null(name);   // v1 carries no name claim at all
    }

    [Fact]
    public void V2_RoundTrips_SubjectExpiryAndName()
    {
        DateTimeOffset exp = Now.AddHours(2);
        string token = SignedToken.Mint("player-7", "Sir Reginald.the.Third", exp, Secret);

        Assert.True(SignedToken.TryParseUnverified(token, out string subject, out long expUnix, out string? name));
        Assert.Equal("player-7", subject);
        Assert.Equal(exp.ToUnixTimeSeconds(), expUnix);
        Assert.Equal("Sir Reginald.the.Third", name);   // dots inside the name survive (it is base64url-encoded)
    }

    [Fact]
    public void V2_EmptyName_ParsesAsEmptyString()
    {
        string token = SignedToken.Mint("player-7", string.Empty, Now.AddHours(1), Secret);
        Assert.True(SignedToken.TryParseUnverified(token, out string subject, out _, out string? name));
        Assert.Equal("player-7", subject);
        Assert.Equal(string.Empty, name);   // present-but-empty name claim -> "", distinct from v1's null
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("not.a.real.token.at.all.too.many")]   // 7 parts
    [InlineData("v1.subject.only")]                     // 3 parts
    [InlineData("v2.subject.name.exp")]                 // 4 parts but v2 needs 5
    [InlineData("v1.subject.name.exp.extra")]           // 5 parts but v1 needs 4
    [InlineData("v3.subject.123.sig")]                  // unknown version, right count
    [InlineData("v9.subject.name.123.sig")]             // unknown version, right count
    public void Garbage_And_WrongShapes_Rejected(string token)
    {
        Assert.False(SignedToken.TryParseUnverified(token, out string subject, out long expUnix, out string? name));
        Assert.Equal(string.Empty, subject);
        Assert.Equal(0L, expUnix);
        Assert.Null(name);
    }

    [Theory]
    [InlineData("v1.subject.notanumber.sig")]
    [InlineData("v1.subject.-5.sig")]        // NumberStyles.None rejects a leading sign
    [InlineData("v1.subject. 5.sig")]        // ...and leading whitespace
    [InlineData("v2.subject.bmFtZQ.notanumber.sig")]
    public void NonNumericExpiry_Rejected(string token)
    {
        Assert.False(SignedToken.TryParseUnverified(token, out _, out long expUnix, out _));
        Assert.Equal(0L, expUnix);
    }

    [Fact]
    public void ZeroExpiry_IsStructurallyValid()
    {
        // TryParseUnverified does NOT check expiry against a clock, so an expUnix of 0 parses (it is merely expired).
        Assert.True(SignedToken.TryParseUnverified("v1.player-7.0.somesig", out string subject, out long expUnix, out _));
        Assert.Equal("player-7", subject);
        Assert.Equal(0L, expUnix);
    }

    [Fact]
    public void TamperedSignature_StillParses()
    {
        // The whole point: this is NOT authentication. A token whose signature has been swapped for garbage still
        // parses structurally (only the server's TryVerify would reject it as a bad signature).
        string token = SignedToken.Mint("player-7", Now.AddHours(1), Secret);
        string[] parts = token.Split('.');
        parts[^1] = "AAAA-tampered-signature-BBBB";
        string tampered = string.Join('.', parts);

        Assert.True(SignedToken.TryParseUnverified(tampered, out string subject, out long expUnix, out _));
        Assert.Equal("player-7", subject);
        Assert.True(expUnix > 0);
        // ...and the genuine token would of course fail the server-side check only on the signature, not the structure.
        Assert.False(SignedToken.TryVerify(tampered, Secret, Now, out _, out string reason));
        Assert.Equal("bad signature", reason);
    }

    [Fact]
    public void ParseUnverified_AcceptsEverything_TryVerify_StructurallyAccepts()
    {
        // Cross-check the two agree on STRUCTURE: a well-formed but wrong-secret token verifies-false with a
        // signature/expiry reason (never "malformed"), and parses true. A malformed one fails both.
        string good = SignedToken.Mint("acct-42", "Bob", Now.AddHours(1), Secret);
        Assert.True(SignedToken.TryParseUnverified(good, out _, out _, out _));
        Assert.False(SignedToken.TryVerify(good, Encoding.UTF8.GetBytes("wrong-secret"), Now, out _, out string reason));
        Assert.Equal("bad signature", reason);   // structurally fine, only the signature is wrong

        Assert.False(SignedToken.TryParseUnverified("totally-malformed", out _, out _, out _));
        Assert.False(SignedToken.TryVerify("totally-malformed", Secret, Now, out _, out string reason2));
        Assert.Equal("malformed", reason2);
    }
}
