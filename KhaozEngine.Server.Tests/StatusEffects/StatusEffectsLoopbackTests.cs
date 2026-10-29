using System.Collections.Generic;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using KhaozEngine.StatusEffects;
using KhaozEngine.StatusEffects.Netcode;
using Xunit;

namespace KhaozEngine.Tests.StatusEffects;

public class StatusEffectsLoopbackTests
{
    [Fact]
    public void Loopback_ClientSeesEffectAndRemainingTime()
    {
        const float dt = 1f / 30f;
        using var hub = new InMemoryTransportHub();
        using var server = new ShardedWorldServer(hub.Server,
            new ShardedWorldServerConfig { TickSeconds = dt, MaxPlayers = 4 },
            (_, _) => 0f, MoveTuning.Default, registry: Registry());
        using var client = new WorldClient(hub.CreateClient(), (_, _) => 0f, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = dt, ReceiveServerTick = true }, registry: Registry());
        using var observer = new WorldClient(hub.CreateClient(), (_, _) => 0f, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = dt, ReceiveServerTick = true }, registry: Registry());

        void Frame()
        {
            server.Poll();
            server.Tick(dt);
            client.Poll();
            observer.Poll();
        }

        for (int i = 0; i < 12; i++) Frame();
        Assert.True(client.Joined && observer.Joined);
        long actor = client.LocalNetId;
        Assert.True(server.Host.TryGetOwner(actor, out _, out _));
        var catalog = new StatusEffectCatalog(new[] { new StatusEffectDefinition(new(7), 60) });
        var book = new StatusEffectBook(catalog);
        var projection = new StatusEffectProjection(book, new ShardHostStatusEffectTarget(server.Host));
        server.OnBeforeTick += _ =>
        {
            book.Advance(server.ServerTick);
            projection.Publish();
        };
        long start = server.ServerTick;
        Assert.Equal(StatusEffectApplyResult.Applied, book.Apply(actor, new(7), start, source: 99));

        for (int i = 0; i < 8; i++)
        {
            Frame();
            if (client.TryGetComponent<ActiveStatusEffects>(actor, out _)
                && observer.TryGetComponent<ActiveStatusEffects>(actor, out _))
                break;
        }

        Assert.True(client.TryGetComponent<ActiveStatusEffects>(actor, out var state));
        Assert.True(observer.TryGetComponent<ActiveStatusEffects>(actor, out var observed));
        Assert.Equal(new StatusEffectEntry(new(7), 99, 1, start, start + 60), Assert.Single(state.Entries));
        Assert.Equal<StatusEffectEntry>(state.Entries, observed.Entries);
        Assert.Equal(server.ServerTick, client.LatestServerTick);
        var items = new List<StatusEffectBarItem>();
        StatusEffectBar.Build(state.Entries.AsSpan(), catalog, client.LatestServerTick, items);
        double remaining = Assert.Single(items).RemainingTicks;
        Assert.Equal((double)(start + 60 - client.LatestServerTick), remaining);
        Assert.InRange(remaining, 1, 59);

        for (int i = 0; i < 5; i++) Frame();
        Assert.True(client.TryGetComponent<ActiveStatusEffects>(actor, out state));
        items.Clear();
        StatusEffectBar.Build(state.Entries.AsSpan(), catalog, client.LatestServerTick, items);
        Assert.Equal(remaining - 5, Assert.Single(items).RemainingTicks);
    }

    private static ReplicationRegistry Registry() => MoveProtocol.CreateRegistry(registry =>
        StatusEffectsReplication.Register(registry, ReplicationRegistry.FirstExtensionTypeId));
}
