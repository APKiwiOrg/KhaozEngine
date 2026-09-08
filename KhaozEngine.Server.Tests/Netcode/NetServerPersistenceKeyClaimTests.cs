using System;
using System.Text;
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

public class NetServerPersistenceKeyClaimTests
{
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("super-secret-signing-key");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddSeconds(1_700_000_000);

    private sealed class RejectingClaimAuthenticator : IConnectionAuthenticator, IConnectionPersistenceKey
    {
        public int PersistenceKeyReadCount { get; private set; }

        public bool TryAuthenticate(ReadOnlySpan<byte> token, out string subject, out string rejectReason)
        {
            subject = string.Empty;
            rejectReason = "rejected";
            return false;
        }

        public string ReadPersistenceKey(ReadOnlySpan<byte> token)
        {
            PersistenceKeyReadCount++;
            return "character:wrong";
        }
    }

    [Fact]
    public void Hmac_v3_Join_Surfaces_Verified_Persistence_Key()
    {
        var transport = new RecordingTransport();
        var server = new NetServer(transport, maxPlayers: 4, new HmacTokenAuthenticator(Secret, () => Now));
        string token = SignedToken.Mint("acct:42", "Ada", "character:9001", Now.AddHours(1), Secret);

        ServerSessionEvent joined = Join(server, transport, connection: 1, token);

        Assert.Equal("acct:42", joined.Subject);
        Assert.Equal("Ada", joined.DisplayName);
        Assert.Equal("character:9001", joined.PersistenceKey);
    }

    [Fact]
    public void Plain_Authenticator_Join_Has_Empty_Persistence_Key()
    {
        var transport = new RecordingTransport();
        var server = new NetServer(transport, maxPlayers: 4, new AllowAllAuthenticator());

        ServerSessionEvent joined = Join(server, transport, connection: 1, "acct:42");

        Assert.Equal("acct:42", joined.Subject);
        Assert.Equal(string.Empty, joined.PersistenceKey);
    }

    [Fact]
    public void Failed_Authentication_Produces_No_Event_And_Does_Not_Read_The_Claim()
    {
        var transport = new RecordingTransport();
        var authenticator = new RejectingClaimAuthenticator();
        var server = new NetServer(transport, maxPlayers: 4, authenticator);

        SendHello(server, transport, connection: 1, "bad-token");

        Assert.False(server.TryDequeueEvent(out _));
        Assert.Equal(0, authenticator.PersistenceKeyReadCount);
    }

    [Fact]
    public void Duplicate_Sessions_Still_Compare_The_Subject_When_Claims_Differ()
    {
        var transport = new RecordingTransport();
        var server = new NetServer(transport, maxPlayers: 4, new HmacTokenAuthenticator(Secret, () => Now),
            duplicateSessions: DuplicateSessionPolicy.RefuseNewer);
        string firstToken = SignedToken.Mint("acct:42", "Ada", "character:9001", Now.AddHours(1), Secret);
        string secondToken = SignedToken.Mint("acct:42", "Ada", "character:9002", Now.AddHours(1), Secret);

        ServerSessionEvent first = Join(server, transport, connection: 1, firstToken);
        SendHello(server, transport, connection: 2, secondToken);

        Assert.Equal("character:9001", first.PersistenceKey);
        Assert.False(server.TryDequeueEvent(out _));
        (NetConnectionId target, byte[]? reason) = Assert.Single(transport.Disconnects);
        Assert.Equal(new NetConnectionId(2), target);
        Assert.NotNull(reason);
        Assert.Equal(SessionRejectReason.AlreadySignedIn,
            Encoding.UTF8.GetString(SessionFrame.ReadBody(reason!)));
    }

    private static ServerSessionEvent Join(NetServer server, RecordingTransport transport, int connection,
        string token)
    {
        SendHello(server, transport, connection, token);
        Assert.True(server.TryDequeueEvent(out ServerSessionEvent joined));
        Assert.Equal(ServerSessionEventKind.Joined, joined.Kind);
        Assert.False(server.TryDequeueEvent(out _));
        return joined;
    }

    private static void SendHello(NetServer server, RecordingTransport transport, int connection, string token)
    {
        var id = new NetConnectionId(connection);
        transport.Deliver(NetEvent.Connected(id));
        transport.Deliver(NetEvent.FromData(id,
            SessionFrame.Write(SessionOpcode.Hello, Encoding.UTF8.GetBytes(token)),
            NetChannelReliability.ReliableOrdered));
        server.Poll();
    }
}
