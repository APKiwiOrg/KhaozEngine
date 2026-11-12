using System.Linq;
using KhaozEngine.Netcode;
using KhaozEngine.Replication;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public sealed class TileClientWorldTickTests : ClientWorldTickContract
{
    protected override ClientHead CreateClient(INetTransport transport, ReplicationRegistry registry) =>
        CreateTileClient(transport, registry);

    private static ClientHead CreateTileClient(INetTransport transport, ReplicationRegistry registry, bool? advance = null)
    {
        var config = new TileWorldClientConfig { TickSeconds = 0.25f, StepTicks = new(walk: 4, run: 2) };
        if (advance.HasValue) config = config with { AdvanceWorldTick = advance.Value };
        var client = new TileWorldClient(transport, config,
            TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld()), registry: registry);
        return new(client.World, client.Poll, () => client.IsJoined, client);
    }

    [Fact]
    public void Caller_owned_tick_opt_out_preserves_tracking_while_the_default_clears_it()
    {
        using var automatic = new Session(this);
        using var manual = new Session(this, (transport, registry) => CreateTileClient(transport, registry, advance: false));
        automatic.Send(present: true, 3);
        manual.Send(present: true, 3);
        automatic.Head.Poll();
        manual.Head.Poll();
        Assert.Single(automatic.World.Added<Tracked>());
        Assert.Single(manual.World.Added<Tracked>());
        ulong automaticTick = automatic.World.Tick;
        ulong manualTick = manual.World.Tick;

        automatic.Head.Poll();
        manual.Head.Poll();
        Assert.Empty(automatic.World.Added<Tracked>());
        Assert.Empty(automatic.World.Events<int>());
        Assert.Equal(automaticTick + 1, automatic.World.Tick);
        Assert.Single(manual.World.Added<Tracked>());
        Assert.Equal(new[] { 3 }, manual.World.Events<int>().ToArray());
        Assert.Equal(manualTick, manual.World.Tick);
        manual.World.AdvanceTick();
        Assert.Empty(manual.World.Added<Tracked>());
        Assert.Empty(manual.World.Events<int>());
    }

    protected override byte[] Frame(byte[] snapshot) => TileProtocol.EncodeSnapshotFrame(1, 0, 0, snapshot);
}
