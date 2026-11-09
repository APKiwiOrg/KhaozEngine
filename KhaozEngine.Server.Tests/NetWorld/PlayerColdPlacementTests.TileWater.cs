using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public partial class PlayerColdPlacementTests
{
    [Theory]
    [InlineData(false, WaterTraversalMode.SurfaceSwimmer)]
    [InlineData(true, WaterTraversalMode.SurfaceSwimmer)]
    [InlineData(true, WaterTraversalMode.WadeOnly)]
    [InlineData(true, WaterTraversalMode.DryOnly)]
    public void SeededTileWaterBanksTangenciesAndRegionSeamsEqualTheUncertifiedAlgorithm(bool nonuniform, WaterTraversalMode mode)
    {
        var document = TileWorldPhysicsTestData.FlatWorld(new RegionCoord(0, 0), new RegionCoord(1, 0));
        for (int z = 3; z <= 8; z++)
            for (int x = 58; x <= 70; x++)
            {
                document.SetCornerHeightCm(x, z, 0, (short)(nonuniform && x > 64 ? -80 : -200));
                if (x >= 60 && x <= 68) document.SetUnderlay(x, z, 0, TileWorldPhysicsTestData.Water);
            }
        document.SetCornerHeightCm(60, 3, 0, 2);
        var colliders = TileWorldColliders.Build(document, TileWorldPhysicsTestData.Catalogs(), "water-world");
        using var physics = new BepuPhysicsWorld(Vector3.Zero);
        using var installed = colliders.AddTo(physics);
        var tuning = Fixtures.OrdinaryPlayerWorld.Tuning with { Water = new(mode, 7) };
        using var simulator = new PlayerMoveSimulator(colliders.MovementGroundHeight, tuning,
            colliders.MovementGroundNormal, null, physics, null, SamplerSpace.World, null, colliders.Medium,
            installed.CreateMovementRegistration("water-world"), MovementBoundary.Rectangle(0, -64, 128, 0));
        var random = new Random(1730);
        using var work = new PlacementWork();
        for (int i = 0; i < 32; i++)
        {
            float x = i < 8 ? new[] { 59.75f, MathF.BitDecrement(59.75f), MathF.BitIncrement(59.75f),
                60.25f, 63.75f, 64f, 64.25f, 69.25f }[i] : random.Next(944, 1120) / 16f;
            float z = -5.5f;
            var basis = new PlayerMoveState
            {
                Move = new MoveState
                { Position = new(x, colliders.MovementGroundHeight(x, z) + tuning.CapsuleHalfHeight, z), Grounded = true }
            };
            var command = new MoveCommand(i % 2 == 0 ? Vector2.UnitX : -Vector2.UnitX, false, 0);
            using var read = simulator.BeginExplicitRead(basis);
            var actual = simulator.Step(basis, command, 1f / 30);
            var outcome = simulator.LastExplicitOutcome;
            var expected = ReferenceStep(simulator, read!, basis, command, 1f / 30);
            Assert.Equal(expected.Outcome, outcome);
            AssertExact(expected.State, actual);
            AssertKernelExact(simulator, read!, basis, command, 1f / 30);
        }
        Assert.True(work.Validations >= 32);
        Assert.True(work.Reuses > 0);
    }
}
