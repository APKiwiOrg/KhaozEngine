using System;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;

namespace KhaozEngine.Tests.TileNetcode;

internal sealed class TilePresentationScenario : IDisposable
{
    readonly PreparationDeliveryScenario delivery = new(enabled: false);
    long tick = 10;

    public TileWorldClient Client => delivery.Client;

    public void Snapshot(params (long Id, TileMoveState State)[] remotes)
    {
        var world = new World();
        Add(Client.LocalNetId, TileMoveState.At(new TileCoord(20, 20, 0), TileDirection.N));
        foreach (var remote in remotes) Add(remote.Id, remote.State);
        byte[] snapshot = SnapshotWriter.Write(world, TileProtocol.CreateRegistry(), ownerNetId: Client.LocalNetId);
        delivery.Inject(TileProtocol.EncodeSnapshotFrame(Client.LocalNetId, 0, tick++, snapshot));
        Client.Poll();

        void Add(long id, TileMoveState state)
        {
            Entity entity = world.Spawn();
            world.Set(entity, new NetId(id));
            world.Set(entity, state);
        }
    }

    public void Dispose() => delivery.Dispose();
}
