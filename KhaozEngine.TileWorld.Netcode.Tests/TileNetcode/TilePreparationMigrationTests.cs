using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TilePreparationMigrationTests
{
    [Fact]
    public void Enabled_custom_registry_requires_preparation_state()
    {
        var hub = new InMemoryTransportHub();
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        TileWorldServerConfig config = TileWorldServerTickTests.Config(new TileCoord(20, 20, 0)) with
        { CombatPreparationRules = new PreparationScenario.Profiles() };
        ArgumentException error = Assert.Throws<ArgumentException>(() => new TileWorldServer(hub.Server,
            config, TileMoveSimulatorTests.Bake(doc), registry: new ReplicationRegistry()));
        Assert.Contains("TileCombatPreparationState", error.Message, StringComparison.Ordinal);
        using var disabled = new TileWorldServer(hub.Server, config with { CombatPreparationRules = null },
            TileMoveSimulatorTests.Bake(doc), registry: new ReplicationRegistry());
    }

    [Fact]
    public void Enabled_custom_registry_rejects_another_component_at_the_preparation_id()
    {
        var registry = new ReplicationRegistry();
        registry.Register<NetId>(TileProtocol.TileCombatPreparationStateTypeId,
            (_, _) => throw new InvalidOperationException("Boot validation must not execute codecs."),
            _ => throw new InvalidOperationException("Boot validation must not execute codecs."),
            channels: ReplicationChannels.Migrate);
        AssertInvalidPreparationRegistry(registry);
    }

    [Theory]
    [InlineData(ReplicationChannels.None)]
    [InlineData(ReplicationChannels.Replicate)]
    [InlineData(ReplicationChannels.Persist)]
    [InlineData(ReplicationChannels.Replicate | ReplicationChannels.Persist)]
    [InlineData(ReplicationChannels.Migrate | ReplicationChannels.Replicate)]
    [InlineData(ReplicationChannels.Migrate | ReplicationChannels.Persist)]
    [InlineData(ReplicationChannels.Default)]
    [InlineData(ReplicationChannels.Default | ReplicationChannels.OwnerOnly)]
    public void Enabled_custom_registry_requires_exactly_migrate_only_channels(ReplicationChannels channels)
    {
        var registry = new ReplicationRegistry();
        registry.Register<TileCombatPreparationState>(TileProtocol.TileCombatPreparationStateTypeId,
            (_, _) => throw new InvalidOperationException("Boot validation must not execute codecs."),
            _ => throw new InvalidOperationException("Boot validation must not execute codecs."), channels: channels);
        AssertInvalidPreparationRegistry(registry);
    }

    [Fact]
    public void Enabled_custom_registry_accepts_the_real_preparation_registration()
    {
        var hub = new InMemoryTransportHub();
        var config = TileWorldServerTickTests.Config(new TileCoord(20, 20, 0)) with
        { CombatPreparationRules = new PreparationScenario.Profiles() };
        using var server = new TileWorldServer(hub.Server, config,
            TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld()), registry: TileProtocol.CreateRegistry());
        Assert.Equal(0, server.TickCount);
    }

    static void AssertInvalidPreparationRegistry(ReplicationRegistry registry)
    {
        var hub = new InMemoryTransportHub();
        var map = TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld());
        var config = TileWorldServerTickTests.Config(new TileCoord(20, 20, 0)) with
        { CombatPreparationRules = new PreparationScenario.Profiles() };
        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            new TileWorldServer(hub.Server, config, map, registry: registry));
        Assert.Equal("registry", error.ParamName);
        using var disabled = new TileWorldServer(hub.Server, config with { CombatPreparationRules = null }, map,
            registry: registry);
    }

    [Theory]
    [InlineData(ReplicationChannels.Migrate, true)]
    [InlineData(ReplicationChannels.Replicate, false)]
    [InlineData(ReplicationChannels.Persist, false)]
    public void All_preparation_fields_migrate_but_never_replicate_or_persist(ReplicationChannels channel, bool included)
    {
        ReplicationRegistry registry = TileProtocol.CreateRegistry();
        var world = new World();
        Entity entity = world.Spawn();
        world.Set(entity, new NetId(10));
        var state = new TileCombatPreparationState { LastAttackId = 41, ReadyNotBeforeTick = 109 };
        Assert.True(TileCombatPreparationScheduler.TryCreate(ref state, 100, 10, 20,
            new(3, 1, 0x11223344), 14, 100, 17, 23, out _));
        state.Active = state.Active with { Revision = 7 };
        world.Set(entity, state);
        var view = new ClientReplicationView(registry);
        var copy = new World();
        view.Apply(copy, SnapshotWriter.WriteFiltered(world, registry, new HashSet<long> { 10 }, channel, null));
        Assert.True(view.TryGetEntity(10, out Entity mirrored));
        Assert.Equal(included, copy.TryGet(mirrored, out TileCombatPreparationState restored));
        if (included) Assert.Equal(state, restored);
    }

    [Fact]
    public void Preparation_survives_an_actual_region_handoff_without_restarting()
    {
        using var fight = PreparationScenario.Create();
        fight.SetPosition(fight.Attacker, new TileCoord(63, 20, 0));
        fight.SetTargetPosition(new TileCoord(63, 21, 0));
        fight.Step();
        TileCombatPreparation before = fight.Preparation();
        fight.SetTargetPosition(new TileCoord(65, 20, 0));
        fight.Step();
        Assert.True(fight.Server.Host.TryGetOwner(fight.Attacker, out CellSim owner, out _));
        Assert.Equal(new CellCoord(1, 0), owner.Coord);
        Assert.Equal(before, fight.Preparation());
        fight.AdvanceTo(104);
        Assert.Equal(103L, Assert.Single(fight.Rules.Rolls).Tick);
    }
}
