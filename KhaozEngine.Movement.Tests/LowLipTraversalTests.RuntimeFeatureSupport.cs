using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using static KhaozEngine.Tests.Movement.CornerFeatureOracle;

namespace KhaozEngine.Tests.Movement;

public partial class LowLipTraversalTests
{
    // Catches the walkable, non-flat support gap after independently proving the actual finite candidate.
    // The two x=-0.128 rows protect the existing near-flat path. Each row performs one ordinary public Hold.
    [Theory]
    [InlineData(-0.145f, false, false)]
    [InlineData(-0.145f, true, false)]
    [InlineData(-0.135f, false, false)]
    [InlineData(-0.135f, true, false)]
    [InlineData(-0.128f, false, true)]
    [InlineData(-0.128f, true, true)]
    public void HoldRetainsCorroboratedFiniteLipSupport(float x, bool excludeFloor, bool nearFlat)
    {
        using var world = new BepuPhysicsWorld();
        StaticHandle floor = world.AddStatic(new BoxShape(new Vector3(8f, 0.1f, 8f)),
            Pose.At(new Vector3(0f, -0.1f, 0f)));
        (StaticHandle target, _, _) = AddFaceProbeShape(world, "mesh", Vector3.Zero);
        using IPhysicsWorldQueryView view = world.CreateQueryViewExcludingStatics(excludeFloor ? [floor] : []);
        var features = Assert.IsAssignableFrom<IPhysicsCapsuleFeatures>(view);
        var leases = Assert.IsAssignableFrom<IPhysicsQueryLeaseSource>(view);
        MoveTuning tuning = Tuning with { WalkSpeed = 1f, RunSpeed = 1f, AirMomentum = false };
        CapsuleShape capsule = CharacterMovement.CapsuleFor(tuning);
        Vector3 feet = new(x, 0f, Row);
        MoveState before = StandingAt(feet);
        Pose candidate;
        using (IPhysicsQueryLease lease = leases.AcquireQueryReadLease())
        {
            candidate = AssertRuntimeMeshCandidate(world, view, features, lease, floor, target,
                capsule, before.Position, excludeFloor, nearFlat);
        }

        // The setup result is historical now. Hold gets the unchanged terrain-rest state and selected receiver.
        var context = new GroundMoveContext((_, _) => 0f, groundNormal: null, physics: world,
            clampXz: null, medium: null, movementQueries: view);
        MoveState after = NpcGroundMovement.Hold(before, Tick, tuning, context);
        bool overlap = view.ComputePenetration(capsule, Pose.At(after.Position), out Vector3 mtv);
        _restingOutput.WriteLine($"runtime x={x:R}, excludeFloor={excludeFloor}, nearFlat={nearFlat}, " +
            $"candidate={candidate.Position}, returned={after.Position}, grounded={after.Grounded}, " +
            $"overlap={overlap}, mtv={mtv}");

        Assert.True(after.Grounded, "Hold lost grounded support at a proved eligible finite lip.");
        Assert.True(float.IsFinite(after.Position.X) && float.IsFinite(after.Position.Y) &&
            float.IsFinite(after.Position.Z));
        Assert.InRange(PlanarDelta(before.Position, after.Position), 0f, GroundTraversalProbe.ArrivalTolerance);
        // This assertion precedes post-Hold feature classification, so a terrain snap fails as a body error.
        AssertRuntimeFiniteNearContact(capsule, Pose.At(after.Position));
        Assert.InRange(after.Position.Y - tuning.CapsuleHalfHeight, float.Epsilon, 0.1f);
        using IPhysicsQueryLease returnedLease = leases.AcquireQueryReadLease();
        AssertRuntimeMeshFeature(world, view, features, returnedLease, target, capsule, Pose.At(after.Position));
    }

    // A legacy call under an owner's existing lease can still run ordinary queries. Optional qualification
    // that cannot acquire its own interval must retain the legacy result, without throwing a nested-lease error.
    [Fact]
    public void HoldUnderAnExistingQueryLeaseRetainsUnavailableQualificationBehavior()
    {
        using var world = new BepuPhysicsWorld();
        StaticHandle floor = world.AddStatic(new BoxShape(new Vector3(8f, 0.1f, 8f)),
            Pose.At(new Vector3(0f, -0.1f, 0f)));
        (StaticHandle target, _, _) = AddFaceProbeShape(world, "mesh", Vector3.Zero);
        using IPhysicsWorldQueryView view = world.CreateQueryViewExcludingStatics([floor]);
        var features = Assert.IsAssignableFrom<IPhysicsCapsuleFeatures>(view);
        var leases = Assert.IsAssignableFrom<IPhysicsQueryLeaseSource>(view);
        MoveTuning tuning = Tuning with { WalkSpeed = 1f, RunSpeed = 1f, AirMomentum = false };
        CapsuleShape capsule = CharacterMovement.CapsuleFor(tuning);
        MoveState before = StandingAt(new Vector3(-0.135f, 0f, Row));
        var context = new GroundMoveContext((_, _) => 0f, groundNormal: null, physics: world,
            clampXz: null, medium: null, movementQueries: view);
        using IPhysicsQueryLease heldLease = leases.AcquireQueryReadLease();
        _ = AssertRuntimeMeshCandidate(world, view, features, heldLease, floor, target, capsule,
            before.Position, excludeFloor: true, nearFlat: false);

        MoveState after = NpcGroundMovement.Hold(before, Tick, tuning, context);

        heldLease.AssertCurrent();
        Assert.True(after.Grounded);
        Assert.InRange(Vector3.Distance(after.Position, new Vector3(-0.135f, 0.75f, Row)),
            0f, GroundTraversalProbe.ArrivalTolerance);
    }

    private static Pose AssertRuntimeMeshCandidate(BepuPhysicsWorld world, IPhysicsWorldQueryView view,
        IPhysicsCapsuleFeatures features, IPhysicsQueryLease lease, StaticHandle floor, StaticHandle target,
        CapsuleShape capsule, Vector3 centre, bool excludeFloor, bool nearFlat)
    {
        lease.AssertCurrent();
        Assert.Same(world, view.SourceWorld);
        Assert.Same(world, lease.SourceWorld);
        Assert.Equal(Vector3.Zero, view.Origin);
        Assert.Equal(Vector3.Zero, lease.Origin);
        Assert.Equal(0.3f, capsule.Radius);
        Assert.Equal(0.9f, capsule.Length);
        Assert.Equal(0.75f, Tuning.CapsuleHalfHeight);
        Assert.Equal(1f / 30f, Tick);
        Vector3 floorRay = new(-2f, 1f, Row);
        Assert.True(world.Raycast(floorRay, -Vector3.UnitY, 2f, out RayHit sourceFloor, QueryFilter.StaticsOnly));
        Assert.Equal(floor, sourceFloor.Body);
        Assert.Equal(!excludeFloor, view.Raycast(floorRay, -Vector3.UnitY, 2f, out RayHit selectedFloor,
            QueryFilter.StaticsOnly));
        if (!excludeFloor) Assert.Equal(floor, selectedFloor.Body);
        Assert.True(view.Raycast(new Vector3(0.5f, 1f, -1f), -Vector3.UnitY, 2f, out RayHit top,
            QueryFilter.StaticsOnly));
        Assert.Equal(target, top.Body);
        Triangle[] source =
        [
            new(new(0f, RestingLip, -2f), new(2f, RestingLip, -2f), new(0f, RestingLip, 2f)),
            new(new(2f, RestingLip, -2f), new(2f, RestingLip, 2f), new(0f, RestingLip, 2f)),
        ];
        AssertInstalledMesh(world, Pose.At(Vector3.Zero), source);

        float probeStart = centre.Y + 2f * Tuning.CapsuleHalfHeight;
        float maxProbe = probeStart - Tuning.CapsuleHalfHeight + 0.01f;
        Assert.True(view.SweepCapsule(capsule, Pose.At(new Vector3(centre.X, probeStart, centre.Z)),
            -Vector3.UnitY, maxProbe, out SweepHit sweep));
        Assert.Equal<StaticHandle?>(target, sweep.Body);
        Assert.True(float.IsFinite(sweep.Distance) && sweep.Distance > 0f && sweep.Distance <= maxProbe);
        // The normal class observes the existing dispatch band only. It does not certify incident geometry.
        Assert.True(sweep.Normal.Y >= MathF.Cos(Tuning.MaxSlopeRadians));
        Assert.Equal(nearFlat, sweep.Normal.Y >= 0.9f);
        Pose candidate = Pose.At(new Vector3(centre.X, probeStart - sweep.Distance, centre.Z));
        AssertRuntimeMeshFeature(world, view, features, lease, target, capsule, candidate);
        return candidate;
    }

    private static (ExactVector Axis, ExactVector Edge) AssertRuntimeFiniteNearContact(CapsuleShape capsule,
        Pose pose)
    {
        ExactVector axis = AxisLower(capsule, pose);
        ExactVector edge = new(R.Zero, R.From(RestingLip), axis.Z);
        Assert.True(axis.X < R.Zero && axis.Y > edge.Y && axis.Z > new R(-2, 1) && axis.Z < new R(2, 1),
            "The returned body must retain its lower-endpoint relation to the interior of the finite west edge.");
        // Every top point has x >= 0 and y = the represented lip height. Increasing the axis parameter
        // increases the positive Y gap, so the lower endpoint and this finite edge are the unique minimum.
        Assert.True(axis.Y < edge.Y + R.From(capsule.Radius), "A face-plane snap lost the corner support relation.");
        AssertNearContactSquared((axis - edge).LengthSquared(), R.From(capsule.Radius));
        return (axis, edge);
    }

    private static void AssertRuntimeMeshFeature(BepuPhysicsWorld world, IPhysicsWorldQueryView view,
        IPhysicsCapsuleFeatures features, IPhysicsQueryLease lease, StaticHandle target, CapsuleShape capsule,
        Pose pose)
    {
        (ExactVector axis, ExactVector edge) = AssertRuntimeFiniteNearContact(capsule, pose);
        CapsuleIncidentFace[] faces = Sentinels(), original = (CapsuleIncidentFace[])faces.Clone();
        CapsuleFeatureResult result = features.QueryCapsuleFeature(lease, target, capsule, pose, 0.0001f,
            faces, QueryFilter.StaticsOnly);
        AssertComplete(view, world, features, lease, target, capsule, result, faces, original,
            CapsuleFeatureKind.OpenBoundary, axis, edge, [V(0, 1, 0)]);
        FeatureEligibility policy = BindFeatureEligibility();
        Assert.True(policy(features, lease, result, faces.AsSpan(0, result.Written),
            MathF.Cos(Tuning.MaxSlopeRadians)), "The independently complete actual feature must be eligible.");
    }
}
