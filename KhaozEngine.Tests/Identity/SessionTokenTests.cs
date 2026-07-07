using System;
using System.Text;
using KhaozEngine.Identity;
using Xunit;

namespace KhaozEngine.Tests.Identity;

public class SessionTokenTests
{
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("test-secret-32-bytes-long-000000");
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Roundtrips_subject_and_displayname()
    {
        string t = SessionToken.Mint("sub-1", "Nick", Now.AddHours(1), Secret);
        Assert.True(SessionToken.TryVerify(t, Secret, Now, out string sub, out string? name, out _));
        Assert.Equal("sub-1", sub);
        Assert.Equal("Nick", name);
    }

    [Fact]
    public void Rejects_expired()
    {
        string t = SessionToken.Mint("sub-1", null, Now.AddHours(1), Secret);
        Assert.False(SessionToken.TryVerify(t, Secret, Now.AddHours(2), out _, out _, out string reason));
        Assert.Contains("expired", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_tampered_or_wrong_secret()
    {
        string t = SessionToken.Mint("sub-1", null, Now.AddHours(1), Secret);
        byte[] wrong = Encoding.UTF8.GetBytes("wrong-secret-32-bytes-long-00000");
        Assert.False(SessionToken.TryVerify(t, wrong, Now, out _, out _, out _));
        Assert.False(SessionToken.TryVerify(t + "x", Secret, Now, out _, out _, out _));
    }
}
