using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using static KhaozEngine.Tests.Physics.CertifiedGeneralSweepFixture;

namespace KhaozEngine.Tests.Physics;

public sealed class CertifiedSweepDomainTests
{
    public static IEnumerable<object[]> Routes() => CertifiedStoredBoundsResidualTests.Routes();

    [Theory]
    [MemberData(nameof(Routes))]
    public void SelectedGeneralLeafRefusesEveryDomainOverflowBeforePruning(bool view, bool dimensions)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(new SphereShape(1), Pose.At(Vector3.Zero));
        StaticHandle[]? exclusions = view ? [world.AddStatic(new BoxShape(new(0.25f)), Pose.At(new(0, 10, 0)))] : null;
        Vector3 centre = new(10000, 0, 0), delta = new(0, 0, 0.125f);
        float over = MathF.BitIncrement(1000);
        Refused(Read(world, centre, delta, radius: over, length: 0, useDimensions: dimensions, exclusions: exclusions));
        Refused(Read(world, centre, delta, length: 2 * over, useDimensions: dimensions, exclusions: exclusions));
        for (int axis = 0; axis < 3; axis++)
        {
            Vector3 outside = axis == 0 ? new(1000001, 0, 0) : axis == 1 ? new(0, 1000001, 0) : new(0, 0, 1000001);
            Refused(Read(world, outside, delta, length: 0, useDimensions: dimensions, exclusions: exclusions));
            Refused(Read(world, -outside, delta, length: 0, useDimensions: dimensions, exclusions: exclusions));
            Refused(Read(world, centre, outside, length: 0, useDimensions: dimensions, exclusions: exclusions));
            Refused(Read(world, centre, -outside, length: 0, useDimensions: dimensions, exclusions: exclusions));
        }
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public void IdentityOnlySelectionKeepsLargeQueriesAfterRemovalAndExclusion(bool view, bool dimensions)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(new BoxShape(new(0.25f)), Pose.At(Vector3.Zero));
        StaticHandle general = world.AddStatic(new SphereShape(1), Pose.At(new(100, 0, 0)));
        Vector3 centre = new(10000, 0, 0), delta = new(0, 0, 0.125f);
        if (!view) world.RemoveStatic(general);
        Clear(Read(world, centre, delta, radius: 1001, length: 2002, useDimensions: dimensions,
            exclusions: view ? [general] : null), 0.125f);
        const float radius = 281474976710656f;
        SameBits(new(CapsuleSweepStatus.Clear, 0, null, 0), Read(world, new(MathF.BitIncrement(radius), 0, 0),
            Vector3.Zero, radius: radius, length: 0, useDimensions: dimensions, exclusions: view ? [general] : null));
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public void DomainIncludesItsClosedBoundary(bool view, bool dimensions)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(new SphereShape(1), Pose.At(new(-100, 0, 0)));
        Clear(Read(world, new(1000000, 1000000, 1000000), new(0, 0, -1000000), radius: 1000, length: 2000,
            useDimensions: dimensions, exclusions: view
                ? [world.AddStatic(new BoxShape(new(0.25f)), Pose.At(new(0, 10, 0)))] : null), 1000000);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public void MobilitySelectionTracksGeneralDynamicsAcrossSleepWakeAndRemoval(bool view, bool dimensions)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(new SphereShape(1), Pose.At(Vector3.Zero));
        var identity = world.AddDynamic(new BoxShape(new(0.25f)), Pose.At(new(10, 0, 0)), new DynamicBodyDescription(1));
        StaticHandle[]? exclusions = view ? [world.AddStatic(new BoxShape(new(0.25f)), Pose.At(new(0, 10, 0)))] : null;
        Vector3 centre = new(10000, 0, 0), delta = new(0, 0, 0.125f);
        CapsuleSweepResult Query(QueryFilter filter) => Read(world, centre, delta, radius: 1001, length: 0,
            filter: filter, useDimensions: dimensions, exclusions: exclusions);
        Clear(Query(QueryFilter.DynamicsOnly), 0.125f);
        Refused(Query(QueryFilter.StaticsOnly));
        var general = world.AddDynamic(new SphereShape(1), Pose.At(new(20, 0, 0)), new DynamicBodyDescription(1));
        Refused(Query(QueryFilter.DynamicsOnly));
        var bodies = (Dictionary<int, (BepuPhysics.BodyHandle Handle, BepuPhysics.Collidables.TypedIndex Shape)>)Field(world, "_dynamics");
        Mutate(world, sim => { var body = sim.Bodies[bodies[general.Value].Handle]; body.Awake = false; });
        Assert.False(world.IsAwake(general));
        Refused(Query(QueryFilter.DynamicsOnly));
        world.SetDynamicVelocity(general, Vector3.UnitX, Vector3.Zero);
        Assert.True(world.IsAwake(general));
        Refused(Query(QueryFilter.DynamicsOnly));
        world.RemoveDynamic(general);
        Clear(Query(QueryFilter.DynamicsOnly), 0.125f);
        world.RemoveDynamic(identity);
    }

    [Fact]
    public void ExistingViewSelectionTracksAddedRemovedAndReposedGeneralLeaves()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle excluded = world.AddStatic(new SphereShape(1), Pose.At(Vector3.Zero));
        using var view = world.CreateQueryViewExcludingStatics([excluded]);
        var dimensions = new CapsuleQueryDimensions(1001, 0);
        Pose pose = Pose.At(new(10000, 0, 0));
        Vector3 delta = new(0, 0, 0.125f);
        CapsuleSweepResult ReadView() => ((IPhysicsCapsuleGeometryQueries)view).SweepCapsuleCertified(dimensions, pose, delta);
        Clear(ReadView(), 0.125f);
        StaticHandle added = world.AddStatic(new SphereShape(1), Pose.At(Vector3.Zero));
        Refused(ReadView());
        world.RemoveStatic(added);
        Clear(ReadView(), 0.125f);
        StaticHandle box = world.AddStatic(new BoxShape(new(0.25f)), Pose.At(Vector3.Zero));
        Clear(ReadView(), 0.125f);
        var native = Static(world, box).Handle;
        Mutate(world, sim =>
        {
            sim.Statics.GetDescription(native, out var description);
            description.Pose.Orientation = new(0.5f, 0.5f, 0.5f, 0.5f);
            sim.Statics.ApplyDescription(native, description);
        }, box.Value);
        Refused(ReadView());
        Mutate(world, sim =>
        {
            sim.Statics.GetDescription(native, out var description);
            description.Pose.Orientation = Quaternion.Identity;
            sim.Statics.ApplyDescription(native, description);
        }, box.Value);
        Clear(ReadView(), 0.125f);
    }
}
