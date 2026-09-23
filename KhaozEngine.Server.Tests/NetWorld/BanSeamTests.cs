using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// One ban seam (#678). <see cref="IBanStore"/>, <see cref="BanRecord"/> and <see cref="InMemoryBanStore"/> live in
/// the <c>KhaozEngine.Netcode</c> assembly under their shipped <c>KhaozEngine.NetWorld</c> names, forwarded from
/// <c>KhaozEngine.NetWorld</c>, so <see cref="BanGateAuthenticator"/> takes the same store a <see cref="WorldServer"/>
/// takes as <c>banStore:</c>.
/// <para>This file imports BOTH namespaces and names <c>IBanStore</c> bare, which is the shape of a consumer's server
/// entry point. It compiling at all is the source-compatibility check: a second <c>IBanStore</c> in
/// <c>KhaozEngine.Netcode</c> would make every such name ambiguous.</para>
/// </summary>
public class BanSeamTests
{
    private static float Flat(float x, float z) => 0f;

    // A consumer-shaped decorator over the seam, implemented against the NetWorld name the way a game's audit
    // wrapper is.
    private sealed class AuditingBanStore : IBanStore
    {
        private readonly IBanStore inner;
        public List<string> Audit { get; } = new();

        public AuditingBanStore(IBanStore inner) => this.inner = inner;

        public bool IsBanned(string accountId) => inner.IsBanned(accountId);

        public async ValueTask BanAsync(string accountId, string reason, DateTimeOffset? until = null,
            CancellationToken cancellationToken = default)
        {
            await inner.BanAsync(accountId, reason, until, cancellationToken).ConfigureAwait(false);
            Audit.Add("ban " + accountId);
        }

        public async ValueTask UnbanAsync(string accountId, CancellationToken cancellationToken = default)
        {
            await inner.UnbanAsync(accountId, cancellationToken).ConfigureAwait(false);
            Audit.Add("unban " + accountId);
        }

        public IReadOnlyCollection<BanRecord> ListBans() => inner.ListBans();
    }

    private static void Pump(WorldServer server, WorldClient client, float tick, int rounds = 30)
    {
        for (int i = 0; i < rounds; i++)
        {
            server.Poll();
            server.Tick(tick);
            client.Poll();
        }
    }

    [Fact]
    public void The_seam_lives_in_Netcode_under_its_NetWorld_names_and_NetWorld_forwards_it()
    {
        Assembly netcode = typeof(HandshakeToken).Assembly;
        Assembly netWorld = typeof(WorldServer).Assembly;
        Type[] forwarded = netWorld.GetForwardedTypes();

        foreach (Type seam in new[] { typeof(IBanStore), typeof(BanRecord), typeof(InMemoryBanStore) })
        {
            Assert.Same(netcode, seam.Assembly);
            Assert.Equal("KhaozEngine.NetWorld", seam.Namespace);
            Assert.Contains(seam, forwarded);
            // What an assembly built against an earlier KhaozEngine.NetWorld asks the runtime for.
            Assert.Same(seam, Type.GetType($"{seam.FullName}, {netWorld.GetName().Name}", throwOnError: true));
        }

        Assert.True(typeof(IBanStore).IsAssignableFrom(typeof(WorldStoreBanStore)));
    }

    [Fact]
    public async Task One_store_refuses_at_the_door_and_kicks_a_ban_applied_mid_session()
    {
        IBanStore bans = new AuditingBanStore(new InMemoryBanStore());
        var hub = new InMemoryTransportHub();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4 };
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default,
            authenticator: new BanGateAuthenticator(new AllowAllAuthenticator(), bans), banStore: bans);
        var admin = new ServerAdmin(server, bans);
        var clientConfig = new WorldClientConfig { TickSeconds = config.TickSeconds };

        var first = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default, clientConfig,
            token: Encoding.UTF8.GetBytes("acct-1"));
        Pump(server, first, config.TickSeconds);
        Assert.True(first.Joined);

        // Mid-session: the store records the ban and the live session is kicked.
        await admin.BanAsync("acct-1", "cheating");
        Pump(server, first, config.TickSeconds);
        Assert.Equal(0, server.PlayerCount);
        Assert.NotEqual(WorldConnectionState.Connected, first.ConnectionState);
        Assert.Equal(new[] { "ban acct-1" }, ((AuditingBanStore)bans).Audit);

        // The next connect never reaches the join: the door reads the same store and refuses with the ban token.
        var again = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default, clientConfig,
            token: Encoding.UTF8.GetBytes("acct-1"));
        Pump(server, again, config.TickSeconds);
        Assert.Equal(DisconnectReason.RejectedToken, again.DisconnectReason);
        Assert.Equal(HandshakeToken.BannedReason, again.DisconnectReasonDetail);
        Assert.Equal(0, server.PlayerCount);

        // Lifting the ban in the one store opens both paths again.
        await admin.UnbanAsync("acct-1");
        var lifted = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default, clientConfig,
            token: Encoding.UTF8.GetBytes("acct-1"));
        Pump(server, lifted, config.TickSeconds);
        Assert.True(lifted.Joined);
    }

    [Fact]
    public async Task The_predicate_and_the_store_constructors_refuse_alike()
    {
        var bans = new InMemoryBanStore();
        await bans.BanAsync("acct-banned", "x");
        var gates = new[]
        {
            new BanGateAuthenticator(new AllowAllAuthenticator(), bans),
            new BanGateAuthenticator(new AllowAllAuthenticator(), s => s == "acct-banned"),
        };

        foreach (BanGateAuthenticator gate in gates)
        {
            Assert.False(gate.TryAuthenticate(Encoding.UTF8.GetBytes("acct-banned"), out string subject, out string reason));
            Assert.Equal(string.Empty, subject);
            Assert.Equal(HandshakeToken.BannedReason, reason);

            Assert.True(gate.TryAuthenticate(Encoding.UTF8.GetBytes("acct-ok"), out subject, out reason));
            Assert.Equal("acct-ok", subject);
            Assert.Equal(string.Empty, reason);
        }
    }

    [Fact]
    public async Task The_store_gate_reads_the_store_live()
    {
        var bans = new InMemoryBanStore();
        var gate = new BanGateAuthenticator(new AllowAllAuthenticator(), bans);
        byte[] token = Encoding.UTF8.GetBytes("acct-7");

        Assert.True(gate.TryAuthenticate(token, out _, out _));
        await bans.BanAsync("acct-7", "x");
        Assert.False(gate.TryAuthenticate(token, out _, out string reason));
        Assert.Equal(HandshakeToken.BannedReason, reason);
        await bans.UnbanAsync("acct-7");
        Assert.True(gate.TryAuthenticate(token, out _, out _));
    }

    [Fact]
    public void The_store_gate_refuses_a_null_store()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new BanGateAuthenticator(new AllowAllAuthenticator(), (IBanStore)null!));
        Assert.Throws<ArgumentNullException>(() =>
            new BanGateAuthenticator(null!, new InMemoryBanStore()));
    }
}
