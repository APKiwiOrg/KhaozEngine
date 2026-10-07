using System;
using System.Numerics;
using System.Reflection;
using BepuPhysics;
using BepuPhysics.Collidables;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using BepuSim = BepuPhysics.Simulation;

namespace KhaozEngine.Tests.Physics;

public class CapsuleBoxFractionSweepTests
{
    const double Width = 1d / 1024;
    delegate bool SweepCall(Shapes shapes, TypedIndex shape, RigidPose pose, Vector3 centre,
        float radius, float halfCylinderLength, Vector3 displacement, double maximumFractionWidth,
        int maximumCells, out double lower, out double? upper);

    [Fact]
    public void ASeparatedWholePathIsClear() =>
        Clear(new(-2, 3, 0), new(4, 0, 0));

    [Fact]
    public void ForwardEntryHasAProvedContainingBracket() =>
        Hit(new(-2, 0, 0), new(4, 0, 0), 0.3125);

    [Fact]
    public void ReversedEntryHasAProvedContainingBracket() =>
        Hit(new(2, 0, 0), new(-4, 0, 0), 0.3125);

    [Fact]
    public void ClosedEndpointTangencyIsHit() =>
        Hit(new(-2, 0, 0), new(1.25f, 0, 0), 1);

    [Fact]
    public void InitialTangencyIsHitEvenWhenMovingAway() =>
        Hit(new(-0.75f, 0, 0), new(-1, 0, 0), 0);

    [Fact]
    public void InitialPenetrationHasNoForwardClearPrefix() =>
        Hit(Vector3.Zero, new(4, 0, 0), 0);

    [Fact]
    public void StationarySeparatedCapsuleIsClear() =>
        Clear(new(2, 0, 0), Vector3.Zero);

    [Fact]
    public void StationaryPenetratingCapsuleIsHit() =>
        Hit(Vector3.Zero, Vector3.Zero, 0);

    [Fact]
    public void ClearEndpointsDoNotHideAnInteriorThinBox() =>
        Hit(new(-2, 0, 0), new(4, 0, 0), (2d - 0.25 - 0.03125) / 4,
            shape: new BoxShape(new Vector3(0.03125f, 0.5f, 0.5f)));

    [Fact]
    public void DiagonalClearanceRequiresWholeCellProofsBeyondItsBoundingRectangle() =>
        Clear(new(-2, 0, 0), new(2, 0, 2));

    [Fact]
    public void VerticalEntryIncludesTheCapsuleAxisExtent() =>
        Hit(new(0, 3, 0), new(0, -4, 0), 0.4375);

    [Fact]
    public void ExactCornerEndpointTangencyIsNotASeparation() =>
        Hit(new(2, 0, 1), new(-1.125f, 0, 0), 1, radius: 0.625f);

    [Fact]
    public void TranslatedInstalledBoxKeepsTheOriginalPathFractions() =>
        Hit(new(52, 1.5f, -98), new(4, 0, 0), 0.3125, Pose.At(new(54, 1.5f, -98)));

    [Fact]
    public void ExhaustedCellBudgetRefusesWithoutPublishingItsClearPrefix() =>
        Refused(new(-2, 0, 0), new(4, 0, 0), maximumCells: 1);

    [Fact]
    public void InvalidWidthsAndBudgetsRefuse()
    {
        foreach (double width in new[] { 0d, -1, double.NaN, double.PositiveInfinity, Math.BitIncrement(1d) })
            Refused(new(-2, 3, 0), new(4, 0, 0), width: width);
        Refused(new(-2, 3, 0), new(4, 0, 0), maximumCells: 0);
        Refused(new(-2, 3, 0), new(4, 0, 0), maximumCells: 257);
    }

    [Fact]
    public void UnsupportedGeometryAndInvalidPathRefuse()
    {
        Refused(new(-2, 3, 0), new(4, 0, 0), shape: new SphereShape(0.5f));
        Refused(new(-2, 3, 0), new(4, 0, 0),
            pose: new Pose(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f)));
        Refused(new(float.NaN, 3, 0), new(4, 0, 0));
    }

    static void Clear(Vector3 centre, Vector3 delta)
    {
        var result = Query(centre, delta);
        Assert.True(result.Complete);
        Assert.Equal(1d, result.Lower);
        Assert.Null(result.Upper);
    }

    static void Hit(Vector3 centre, Vector3 delta, double expected, Pose? pose = null,
        PhysicsShape? shape = null, float radius = 0.25f)
    {
        var result = Query(centre, delta, pose, shape, radius);
        Assert.True(result.Complete);
        Assert.NotNull(result.Upper);
        Assert.InRange(result.Lower, 0, expected);
        Assert.InRange(result.Upper.Value, expected, 1);
        Assert.InRange(result.Upper.Value - result.Lower, 0, Width);
        if (expected == 0) Assert.Equal(0d, result.Upper.Value);
    }

    static void Refused(Vector3 centre, Vector3 delta, Pose? pose = null, PhysicsShape? shape = null,
        double width = Width, int maximumCells = 256)
    {
        var result = Query(centre, delta, pose, shape, width: width, maximumCells: maximumCells);
        Assert.False(result.Complete);
        Assert.Equal(0d, result.Lower);
        Assert.Null(result.Upper);
    }

    static (bool Complete, double Lower, double? Upper) Query(Vector3 centre, Vector3 delta,
        Pose? pose = null, PhysicsShape? shape = null, float radius = 0.25f,
        double width = Width, int maximumCells = 256)
    {
        Pose sourcePose = pose ?? Pose.Identity;
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(shape ?? new BoxShape(new Vector3(0.5f)), sourcePose);
        using IPhysicsWorldQueryView view = world.CreateQueryViewExcludingStatics([]);
        using IPhysicsQueryLease lease = ((IPhysicsQueryLeaseSource)view).AcquireQueryReadLease();
        lease.AssertCurrent();
        Assert.Same(world, view.SourceWorld);
        FieldInfo? field = typeof(BepuPhysicsWorld).GetField("_sim", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        BepuSim simulation = Assert.IsType<BepuSim>(field.GetValue(world));
        Assert.Equal(1, simulation.Statics.Count);
        ref var target = ref simulation.Statics[0];
        Assert.Equal(sourcePose.Position, target.Pose.Position);
        Assert.Equal(sourcePose.Orientation, target.Pose.Orientation);
        Type? type = typeof(BepuPhysicsWorld).Assembly.GetType("KhaozEngine.Physics.Bepu.CapsuleBoxFractionSweep");
        Assert.NotNull(type);
        MethodInfo? method = type.GetMethod("TrySweep", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        bool complete = method.CreateDelegate<SweepCall>()(simulation.Shapes, target.Shape, target.Pose,
            centre, radius, 0.5f, delta, width, maximumCells, out double lower, out double? upper);
        lease.AssertCurrent();
        return (complete, lower, upper);
    }
}
