using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.Sharding;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

internal sealed class PreparationScenario : IDisposable
{
    internal sealed class Profiles : ITileCombatPreparationRules
    {
        public TileCombatPreparationProfile Current = new(3, 1, 7);
        public int Reads;
        public TileCombatPreparationProfile ProfileFor(long attackerNetId) { Reads++; return Current; }
    }

    internal sealed class FixedRules : ITileCombatRules
    {
        public byte Ticks = 14;
        public ushort Damage = 5;
        public bool Land = true;
        public bool Allowed = true;
        public readonly List<TileAttackContext> Rolls = new();
        public bool CanAttack(long attackerNetId, long targetNetId) => Allowed;
        public byte AttackTicks(long attackerNetId) => Ticks;
        public TileAttackOutcome Roll(in TileAttackContext context)
        {
            Rolls.Add(context);
            return Land ? TileAttackOutcome.Hit(Damage, 7) : TileAttackOutcome.Miss(7);
        }
    }

    public TileWorldServer Server { get; }
    public FixedRules Rules { get; }
    public Profiles ProfileSource { get; }
    public long Attacker { get; }
    public long Target { get; }
    readonly float tickSeconds;

    PreparationScenario(long nextTick, byte cadence, bool enabled, float tickSeconds)
    {
        this.tickSeconds = tickSeconds;
        ProfileSource = new Profiles();
        Rules = new FixedRules { Ticks = cadence };
        var hub = new InMemoryTransportHub();
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld(4, new RegionCoord(0, 0), new RegionCoord(1, 0));
        Server = new TileWorldServer(hub.Server, TileWorldServerTickTests.Config(new TileCoord(20, 20, 0)) with
        {
            TickSeconds = tickSeconds,
            CombatPreparationRules = enabled ? ProfileSource : null
        }, TileMoveSimulatorTests.Bake(doc), new TileDocumentTargets(doc, TileMoveSimulatorTests.Catalogs),
            new AllowAllAuthenticator());
        Server.CombatRules = Rules;
        AdvanceTo(nextTick);
        Attacker = Server.SpawnActor(new TileCoord(20, 20, 0), new TileActorSpawn(1000, cadence, TileDirection.N));
        Target = Server.SpawnActor(new TileCoord(20, 21, 0), new TileActorSpawn(1000, cadence, TileDirection.S));
        TileCombatResolveTests.Lock(Server, Attacker, Target);
    }

    public static PreparationScenario Create(long nextTick = 100, byte cadence = 14,
        bool enabled = true, float tickSeconds = 1f / 6f) => new(nextTick, cadence, enabled, tickSeconds);

    public void Step() => Server.Tick(tickSeconds);
    public void AdvanceTo(long nextTick)
    {
        while (Server.TickCount < nextTick) Step();
        Assert.Equal(nextTick, Server.TickCount);
    }

    public TileCombatPreparation Preparation()
    {
        Assert.True(Server.TryGetCombatPreparation(Attacker, out TileCombatPreparation preparation));
        return preparation;
    }

    public void SetProfile(TileCombatPreparationProfile profile) => ProfileSource.Current = profile;
    public void SetTargetPosition(TileCoord tile) => SetPosition(Target, tile);
    public void SetPosition(long netId, TileCoord tile)
    {
        Assert.True(Server.Host.TryGetOwner(netId, out CellSim cell, out Entity e));
        Assert.True(cell.World.TryGet(e, out TileMoveState state));
        state.Tile = tile;
        state.StepFrom = tile;
        cell.World.Set(e, state);
    }

    public void Dispose() => Server.Dispose();
}
