using KhaozEngine.Netcode;
using KhaozEngine.Replication;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;

namespace KhaozEngine.Tests.TileNetcode;

public sealed class TileClientWorldTickTests : ClientWorldTickContract
{
    protected override ClientHead CreateClient(INetTransport transport, ReplicationRegistry registry, bool? advance)
    {
        var config = new TileWorldClientConfig { TickSeconds = 0.25f, StepTicks = new(walk: 4, run: 2) };
        if (advance.HasValue) config = config with { AdvanceWorldTick = advance.Value };
        var client = new TileWorldClient(transport, config,
            TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld()), registry: registry);
        return new(client.World, client.Poll, () => client.IsJoined, client);
    }

    protected override byte[] Frame(byte[] snapshot) => TileProtocol.EncodeSnapshotFrame(1, 0, 0, snapshot);
}
