using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.WaterPrismCensus;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

/// <summary>The census scenario's player simulator over any authored tile world, with the water field the test builds
/// from the world's tile producer. Each step is one authority operation under a fresh read, as in
/// <see cref="WaterPrismScenario"/>.</summary>
internal sealed class TileWaterMovementScene : IDisposable
{
    internal const string WorldId = "water-world";
    readonly BepuPhysicsWorld _world = new(Vector3.Zero);
    readonly TileColliderRegistration _installed;
    internal TileWorldColliders Colliders { get; }
    internal PlayerMoveSimulator Simulator { get; }

    internal TileWaterMovementScene(TileWorldDocument document, WaterTraversalMode mode, MovementBoundary boundary,
        Func<TileMediumSampler, IMovementWaterField> field)
    {
        Colliders = TileWorldColliders.Build(document, TileWorldPhysicsTestData.Catalogs(), WorldId);
        _installed = Colliders.AddTo(_world);
        var tuning = WaterPrismScenario.Tuning with { Water = new(mode) };
        Simulator = new(Colliders.MovementGroundHeight, tuning, Colliders.MovementGroundNormal, null, _world,
            Colliders.Medium.MediumDelegate, SamplerSpace.World, null, field(Colliders.Medium),
            _installed.CreateMovementRegistration(WorldId), boundary);
    }

    /// <summary>A grounded body standing on the captured ground at <paramref name="x"/>, <paramref name="z"/>.</summary>
    internal PlayerMoveState Standing(float x, float z) => new()
    {
        Move = new()
        {
            Position = new(x, Colliders.MovementGroundHeight(x, z) + WaterPrismScenario.Tuning.CapsuleHalfHeight, z),
            Grounded = true,
            SpeedScale = 1,
        },
    };

    internal PlayerMoveState Step(PlayerMoveState state, MoveCommand command, out MovementStepOutcome? outcome)
    {
        using var read = Simulator.BeginExplicitRead(state);
        var next = Simulator.Step(state, command, WaterPrismScenario.Dt);
        outcome = Simulator.LastExplicitOutcome;
        return next;
    }

    public void Dispose() { Simulator.Dispose(); _installed.Dispose(); _world.Dispose(); }
}
