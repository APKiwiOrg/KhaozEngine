using System;
using System.Text;
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

public class SignedTokenTests
{
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("super-secret-signing-key");
    // A fixed, far-from-zero instant so AddHours/AddSeconds stay well inside positive unix time.
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddSeconds(1_700_000_000);

    [Fact]
    public void MintVerify_RoundTrips_Subject()
    {
        string token = SignedToken.Mint("player-7", Now.AddHours(1), Secret);
        Assert.True(SignedToken.TryVerify(token, Secret, Now, out string subject, out string reason));
        Assert.Equal("player-7", subject);
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void Mint_ProducesExpectedFormat_V1SubjectExpMac()
    {
        string token = SignedToken.Mint("player-7", Now.AddHours(1), Secret);
        string[] parts = token.Split('.');
        Assert.Equal(4, parts.Length);
        Assert.Equal("v1", parts[0]);
        Assert.Equal("player-7", parts[1]);
        Assert.Equal(Now.AddHours(1).ToUnixTimeSeconds().ToString(), parts[2]);
        // base64url: no '+', '/', or '=' padding.
        Assert.DoesNotContain('+', parts[3]);
        Assert.DoesNotContain('/', parts[3]);
        Assert.DoesNotContain('=', parts[3]);
    }

    [Fact]
    public void Verify_AfterExpiry_Rejected()
    {
        string token = SignedToken.Mint("player-7", Now.AddSeconds(30), Secret);
        Assert.False(SignedToken.TryVerify(token, Secret, Now.AddSeconds(31), out string subject, out string reason));
        Assert.Equal(string.Empty, subject);
        Assert.Equal("expired", reason);
    }

    [Fact]
    public void Verify_Tampered_Rejected()
    {
        string token = SignedToken.Mint("player-7", Now.AddHours(1), Secret);
        // Flip the subject but keep the original signature: the recomputed HMAC no longer matches.
        string tampered = token.Replace("player-7", "player-8");
        Assert.NotEqual(token, tampered);
        Assert.False(SignedToken.TryVerify(tampered, Secret, Now, out string subject, out string reason));
        Assert.Equal(string.Empty, subject);
        Assert.Equal("bad signature", reason);
    }

    [Fact]
    public void Verify_WrongSecret_Rejected()
    {
        string token = SignedToken.Mint("player-7", Now.AddHours(1), Secret);
        byte[] wrong = Encoding.UTF8.GetBytes("a-completely-different-key");
        Assert.False(SignedToken.TryVerify(token, wrong, Now, out string subject, out string reason));
        Assert.Equal(string.Empty, subject);
        Assert.Equal("bad signature", reason);
    }

    [Fact]
    public void Mint_SubjectWithDot_Throws()
    {
        Assert.Throws<ArgumentException>(() => SignedToken.Mint("bad.subject", Now.AddHours(1), Secret));
    }

    [Fact]
    public void Verify_Malformed_Rejected()
    {
        Assert.False(SignedToken.TryVerify("not-a-real-token", Secret, Now, out string subject, out string reason));
        Assert.Equal(string.Empty, subject);
        Assert.Equal("malformed", reason);
    }

    [Fact]
    public void HmacAuthenticator_Accepts_ValidToken_ReturnsSubject()
    {
        string token = SignedToken.Mint("acct-9", Now.AddHours(1), Secret);
        var auth = new HmacTokenAuthenticator(Secret, () => Now);
        Assert.True(auth.TryAuthenticate(Encoding.UTF8.GetBytes(token), out string subject, out string reason));
        Assert.Equal("acct-9", subject);
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void HmacAuthenticator_Rejects_ExpiredToken_WithReason()
    {
        string token = SignedToken.Mint("acct-9", Now.AddSeconds(10), Secret);
        var auth = new HmacTokenAuthenticator(Secret, () => Now.AddMinutes(5));
        Assert.False(auth.TryAuthenticate(Encoding.UTF8.GetBytes(token), out string subject, out string reason));
        Assert.Equal(string.Empty, subject);
        Assert.Equal("expired", reason);
    }

    [Fact]
    public void AllowAll_ReturnsTokenAsSubject_EmptyWhenNoToken()
    {
        var auth = new AllowAllAuthenticator();
        Assert.True(auth.TryAuthenticate(Encoding.UTF8.GetBytes("acct-from-token"), out string s1, out string r1));
        Assert.Equal("acct-from-token", s1);
        Assert.Equal(string.Empty, r1);

        Assert.True(auth.TryAuthenticate(ReadOnlySpan<byte>.Empty, out string s2, out string r2));
        Assert.Equal(string.Empty, s2);
        Assert.Equal(string.Empty, r2);
    }
}
