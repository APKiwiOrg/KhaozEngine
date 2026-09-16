using System;
using System.Linq;
using System.Text;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Netcode;
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// The content layer of the connect door, contracts 7.5 and spec 8.5, beside the world gate's own tests
/// because it is the same nest and the same refusal shape.
/// <para>
/// The two refusal tokens are STABLE WIRE STRINGS a shipped client matches on, so both are pinned
/// LITERALLY here through the refusal that emits them. Round-tripping a builder through its own parser
/// pins nothing: rename the prefix and the two move together, green the whole way, while an old client
/// facing a new server quietly falls through to a generic refusal.
/// </para>
/// </summary>
public class ContentIdentityGateTests
{
    const string ServerHash = "1111111111111111111111111111111111111111111111111111111111111111";
    const string ClientHash = "2222222222222222222222222222222222222222222222222222222222222222";
    const int ServerVersion = 47;

    static ContentVersionIdentity Server => new(ServerVersion, ServerHash);

    static IConnectionAuthenticator Gate(
        int minimumClientBuild = 0,
        Action<string>? log = null,
        IConnectionAuthenticator? inner = null) =>
        new ContentIdentityGateAuthenticator(
            Server,
            inner ?? new AllowAllAuthenticator(),
            minimumClientBuild,
            log);

    static byte[] Token(int versionNumber, string manifestHash, string subject, int? clientBuild = null) =>
        ContentIdentityLayer.Wrap(
            new ContentVersionIdentity(versionNumber, manifestHash),
            Encoding.UTF8.GetBytes(subject),
            clientBuild);

    [Fact]
    public void A_matching_layer_is_admitted_and_the_inner_authenticator_sees_its_own_token()
    {
        Assert.True(Gate().TryAuthenticate(
            Token(ServerVersion, ServerHash, "acct-1"), out string subject, out string reason));
        Assert.Equal("acct-1", subject);
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void A_version_mismatch_refuses_with_both_sides_in_the_token()
    {
        Assert.False(Gate().TryAuthenticate(Token(44, ClientHash, "acct"), out string subject, out string reason));
        Assert.Equal(string.Empty, subject);
        Assert.True(ContentRefusal.TryParseMismatch(reason, out ContentVersionIdentity server, out ContentVersionIdentity? client));
        Assert.Equal(Server, server);
        Assert.Equal(new ContentVersionIdentity(44, ClientHash), client);
    }

    // The case the NUMBER alone cannot catch: a client that rebuilt a pack wrongly presents the right version
    // number over different bytes, which is exactly why contracts 7.5 puts both in the layer.
    [Fact]
    public void A_hash_mismatch_under_matching_version_numbers_refuses()
    {
        Assert.False(Gate().TryAuthenticate(Token(ServerVersion, ClientHash, "acct"), out _, out string reason));
        Assert.True(ContentRefusal.TryParseMismatch(reason, out _, out ContentVersionIdentity? client));
        Assert.Equal(new ContentVersionIdentity(ServerVersion, ClientHash), client);
    }

    [Fact]
    public void An_absent_layer_refuses_with_empty_client_fields()
    {
        Assert.False(Gate().TryAuthenticate(Encoding.UTF8.GetBytes("bare"), out _, out string reason));
        Assert.True(ContentRefusal.TryParseMismatch(reason, out ContentVersionIdentity server, out ContentVersionIdentity? client));
        Assert.Equal(Server, server);
        Assert.Null(client);
        Assert.Equal("ke:content-mismatch:47|" + ServerHash + "||", reason);
    }

    [Fact]
    public void A_client_below_the_minimum_build_is_told_to_update_rather_than_given_the_generic_mismatch()
    {
        IConnectionAuthenticator gate = Gate(minimumClientBuild: 4118);

        Assert.False(gate.TryAuthenticate(
            Token(ServerVersion, ServerHash, "acct", clientBuild: 4117), out _, out string reason));
        Assert.True(ContentRefusal.TryParseClientTooOld(reason, out int minimum));
        Assert.Equal(4118, minimum);
        Assert.False(ContentRefusal.TryParseMismatch(reason, out _, out _));

        Assert.True(gate.TryAuthenticate(
            Token(ServerVersion, ServerHash, "acct", clientBuild: 4118), out _, out _));
    }

    // The build field is OPTIONAL and its absence is not a statement, so the gate cannot judge it and does not
    // refuse on it. The client half of the same check is the fetch loop's (spec 8.7 step 3), which compares its
    // own build against the manifest it just fetched, and a client that lies about its build defeats a door
    // check anyway. Locking every player out of a version because their head sends no build ordinal would be a
    // refusal no update can clear.
    [Fact]
    public void A_layer_that_states_no_build_is_judged_on_its_content_identity_alone()
    {
        IConnectionAuthenticator gate = Gate(minimumClientBuild: 4118);

        Assert.True(gate.TryAuthenticate(Token(ServerVersion, ServerHash, "acct"), out _, out _));
        Assert.False(gate.TryAuthenticate(Token(44, ServerHash, "acct"), out _, out string reason));
        Assert.True(ContentRefusal.TryParseMismatch(reason, out _, out _));
    }

    [Fact]
    public void The_mismatch_refusal_carries_the_literal_content_mismatch_token()
    {
        Assert.False(Gate().TryAuthenticate(Token(44, ClientHash, "acct"), out _, out string reason));
        Assert.Equal(
            "ke:content-mismatch:47|" + ServerHash + "|44|" + ClientHash,
            reason);
    }

    [Fact]
    public void The_too_old_refusal_carries_the_literal_client_too_old_token()
    {
        Assert.False(Gate(minimumClientBuild: 4118).TryAuthenticate(
            Token(ServerVersion, ServerHash, "acct", clientBuild: 17), out _, out string reason));
        Assert.Equal("ke:content-client-too-old:4118", reason);
    }

    // The PIPE separates fields inside the payload and the COLON separates the token's own fields, so a value
    // carrying a colon would re-split a shipped client's parse. Every field either comes from the server's own
    // identity or survived a strict parse, so a hostile layer buys an attacker nothing but empty client fields.
    [Fact]
    public void No_value_in_either_token_ever_contains_a_colon()
    {
        byte[] hostile = HandshakeToken.Wrap(
            "47|ke:evil:" + ClientHash, Encoding.UTF8.GetBytes("acct"));

        Assert.False(Gate().TryAuthenticate(hostile, out _, out string mismatch));
        Assert.Equal(2, mismatch.Count(c => c == ':'));
        Assert.DoesNotContain("evil", mismatch, StringComparison.Ordinal);

        Assert.False(Gate(minimumClientBuild: 9).TryAuthenticate(
            Token(ServerVersion, ServerHash, "acct", clientBuild: 1), out _, out string tooOld));
        Assert.Equal(2, tooOld.Count(c => c == ':'));
    }

    // The PARSER is the client's half of the same strictness, and it was checking the version fields only.
    // A refusal is the whole input to the fetch loop, so a server hash that is not a content address is a
    // path fragment reaching a client's fetch, and an EMPTY one is an ArgumentException out of
    // ContentFetchLoop.FetchAsync on the exact flow the Catalog README documents. Neither is a refusal a
    // client may act on, so neither parses.
    [Theory]
    [InlineData("ke:content-mismatch:1|../../../etc/passwd||")]
    [InlineData("ke:content-mismatch:1|||")]
    [InlineData("ke:content-mismatch:1|" + ServerHash + "1||")]
    [InlineData("ke:content-mismatch:1|" + ClientHash + "|2|")]
    [InlineData("ke:content-mismatch:1|" + ClientHash + "|2|not-a-hash")]
    [InlineData("ke:content-mismatch:1\0|" + ServerHash + "||")]
    public void A_mismatch_whose_hashes_are_not_content_addresses_parses_false(string reason)
    {
        Assert.False(ContentRefusal.TryParseMismatch(reason, out ContentVersionIdentity server, out ContentVersionIdentity? client));
        Assert.Equal(default, server);
        Assert.Null(client);
    }

    [Fact]
    public void A_mismatch_carrying_two_content_addresses_parses_both_sides()
    {
        Assert.True(ContentRefusal.TryParseMismatch(
            "ke:content-mismatch:47|" + ServerHash + "|44|" + ClientHash,
            out ContentVersionIdentity server,
            out ContentVersionIdentity? client));
        Assert.Equal(Server, server);
        Assert.Equal(new ContentVersionIdentity(44, ClientHash), client);

        Assert.True(ContentRefusal.TryParseMismatch(
            "ke:content-mismatch:47|" + ServerHash + "||",
            out server,
            out client));
        Assert.Equal(Server, server);
        Assert.Null(client);
    }

    // The producing side refuses the same thing, so a server cannot emit a token its own client parser
    // would then read as no refusal at all.
    [Fact]
    public void A_mismatch_over_a_hash_that_is_not_a_content_address_refuses_to_be_written()
    {
        Assert.Throws<ArgumentException>(() =>
            ContentRefusal.Mismatch(new ContentVersionIdentity(1, "not-a-hash"), null));
        Assert.Throws<ArgumentException>(() =>
            ContentRefusal.Mismatch(Server, new ContentVersionIdentity(2, "not-a-hash")));
        Assert.Throws<ArgumentException>(() =>
            ContentRefusal.Mismatch(Server, new ContentVersionIdentity(2, string.Empty)));
    }

    [Fact]
    public void A_layer_whose_hash_is_not_a_content_address_is_read_as_no_statement_at_all()
    {
        byte[] shortHash = HandshakeToken.Wrap("47|abc123", Encoding.UTF8.GetBytes("acct"));

        Assert.False(Gate().TryAuthenticate(shortHash, out _, out string reason));
        Assert.True(ContentRefusal.TryParseMismatch(reason, out _, out ContentVersionIdentity? client));
        Assert.Null(client);
    }

    [Fact]
    public void A_refusal_is_logged_with_both_sides_for_the_operator()
    {
        string? logged = null;
        Gate(log: m => logged = m).TryAuthenticate(Token(44, ClientHash, "acct"), out _, out _);

        Assert.Contains("44", logged, StringComparison.Ordinal);
        Assert.Contains(ClientHash, logged, StringComparison.Ordinal);
    }

    [Fact]
    public void The_display_name_and_persistence_key_unwrap_the_layer_and_delegate_inward()
    {
        var inner = new NamedAuthenticator();
        var gate = new ContentIdentityGateAuthenticator(Server, inner);
        byte[] token = Token(ServerVersion, ServerHash, "acct-9");

        Assert.Equal("name:acct-9", gate.ReadDisplayName(token));
        Assert.Equal("key:acct-9", gate.ReadPersistenceKey(token));
    }

    [Fact]
    public void The_layer_value_is_the_version_number_and_the_hash_joined_by_a_pipe()
    {
        Assert.Equal("47|" + ServerHash, ContentIdentityLayer.Format(Server));
        Assert.Equal("47|" + ServerHash + "|4118", ContentIdentityLayer.Format(Server, 4118));

        Assert.True(ContentIdentityLayer.TryParse(
            "47|" + ServerHash, out ContentVersionIdentity identity, out int? build));
        Assert.Equal(Server, identity);
        Assert.Null(build);

        Assert.True(ContentIdentityLayer.TryParse(
            "47|" + ServerHash + "|4118", out identity, out build));
        Assert.Equal(Server, identity);
        Assert.Equal(4118, build);
    }

    [Theory]
    [InlineData("")]
    [InlineData("47")]
    [InlineData("47|")]
    [InlineData("|" + ServerHash)]
    [InlineData("-1|" + ServerHash)]
    [InlineData("47|" + ServerHash + "|")]
    [InlineData("47|" + ServerHash + "|x")]
    [InlineData("47|" + ServerHash + "|1|2")]
    // A trailing NUL, which int.TryParse under NumberStyles.None reads as the end of the string. The doc
    // says plain decimal digits and the gate echoes the parsed number, so nothing follows from it today,
    // and a number that is not what it was written as is not a statement this door should accept.
    [InlineData("47\0|" + ServerHash)]
    [InlineData("47|" + ServerHash + "|4118\0")]
    public void A_layer_value_that_is_not_a_number_and_a_content_address_parses_false(string value)
    {
        Assert.False(ContentIdentityLayer.TryParse(value, out ContentVersionIdentity identity, out int? build));
        Assert.Equal(default, identity);
        Assert.Null(build);
    }

    [Fact]
    public void A_hash_that_is_the_wrong_length_or_not_lower_hex_parses_false()
    {
        Assert.False(ContentIdentityLayer.TryParse("47|" + ServerHash + "1", out _, out _));
        Assert.False(ContentIdentityLayer.TryParse("47|" + ServerHash[..63], out _, out _));
        Assert.False(ContentIdentityLayer.TryParse("47|" + new string('A', 64), out _, out _));
        Assert.True(ContentIdentityLayer.TryParse("47|" + new string('f', 64), out _, out _));
    }

    [Fact]
    public void A_gate_over_an_identity_that_is_not_a_content_address_refuses_to_be_built()
    {
        Assert.Throws<ArgumentException>(() =>
            new ContentIdentityGateAuthenticator(new ContentVersionIdentity(1, "not-a-hash")));
        Assert.Throws<ArgumentException>(() =>
            ContentRefusal.Mismatch(new ContentVersionIdentity(1, "ke:oops"), null));
    }

    // Layer ORDER in the nest, outermost first: protocol version, world, CONTENT, the game's token auth, the
    // ban check (contracts 7.5). Content sits inside world and outside auth because a disagreement about
    // content is a cheaper and more specific refusal than a failed credential, and the ban check stays
    // innermost because it needs the subject the token produced. Each fact below is one adjacent pair.
    public class Order
    {
        const string ProtocolVersion = "tile-1";
        const string WorldHash = "worldhash";

        static IConnectionAuthenticator Door(Func<string, bool>? banned = null)
        {
            IConnectionAuthenticator auth = new AllowAllAuthenticator();
            if (banned is not null)
            {
                auth = new BanGateAuthenticator(auth, banned);
            }

            return ConnectionGate.Wrap(
                new ContentIdentityGateAuthenticator(Server, auth), ProtocolVersion, WorldHash);
        }

        static byte[] Token(string protocolVersion, string worldHash, int versionNumber, string hash, string subject) =>
            ConnectionGate.BuildToken(
                protocolVersion,
                worldHash,
                ContentIdentityLayer.Wrap(
                    new ContentVersionIdentity(versionNumber, hash), Encoding.UTF8.GetBytes(subject)));

        [Fact]
        public void A_version_mismatch_is_refused_before_the_content_layer_is_read()
        {
            Assert.False(Door().TryAuthenticate(
                Token("tile-0", WorldHash, 44, ClientHash, "acct"), out _, out string reason));
            Assert.Equal("ke:incompatible-version:tile-1", reason);
        }

        [Fact]
        public void A_world_mismatch_is_refused_before_the_content_layer_is_read()
        {
            Assert.False(Door().TryAuthenticate(
                Token(ProtocolVersion, "otherworld", 44, ClientHash, "acct"), out _, out string reason));
            Assert.Equal("ke:world-mismatch:worldhash|otherworld", reason);
        }

        [Fact]
        public void A_content_mismatch_is_refused_before_the_ban_check_reaches_a_subject()
        {
            Assert.False(Door(banned: s => s == "acct-banned").TryAuthenticate(
                Token(ProtocolVersion, WorldHash, 44, ClientHash, "acct-banned"), out _, out string reason));
            Assert.True(ContentRefusal.TryParseMismatch(reason, out _, out _));
        }

        [Fact]
        public void A_banned_subject_matching_on_everything_else_is_refused_innermost()
        {
            IConnectionAuthenticator door = Door(banned: s => s == "acct-banned");

            Assert.False(door.TryAuthenticate(
                Token(ProtocolVersion, WorldHash, ServerVersion, ServerHash, "acct-banned"), out _, out string reason));
            Assert.Equal(HandshakeToken.BannedReason, reason);
            Assert.True(door.TryAuthenticate(
                Token(ProtocolVersion, WorldHash, ServerVersion, ServerHash, "acct-ok"), out string subject, out _));
            Assert.Equal("acct-ok", subject);
        }
    }

    sealed class NamedAuthenticator : IConnectionAuthenticator, IConnectionDisplayName, IConnectionPersistenceKey
    {
        public bool TryAuthenticate(ReadOnlySpan<byte> token, out string subject, out string rejectReason)
        {
            subject = Encoding.UTF8.GetString(token);
            rejectReason = string.Empty;
            return true;
        }

        public string ReadDisplayName(ReadOnlySpan<byte> token) => "name:" + Encoding.UTF8.GetString(token);

        public string ReadPersistenceKey(ReadOnlySpan<byte> token) => "key:" + Encoding.UTF8.GetString(token);
    }
}
