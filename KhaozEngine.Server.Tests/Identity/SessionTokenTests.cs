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

    [Fact]
    public void Colliding_identities_produce_distinct_tokens()
    {
        string t1 = SessionToken.Mint("a", "b.c", Now.AddHours(1), Secret);
        string t2 = SessionToken.Mint("a.b", "c", Now.AddHours(1), Secret);
        Assert.NotEqual(t1, t2);

        Assert.True(SessionToken.TryVerify(t1, Secret, Now, out string sub1, out string? name1, out _));
        Assert.Equal("a", sub1);
        Assert.Equal("b.c", name1);

        Assert.True(SessionToken.TryVerify(t2, Secret, Now, out string sub2, out string? name2, out _));
        Assert.Equal("a.b", sub2);
        Assert.Equal("c", name2);
    }

    [Fact]
    public void Repartitioned_wire_fields_fail_verification()
    {
        string original = SessionToken.Mint("a", "b.c", Now.AddHours(1), Secret);
        string[] parts = original.Split('.');
        Assert.Equal(5, parts.Length);

        string repartitionedSub = B64Url("a.b");
        string repartitionedName = B64Url("c");
        string tampered = $"{parts[0]}.{repartitionedSub}.{repartitionedName}.{parts[3]}.{parts[4]}";

        Assert.False(SessionToken.TryVerify(tampered, Secret, Now, out _, out _, out string reason));
        Assert.Contains("signature", reason, StringComparison.OrdinalIgnoreCase);
    }

    private static string B64Url(string s) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
