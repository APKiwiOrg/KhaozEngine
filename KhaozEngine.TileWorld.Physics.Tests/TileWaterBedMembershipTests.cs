using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class TileWaterBedMembershipTests
{
    // -80 cm in the lattice's binary32 metre representation, with a 0.75 m capsule half height.
    const float Bed = -0.7999999523162842f;
    const float BedCentre = -0.04999995231628418f;
    const float Dt = 1f / 60;

    public static IEnumerable<object[]> StartPhases()
    {
        // The reported failures, followed by both sides of a triangle diagonal and a tile edge.
        foreach (float z in new[] { -25.571007f, -25.684608f, -26.126749f, -25.5f, -26f })
        {
            yield return new object[] { z };
            if (z != -25.5f && z != -26f) continue;
            yield return new object[] { MathF.BitDecrement(z) };
            yield return new object[] { MathF.BitIncrement(z) };
        }
    }

    [Theory]
    [MemberData(nameof(StartPhases))]
    public void GroundedContactKeepsExactBedMembershipAndWadePace(float startZ)
    {
        using var scene = new BedScene();
        using var lease = scene.Acquire();
        float startHeight = scene.Colliders.MovementGroundHeight(20.5f, startZ);
        var state = new FramedMovementState(new MoveState
        {
            Position = new(20.5f, startHeight + scene.Tuning.CapsuleHalfHeight, startZ),
            Grounded = true,
        }, scene.Frame, null);

        for (int tick = 0; tick < 24; tick++)
        {
            Vector3 before = state.State.Position;
            var result = scene.Binding.StepTowards(state, -Vector2.UnitY, false, Dt, lease);
            Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
            Assert.True(result.State.State.Grounded);
            Assert.False(result.State.State.Swimming);
            Assert.NotNull(result.State.Selection!.Value.Support);
            // 0.78 m of water over a 1.5 m body gives depth 0.52. The 0.15..0.65 ramp to 0.6 gives 0.704.
            Assert.Equal(new Vector2(0, -2.816f), result.State.State.CommandedVelocity);
            Assert.Equal(new Vector3(before.X, BedCentre, before.Z - 2.816f * Dt), result.State.State.Position);

            MovementSpaceKey space = result.State.Selection.Value.Space;
            var point = scene.Colliders.Medium.SampleCentreWater(
                new(before, scene.Tuning.CapsuleRadius, scene.Tuning.CapsuleHalfHeight, space, null), Vector3.Zero);
            Assert.Equal(MovementAvailability.Known, point.Availability);
            Assert.True(point.InWater, $"Start {startZ:R}, tick {tick}, centre {before.Y:R} was dry.");
            Assert.Equal(Bed, point.Interval!.Value.LowerY);
            Assert.Equal(-0.02f, point.Interval.Value.UpperY);
            Assert.Equal((double)Bed, (double)before.Y - scene.Tuning.CapsuleHalfHeight);
            Assert.Equal(Bed, scene.Colliders.MovementGroundHeight(before.X, before.Z));
            Assert.Equal(Bed, scene.Colliders.Ground.HeightAt(before.X, before.Z));
            Assert.Equal(BedCentre, result.State.State.Position.Y);
            state = result.State;
        }
    }

    [Fact]
    public void ExactWaterMembershipStillExcludesFeetBelowTheBed()
    {
        using var scene = new BedScene();
        MovementSpaceKey space = new("bed-world", "ordinary");
        foreach (float centreY in new[] { MathF.BitDecrement(BedCentre), BedCentre, MathF.BitIncrement(BedCentre) })
        {
            var body = new MovementBodyQuery(new(20.5f, centreY, -25.571007f),
                scene.Tuning.CapsuleRadius, scene.Tuning.CapsuleHalfHeight, space, null);
            var point = scene.Colliders.Medium.SampleCentreWater(body, Vector3.Zero);
            Assert.Equal(MovementAvailability.Known, point.Availability);
            Assert.Equal(centreY >= BedCentre, point.InWater);
        }
    }

    sealed class BedScene : IDisposable
    {
        readonly BepuPhysicsWorld _physics = new(Vector3.Zero);
        readonly TileColliderRegistration _installed;
        internal MoveTuning Tuning { get; } = OrdinaryTileWaterTests.Tuning(WaterTraversalMode.WadeOnly)
            with { WadeMinSpeedScale = 0.6f };
        internal TileWorldColliders Colliders { get; }
        internal EffectiveMovementBinding Binding { get; }
        internal MovementFrameDescriptor Frame { get; } = new(default, Vector3.Zero, 1);

        internal BedScene()
        {
            var document = TileWorldPhysicsTestData.FlatWorld();
            for (int z = 24; z <= 29; z++)
                for (int x = 16; x <= 24; x++)
                    document.SetUnderlay(x, z, 0, TileWorldPhysicsTestData.Water);
            for (int z = 25; z <= 29; z++)
                for (int x = 17; x <= 24; x++)
                    document.SetCornerHeightCm(x, z, 0, -80);
            Colliders = TileWorldColliders.Build(document, TileWorldPhysicsTestData.Catalogs(), "bed-world");
            _installed = Colliders.AddTo(_physics);
            Binding = EffectiveMovementBinding.Ordinary(Tuning, _physics, _installed.CreateMovementRegistration("bed-world"),
                Colliders.MovementGroundHeight, Colliders.MovementGroundNormal,
                MovementBoundary.Rectangle(0, -64, 64, 0), Colliders.Medium);
        }

        internal MovementQueryLease Acquire()
        {
            Assert.Equal(MovementAvailability.Known, Binding.TryAcquire(Binding.CreateOrdinaryScope(Frame), out var lease));
            return Assert.IsType<MovementQueryLease>(lease);
        }

        public void Dispose() { Binding.Dispose(); _installed.Dispose(); _physics.Dispose(); }
    }
}
