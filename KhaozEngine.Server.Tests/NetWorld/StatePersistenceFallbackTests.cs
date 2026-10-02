using System;
using System.Numerics;
using System.Threading.Tasks;
using KhaozEngine.NetWorld;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class StatePersistenceFallbackTests
{
    [Fact]
    public async Task UndecodableFallbackBytes_UseTheNormalQuarantinePath()
    {
        var host = new WorldPersistenceFallbackRig.HostStub();
        var store = new InMemoryWorldStore();
        byte[] invalid = { 0xff };
        var binding = new PersistenceBinding<PlayerMoveState>(
            state => state.Position,
            (state, game) => PlayerRecord.From(state, game).Encode(),
            (byte[] data, out PlayerMoveState state, out byte[]? game) =>
            {
                PlayerRecord record = PlayerRecord.Decode(data);
                state = record.ToState();
                game = record.Game;
                return true;
            },
            (_, _) => null);
        var persistence = new StatePersistence<PlayerMoveState>(host, store, binding, new PersistenceCoreConfig
        {
            LoadFallback = _ => Task.FromResult<byte[]?>(invalid),
        });
        string? quarantined = null;
        persistence.OnRecordQuarantined += (key, _) => quarantined = key;

        host.Join();
        await persistence.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("durable", quarantined);
        Assert.Equal(invalid, await store.LoadAsync("quarantine:player:durable"));
        Assert.Null(await store.LoadAsync("player:durable"));
        Assert.Equal(Vector3.Zero, host.Position());
    }
}
