using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.NetWorld.Fixtures;

internal sealed class OrdinaryNetworkHeads : IDisposable
{
    readonly LoopbackTransport serverTransport;
    readonly LoopbackTransport clientTransport;
    public OrdinaryPlayerWorld ServerWorld { get; }
    public OrdinaryPlayerWorld ClientWorld { get; }
    public WorldServer? Single { get; }
    public ShardedWorldServer? Sharded { get; }
    public WorldClient Client { get; }
    public Dictionary<CellCoord, OrdinaryPlayerWorld> Cells { get; } = new();
    public OrdinaryNetworkHeads(bool sharded, bool roof = false, MoveTuning? tuning = null, bool water = false,
        float h = 0, bool stairs = false, float? resumeY = null, IMovementWaterField? field = null, Vector3? spawn = null, bool pump = true, IMovementWaterField? clientField = null, WorldBounds? bounds = null, MovementBoundary? boundary = null)
    {
        var selected = tuning ?? OrdinaryPlayerWorld.Tuning;
        ServerWorld = new(roof: roof, water: water, groundY: h, stairs: stairs, field: field);
        ClientWorld = new(roof: roof, water: water, groundY: h, stairs: stairs, field: clientField ?? field);
        (serverTransport, clientTransport) = LoopbackTransport.CreatePair();
        if (sharded)
        {
            ShardedWorldServer? created = null;
            Sharded = created = new(serverTransport, new()
            {
                CellSize = 128,
                OverlapMargin = 4,
                InterestRadius = 2,
                TickSeconds = 0.1f,
                MaxPlayers = 2,
                SpawnPosition = _ => spawn ?? new(4, h + 0.75f, 4),
                PhysicsWorldFactory = coord =>
                {
                    var island = new OrdinaryPlayerWorld(created!.FrameFor(coord).Anchor, roof: roof,
                        water: water, groundY: h, stairs: stairs, island: 96, field: field);
                    Cells.Add(coord, island);
                    return island.Physics;
                },
                MovementRegistrationFactory = cell => Cells[cell.Coord].Registration,
                MovementBoundaryFactory = cell => boundary ?? MovementBoundary.Rectangle(-100000, -100000, 100000, 100000),
                MovementWaterFieldFactory = cell => Cells[cell.Coord].Field
            }, (_, _) => h, selected, bounds: bounds);
            if (resumeY is { } saved) Sharded.SetResumePositionProvider((string _, out Vector3 p) => { p = new(4, saved, 4); return true; });
        }
        else
        {
            Single = new(serverTransport, new()
            {
                FrameAnchoring = false,
                TickSeconds = 0.1f,
                MaxPlayers = 2,
                SpawnPosition = _ => spawn ?? new(0, h + 0.75f, 0),
                MovementRegistration = ServerWorld.Registration,
                MovementBoundary = boundary ?? ServerWorld.Boundary,
                MovementWaterField = ServerWorld.Field
            }, ServerWorld.Ground, selected, bounds: bounds, physics: ServerWorld.Physics);
            if (resumeY is { } saved) Single.SetResumePositionProvider((string _, out Vector3 p) => { p = new(0, saved, 0); return true; });
        }
        Client = new(clientTransport, ClientWorld.Ground, selected, new()
        {
            FrameAnchoring = false,
            TickSeconds = 0.1f,
            MovementRegistration = ClientWorld.Registration,
            MovementBoundary = boundary ?? ClientWorld.Boundary,
            MovementWaterField = ClientWorld.Field
        }, bounds: bounds, physics: ClientWorld.Physics);
        if (pump) Pump(6);
    }
    public PlayerMoveState State
    {
        get
        {
            bool found = Single is { } single ? single.TryGetPlayerState(0, out var state) : Sharded!.TryGetPlayerState(0, out state);
            Assert.True(found);
            return state;
        }
    }
    public bool Place(PlayerMoveState state, bool teleport = false) => Single is { } single
        ? single.TrySetPlayerState(0, state, teleport) : Sharded!.TrySetPlayerState(0, state, teleport);
    public bool Reset() => Single is { } single ? single.TryResetToConfiguredSpawn(0) : Sharded!.TryResetToConfiguredSpawn(0);
    public void Pump(int count)
    {
        for (int tick = 0; tick < count; tick++) { Single?.Poll(); Sharded?.Poll(); Single?.Tick(0.1f); Sharded?.Tick(0.1f); Client.Poll(); }
    }
    public void Dispose()
    {
        Client.Dispose(); Single?.Dispose(); Sharded?.Dispose();
        serverTransport.Dispose(); clientTransport.Dispose();
        foreach (var island in Cells.Values) island.Dispose();
        ServerWorld.Dispose(); ClientWorld.Dispose();
    }
}
