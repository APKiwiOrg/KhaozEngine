using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.Movement;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class TileAnalyticSupportIdentityTests
{
    // #1697. Identity follows the captured lattice and real shared edges, including rebasing and source edits.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapturedIdentityAndAdjacencyDoNotDependOnLocalHandlesOrLiveDocument(bool rebase)
    {
        var doc = TileWorldMovementNavigationTests.Drawn(0, 0, 4, 3);
        var catalogs = TileWorldCatalogs.LoadJson("{\"materials\":[],\"archetypes\":[]}", "identity-tests");
        var colliders = TileWorldColliders.Build(doc, catalogs, "world");
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        if (rebase) world.Rebase(new(100, 8, -100));
        using var installed = colliders.AddTo(world);
        var registration = installed.CreateMovementRegistration("world");
        var source = Assert.IsAssignableFrom<IMovementAnalyticSupportSource>(registration.AnalyticSupport);
        Assert.True(source.Matches(colliders.MovementGroundHeight, colliders.MovementGroundNormal, OrdinaryGroundSpace.World));
        Assert.False(source.Matches((_, _) => 0, colliders.MovementGroundNormal, OrdinaryGroundSpace.World));
        Assert.Equal(MovementAvailability.Known, source.Describe(new(1.75f, -1.25f), out var first));
        Assert.Equal(MovementAvailability.Known, source.Describe(new(2.25f, -1.75f), out var next));
        Assert.Equal(MovementAvailability.Known, source.Describe(new(3.75f, -1.25f), out var remote));
        Assert.NotEqual(first.Key, next.Key);
        Assert.Equal(new Vector3(1, 0, -1), first.A);
        Assert.Equal(new Vector3(2, 0, -1), first.B);
        Assert.Equal(new Vector3(2, 0, -2), first.C);
        Assert.True(source.AreAdjacent(first.Key, next.Key));
        Assert.False(source.AreAdjacent(first.Key, remote.Key));
        using var repeatWorld = new BepuPhysicsWorld(Vector3.Zero);
        repeatWorld.AddStatic(new KhaozEngine.Physics.BoxShape(Vector3.One), KhaozEngine.Physics.Pose.At(new(10, -10, 10)));
        using var repeatInstalled = colliders.AddTo(repeatWorld);
        var repeat = repeatInstalled.CreateMovementRegistration("world").AnalyticSupport!;
        Assert.Equal(MovementAvailability.Known, repeat.Describe(new(1.75f, -1.25f), out var independent));
        Assert.Equal(first, independent);
        doc.SetCornerHeightCm(1, 1, 0, 400);
        Assert.Equal(MovementAvailability.Known, source.Describe(new(1.75f, -1.25f), out var retained));
        Assert.Equal(first, retained);
        var changed = TileWorldColliders.Build(doc, catalogs, "world");
        using var otherWorld = new BepuPhysicsWorld(Vector3.Zero);
        using var otherInstalled = changed.AddTo(otherWorld);
        var other = otherInstalled.CreateMovementRegistration("world").AnalyticSupport!;
        Assert.Equal(MovementAvailability.Known, other.Describe(new(1.75f, -1.25f), out var different));
        Assert.NotEqual(first.Key.Source, different.Key.Source);
        Assert.Equal(MovementAvailability.Unresolved, source.Describe(new(8, -8), out _));
        Assert.Equal(MovementAvailability.Unresolved, source.GetFeature(different.Key, out _));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OmittedGroundCannotSupplyInstalledAnalyticIdentity(bool omitGround)
    {
        var doc = TileWorldMovementNavigationTests.Drawn(0, 0, 2, 2);
        var catalogs = TileWorldCatalogs.LoadJson("{\"materials\":[],\"archetypes\":[]}", "identity-installation-tests");
        var colliders = TileWorldColliders.Build(doc, catalogs, "world");
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using var installed = colliders.AddTo(world, collider => !omitGround || collider.Kind != TileColliderKind.Ground);
        var registration = installed.CreateMovementRegistration("world");
        if (omitGround) Assert.Null(registration.AnalyticSupport);
        else Assert.NotNull(registration.AnalyticSupport);
    }

}
