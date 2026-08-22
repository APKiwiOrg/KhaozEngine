using System;
using System.Text;
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class ConnectionGateTests
{
    const string Version = "tile-1";
    const string Hash = "abc123def456";

    static IConnectionAuthenticator Gate(Func<string, bool>? banned = null, Action<string>? log = null) =>
        ConnectionGate.Wrap(new AllowAllAuthenticator(), Version, Hash, log, banned);

    static byte[] Token(string version, string hash, string subject) =>
        ConnectionGate.BuildToken(version, hash, Encoding.UTF8.GetBytes(subject));

    [Fact]
    public void A_matching_version_hash_and_token_is_admitted_with_its_subject()
    {
        Assert.True(Gate().TryAuthenticate(Token(Version, Hash, "acct-1"), out string subject, out string reason));
        Assert.Equal("acct-1", subject);
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void A_version_mismatch_refuses_with_the_version_token_and_never_reaches_the_world_check()
    {
        Assert.False(Gate().TryAuthenticate(Token("tile-0", "wrong-world", "acct"), out string s, out string reason));
        Assert.Equal(string.Empty, s);
        Assert.True(HandshakeToken.TryParseIncompatibleVersion(reason, out string required));
        Assert.Equal(Version, required);
    }

    [Fact]
    public void A_world_mismatch_refuses_with_both_hashes_and_logs_them()
    {
        string? logged = null;
        Assert.False(Gate(log: m => logged = m).TryAuthenticate(Token(Version, "otherworld", "acct"), out _, out string reason));
        Assert.True(HandshakeToken.TryParseWorldMismatch(reason, out string server, out string client));
        Assert.Equal(Hash, server);
        Assert.Equal("otherworld", client);
        Assert.Contains("otherworld", logged);
    }

    [Fact]
    public void A_bad_token_refuses_with_the_inner_authenticators_reason()
    {
        var inner = new HmacTokenAuthenticator(new byte[] { 1, 2, 3 }, () => DateTimeOffset.UnixEpoch);
        IConnectionAuthenticator gate = ConnectionGate.Wrap(inner, Version, Hash);
        Assert.False(gate.TryAuthenticate(Token(Version, Hash, "not-a-signed-token"), out _, out string reason));
        Assert.Equal("malformed", reason);
    }

    [Fact]
    public void A_banned_subject_refuses_after_the_token_verified_it()
    {
        IConnectionAuthenticator gate = Gate(banned: s => s == "acct-banned");
        Assert.False(gate.TryAuthenticate(Token(Version, Hash, "acct-banned"), out _, out string reason));
        Assert.Equal(HandshakeToken.BannedReason, reason);
        Assert.True(gate.TryAuthenticate(Token(Version, Hash, "acct-ok"), out _, out _));
    }

    [Fact]
    public void A_legacy_unwrapped_token_is_read_as_version_empty_and_refused_at_the_outermost_gate()
    {
        Assert.False(Gate().TryAuthenticate(Encoding.UTF8.GetBytes("bare"), out _, out string reason));
        Assert.True(HandshakeToken.TryParseIncompatibleVersion(reason, out _));
    }

    // The three ke: reason tokens are STABLE WIRE STRINGS a shipped client matches on, so each is pinned LITERALLY
    // here, through the refusal that emits it. Round-tripping a builder through its own parser pins nothing: rename
    // the prefix and the builder and the parser move together, green the whole way, while an old client facing a new
    // server quietly downgrades to a generic token rejection.
    [Fact]
    public void The_version_refusal_carries_the_literal_incompatible_version_token()
    {
        Assert.False(Gate().TryAuthenticate(Token("tile-0", Hash, "acct"), out _, out string reason));
        Assert.Equal("ke:incompatible-version:tile-1", reason);
    }

    [Fact]
    public void The_world_refusal_carries_the_literal_world_mismatch_token()
    {
        Assert.False(Gate().TryAuthenticate(Token(Version, "otherworld", "acct"), out _, out string reason));
        Assert.Equal("ke:world-mismatch:abc123def456|otherworld", reason);
    }

    [Fact]
    public void The_ban_refusal_carries_the_literal_banned_token()
    {
        IConnectionAuthenticator gate = Gate(banned: s => s == "acct-banned");
        Assert.False(gate.TryAuthenticate(Token(Version, Hash, "acct-banned"), out _, out string reason));
        Assert.Equal("ke:banned", reason);
    }
}
