using System;
using System.Collections.Generic;
using System.Text;
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

public class SignedTokenDisplayNameTests
{
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("super-secret-signing-key");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddSeconds(1_700_000_000);

    [Fact]
    public void Mint_v2_RoundTrips_Subject_And_DisplayName()
    {
        string token = SignedToken.Mint("acct-7", "Daniel", Now.AddHours(1), Secret);
        string[] parts = token.Split('.');
        Assert.Equal(5, parts.Length);
        Assert.Equal("v2", parts[0]);

        Assert.True(SignedToken.TryVerify(token, Secret, Now, out string subject, out string displayName, out string reason));
        Assert.Equal("acct-7", subject);
        Assert.Equal("Daniel", displayName);
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void DisplayName_With_Dots_And_Unicode_Survives()
    {
        // base64url encoding means the name can contain '.' and multibyte glyphs without breaking the field split.
        string name = "Dr. Z油.é";
        string token = SignedToken.Mint("acct-7", name, Now.AddHours(1), Secret);
        Assert.True(SignedToken.TryVerify(token, Secret, Now, out _, out string displayName, out _));
        Assert.Equal(name, displayName);
    }

    [Fact]
    public void Mint_v2_VerifiesWithLegacyOverload_NameDropped_SubjectIntact()
    {
        string token = SignedToken.Mint("acct-7", "Daniel", Now.AddHours(1), Secret);
        Assert.True(SignedToken.TryVerify(token, Secret, Now, out string subject, out string reason));
        Assert.Equal("acct-7", subject);
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void V1Token_Has_Empty_DisplayName_On_New_Overload()
    {
        string token = SignedToken.Mint("acct-7", Now.AddHours(1), Secret);   // legacy v1 mint
        Assert.True(SignedToken.TryVerify(token, Secret, Now, out string subject, out string displayName, out _));
        Assert.Equal("acct-7", subject);
        Assert.Equal(string.Empty, displayName);
    }

    [Fact]
    public void Mint_v2_EmptyName_Verifies_With_EmptyName()
    {
        string token = SignedToken.Mint("acct-7", string.Empty, Now.AddHours(1), Secret);
        Assert.True(SignedToken.TryVerify(token, Secret, Now, out string subject, out string displayName, out _));
        Assert.Equal("acct-7", subject);
        Assert.Equal(string.Empty, displayName);
    }

    [Fact]
    public void V2_TamperedName_Is_Rejected()
    {
        string token = SignedToken.Mint("acct-7", "Daniel", Now.AddHours(1), Secret);
        // Re-mint with a different name under the SAME secret, then graft the original signature on: it won't match.
        string other = SignedToken.Mint("acct-7", "Mallory", Now.AddHours(1), Secret);
        string[] op = token.Split('.');
        string[] np = other.Split('.');
        string tampered = string.Join('.', np[0], np[1], np[2], np[3], op[4]);   // Mallory body, Daniel's mac
        Assert.False(SignedToken.TryVerify(tampered, Secret, Now, out string subject, out string displayName, out string reason));
        Assert.Equal(string.Empty, subject);
        Assert.Equal(string.Empty, displayName);
        Assert.Equal("bad signature", reason);
    }

    [Fact]
    public void Hmac_ReadDisplayName_Returns_Name_For_v2_Empty_For_v1()
    {
        var auth = new HmacTokenAuthenticator(Secret, () => Now);

        string v2 = SignedToken.Mint("acct-7", "Daniel", Now.AddHours(1), Secret);
        Assert.Equal("Daniel", auth.ReadDisplayName(Encoding.UTF8.GetBytes(v2)));

        string v1 = SignedToken.Mint("acct-7", Now.AddHours(1), Secret);
        Assert.Equal(string.Empty, auth.ReadDisplayName(Encoding.UTF8.GetBytes(v1)));

        // An invalid token surfaces no name (never throws).
        Assert.Equal(string.Empty, auth.ReadDisplayName(Encoding.UTF8.GetBytes("garbage")));
    }

    [Fact]
    public void NetServer_Surfaces_DisplayName_From_Token_On_Join()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var auth = new HmacTokenAuthenticator(Secret, () => Now);
        var server = new NetServer(st, maxPlayers: 4, auth);
        string token = SignedToken.Mint("acct-7", "Daniel", Now.AddHours(1), Secret);
        var client = new NetClient(ct, Encoding.UTF8.GetBytes(token));

        ServerSessionEvent joined = PumpUntilJoined(server, client);
        Assert.Equal("acct-7", joined.Subject);
        Assert.Equal("Daniel", joined.DisplayName);
    }

    [Fact]
    public void NetServer_DisplayName_Empty_When_Authenticator_Provides_None()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var server = new NetServer(st, maxPlayers: 4, new AllowAllAuthenticator());   // not an IConnectionDisplayName
        var client = new NetClient(ct, Encoding.UTF8.GetBytes("acct-from-token"));

        ServerSessionEvent joined = PumpUntilJoined(server, client);
        Assert.Equal("acct-from-token", joined.Subject);
        Assert.Equal(string.Empty, joined.DisplayName);
    }

    private static ServerSessionEvent PumpUntilJoined(NetServer server, NetClient client)
    {
        for (int i = 0; i < 32; i++)
        {
            server.Poll();
            client.Poll();
            while (server.TryDequeueEvent(out ServerSessionEvent e))
                if (e.Kind == ServerSessionEventKind.Joined) return e;
        }
        throw new Xunit.Sdk.XunitException("server never raised a Joined event");
    }
}
