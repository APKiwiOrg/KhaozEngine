using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Text;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// One admin seam over three heads (#826). <see cref="IAdminControllable"/>, <see cref="PlayerRef"/>,
/// <see cref="OnlinePlayer"/> and <see cref="MovementCommitmentRequest"/> live in the <c>KhaozEngine.Netcode</c>
/// assembly under their shipped <c>KhaozEngine.NetWorld</c> names, forwarded from <c>KhaozEngine.NetWorld</c>, so a
/// <see cref="TileWorldServer"/> implements the same interface a <see cref="WorldServer"/> does without its package
/// ever referencing <c>NetWorld</c>.
/// <para>This file imports BOTH namespaces and names the four types bare, which is the shape of a consumer's admin
/// wiring. It compiling at all is the source-compatibility check: a second copy of any of them in
/// <c>KhaozEngine.Netcode</c> would make every such name ambiguous.</para>
/// </summary>
public class AdminSeamTests
{
    private static float Flat(float x, float z) => 0f;

    // The shape both games wrote: an audit decorator implementing only the four members the interface has always
    // required, so the default members (SetPosition, the commitment pair) are the interface's own.
    private sealed class AuditingAdmin : IAdminControllable
    {
        private readonly IAdminControllable inner;
        public List<string> Audit { get; } = new();

        public AuditingAdmin(IAdminControllable inner) => this.inner = inner;

        public IReadOnlyList<OnlinePlayer> ListOnline() => inner.ListOnline();

        public void Teleport(PlayerRef target, Vector3 position)
        {
            Audit.Add("teleport " + Describe(target));
            inner.Teleport(target, position);
        }

        public void Kick(PlayerRef target, string reason)
        {
            Audit.Add("kick " + Describe(target));
            inner.Kick(target, reason);
        }

        public void Broadcast(string text)
        {
            Audit.Add("broadcast");
            inner.Broadcast(text);
        }

        private static string Describe(PlayerRef target) => target.IsSlot ? "#" + target.SlotValue : target.AccountValue;
    }

    [Fact]
    public void The_seam_lives_in_Netcode_under_its_NetWorld_names_and_NetWorld_forwards_it()
    {
        Assembly netcode = typeof(HandshakeToken).Assembly;
        Assembly netWorld = typeof(WorldServer).Assembly;
        Type[] forwarded = netWorld.GetForwardedTypes();

        foreach (Type seam in new[]
                 {
                     typeof(IAdminControllable), typeof(PlayerRef), typeof(OnlinePlayer), typeof(MovementCommitmentRequest),
                 })
        {
            Assert.Same(netcode, seam.Assembly);
            Assert.Equal("KhaozEngine.NetWorld", seam.Namespace);
            Assert.Contains(seam, forwarded);
            // What an assembly built against an earlier KhaozEngine.NetWorld asks the runtime for.
            Assert.Same(seam, Type.GetType($"{seam.FullName}, {netWorld.GetName().Name}", throwOnError: true));
        }

        // The result half of the commitment pair names Locomotion, so it stays where it shipped.
        Assert.Same(netWorld, typeof(MovementCommitmentResult).Assembly);
        Assert.True(typeof(IAdminControllable).IsAssignableFrom(typeof(WorldServer)));
        Assert.True(typeof(IAdminControllable).IsAssignableFrom(typeof(ShardedWorldServer)));
        Assert.True(typeof(IAdminControllable).IsAssignableFrom(typeof(TileWorldServer)));
    }

    [Fact]
    public void One_consumer_decorator_drives_a_float_head_and_a_tile_head_through_ServerAdmin()
    {
        // The float head, exactly as a game wires it today.
        var floatHub = new InMemoryTransportHub();
        var floatConfig = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4 };
        var world = new WorldServer(floatHub.Server, floatConfig, Flat, MoveTuning.Default);
        var floatClient = new WorldClient(floatHub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = floatConfig.TickSeconds }, token: Encoding.UTF8.GetBytes("acct-f"));
        for (int i = 0; i < 30; i++) { world.Poll(); world.Tick(floatConfig.TickSeconds); floatClient.Poll(); }
        Assert.True(floatClient.Joined);

        // The tile head, through the same decorator type and the same facade.
        var tileHub = new InMemoryTransportHub();
        var map = new TileCollisionMap(TileWorldDocument.DefaultPlaneCount);
        map.EnsureRegion(new RegionCoord(0, 0));
        using var tile = new TileWorldServer(tileHub.Server, new TileWorldServerConfig
        {
            TickSeconds = 0.25f,
            StepTicks = new TileStepTicks(walk: 4, run: 2),
            Spawn = new TileCoord(5, 5, 0),
        }, map);
        tile.SpawnPlayer(0, "acct-t", "Tia");

        foreach (IAdminControllable head in new IAdminControllable[] { world, tile })
        {
            var audited = new AuditingAdmin(head);
            var admin = new ServerAdmin(audited);
            admin.Kick(PlayerRef.Account(head is WorldServer ? "acct-f" : "acct-t"), "ke:kicked");
            Assert.Equal(new[] { "kick " + (head is WorldServer ? "acct-f" : "acct-t") }, audited.Audit);
        }

        for (int i = 0; i < 30; i++) { world.Poll(); world.Tick(floatConfig.TickSeconds); floatClient.Poll(); }
        tile.Tick(0.25f);
        Assert.Equal(0, world.PlayerCount);
        Assert.Equal(0, tile.PlayerCount);
    }

    [Fact]
    public void A_commitment_request_still_builds_and_starts_on_a_float_head()
    {
        var hub = new InMemoryTransportHub();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4 };
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default);
        var client = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = config.TickSeconds }, token: Encoding.UTF8.GetBytes("acct-c"));
        for (int i = 0; i < 30; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        var ended = new List<MovementCommitmentResult>();
        server.MovementCommitmentEnded += ended.Add;

        IAdminControllable admin = server;
        MovementCommitmentRequest request = MovementCommitmentRequest.ForBallisticArc(new Vector2(0f, 2f),
            distance: 4f, apexHeight: 1f, durationSeconds: 0.5f);
        Assert.Equal(Vector2.UnitY, request.Direction);
        uint sequence = admin.BeginMovementCommitment(PlayerRef.Account("acct-c"), request);
        Assert.NotEqual(0u, sequence);
        for (int i = 0; i < 180 && ended.Count == 0; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }

        MovementCommitmentResult end = Assert.Single(ended);
        Assert.Equal(sequence, end.Sequence);
    }
}
