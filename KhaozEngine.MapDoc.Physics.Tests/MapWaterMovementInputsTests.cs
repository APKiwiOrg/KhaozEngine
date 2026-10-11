using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

public class MapWaterMovementInputsTests
{
    [Fact]
    public void LegacyContext_WadesBelowTheGlobalLevelOnlyWhenAsked()
    {
        MapBuiltWorld world = NativeWorldFixtures.BuildLegacyStripWithWaterLevel(0.5f);
        using var physics = new BepuPhysicsWorld();
        using var registration = MapPhysicsRegistration.Register(world, physics);
        Assert.Null(registration.CreateLegacyMoveContext().Medium);
        Assert.Null(registration.CreateLegacyMoveContext(legacyGlobalWater: false).Medium);
        var context = registration.CreateLegacyMoveContext(legacyGlobalWater: true);
        var medium = context.Medium!;
        Assert.Equal(new MovementMedium(0.5f, true), medium(1f, 1f, 0.2f));
        Assert.Equal(MovementMedium.Dry, medium(1f, 1f, 0.5f));
        Assert.Equal(MovementMedium.Dry, medium(1f, 1f, 0.8f));
        Assert.Equal(0.5f, world.LegacyWaterLevel);
        Assert.Same(physics, context.Physics);
        Assert.Equal(0f, context.GroundHeight(1f, 1f));
        Assert.Null(context.GroundNormal);
        Assert.Null(context.MovementQueries);
        Assert.Equal(new Vector2(0f, 64f), context.ClampXz!(-1f, 65f));
        Assert.Equal(new Vector2(192f, 0f), context.ClampXz!(193f, -1f));
    }

    [Fact]
    public void NativeWorld_RefusesTheLegacyMedium()
    {
        using var physics = new BepuPhysicsWorld();
        using var registration = MapPhysicsRegistration.Register(NativeWorldFixtures.BuildBridgeOverRiver(), physics);
        Assert.Null(registration.World.LegacyWaterLevel);
        Assert.Throws<MapDocumentException>(() => registration.CreateLegacyMoveContext(legacyGlobalWater: true));
    }

    [Fact]
    public void WalkerRuleInputs_AreAvailableUnderTheReleasedLease()
    {
        WaterWorldFixture fixture = NativeWorldFixtures.BridgeOverRiver();
        MapBuiltWorld world = NativeWorldFixtures.Build(fixture);
        using var physics = new BepuPhysicsWorld();
        using var registration = MapPhysicsRegistration.Register(world, physics);
        using var lease = ((IPhysicsQueryLeaseSource)physics).AcquireQueryReadLease();
        var water = new MapWaterQuery(world.Surfaces, fixture.Document.Water!);

        Assert.Equal(MapWaterStatus.Dry, water.Sample(fixture.Point(0f, 2.825f, 0f)).Status);
        Assert.Equal("bridge-deck", NativeWorldFixtures.DownwardHit(registration, lease, new Vector3(0f, 10f, 0f)).OwnerId);
        MapWaterSample wading = water.Sample(fixture.Point(-3f, -1.2f, 6f));
        Assert.Equal((MapWaterStatus.Wet, "river", -1.3f, -0.37f), (wading.Status, wading.BodyId, wading.LowerY, wading.NominalSurfaceY));
        Assert.Equal(MapWaterStatus.Dry, water.Sample(fixture.Point(-3f, -0.37f, 6f)).Status);
        Assert.Equal(world.Surfaces.ReadWitness.ScopedDigest, wading.Witness.ScopedDigest);
        Assert.Equal(world.WaterDigest, wading.WaterDigest);
    }
}
