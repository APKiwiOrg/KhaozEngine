using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using static KhaozEngine.Tests.Movement.CornerFeatureOracle;

namespace KhaozEngine.Tests.Movement;

public partial class LowLipTraversalTests
{
    // Catches mesh refusal, plane substitution, a wrong cap endpoint, or consumption in the wrong selected view.
    // The old sweep supplies only a target and candidate centre. Its point and normal are never read here.
    [Theory]
    [InlineData(-0.145f, false, false)]
    [InlineData(-0.145f, true, false)]
    [InlineData(-0.135f, false, false)]
    [InlineData(-0.135f, true, false)]
    [InlineData(-0.128f, false, false)]
    [InlineData(-0.128f, true, false)]
    [InlineData(-0.145f, false, true)]
    [InlineData(-0.145f, true, true)]
    [InlineData(-0.135f, false, true)]
    [InlineData(-0.135f, true, true)]
    [InlineData(-0.128f, false, true)]
    [InlineData(-0.128f, true, true)]
    public void MeshCornerFeatureCorrespondsToTheActualSweptCandidate(float x, bool excludeFloor, bool translated)
    {
        Vector3 offset = translated ? new Vector3(45f, 1.4825f, -97.75f) : Vector3.Zero;
        using var world = new BepuPhysicsWorld();
        StaticHandle floor = world.AddStatic(new BoxShape(new Vector3(8f, 0.1f, 8f)),
            Pose.At(offset + new Vector3(0f, -0.1f, 0f)));
        (StaticHandle installed, _, _) = AddFaceProbeShape(world, "mesh", offset);
        using IPhysicsWorldQueryView view = world.CreateQueryViewExcludingStatics(excludeFloor ? [floor] : []);
        var features = Assert.IsAssignableFrom<IPhysicsCapsuleFeatures>(view);
        using IPhysicsQueryLease lease = Assert.IsAssignableFrom<IPhysicsQueryLeaseSource>(view).AcquireQueryReadLease();
        lease.AssertCurrent();
        Assert.Same(world, lease.SourceWorld);
        Assert.Equal(Vector3.Zero, lease.Origin);

        // Ordinary controls precede the feature classification. Neither control lies at the candidate edge.
        Vector3 floorPoint = offset + new Vector3(-2f, 0f, Row);
        Assert.True(world.Raycast(floorPoint + Vector3.UnitY, -Vector3.UnitY, 2f, out RayHit floorHit,
            QueryFilter.StaticsOnly));
        Assert.Equal(floor, floorHit.Body);
        Assert.Equal(!excludeFloor, view.Raycast(floorPoint + Vector3.UnitY, -Vector3.UnitY, 2f,
            out RayHit selectedFloor, QueryFilter.StaticsOnly));
        if (!excludeFloor) Assert.Equal(floor, selectedFloor.Body);
        Vector3 interior = offset + new Vector3(0.5f, RestingLip, -1f);
        Assert.True(view.Raycast(interior + Vector3.UnitY, -Vector3.UnitY, 2f, out RayHit topHit,
            QueryFilter.StaticsOnly));
        Assert.Equal(installed, topHit.Body);
        AssertVectorWithin(topHit.Normal, V(0, 1, 0), PositionCeiling, "ordinary top control");
        Triangle[] source =
        [
            new(new(0f, RestingLip, -2f), new(2f, RestingLip, -2f), new(0f, RestingLip, 2f)),
            new(new(2f, RestingLip, -2f), new(2f, RestingLip, 2f), new(0f, RestingLip, 2f)),
        ];
        AssertInstalledMesh(world, Pose.At(offset), source);

        CapsuleShape capsule = CharacterMovement.CapsuleFor(Tuning);
        Assert.Equal(0.3f, capsule.Radius);
        Assert.Equal(0.9f, capsule.Length);
        Vector3 centre = offset + new Vector3(x, Tuning.CapsuleHalfHeight, Row);
        float probeStart = centre.Y + 2f * Tuning.CapsuleHalfHeight;
        float maxProbe = probeStart - (offset.Y + Tuning.CapsuleHalfHeight) + 0.01f;
        Assert.True(view.SweepCapsule(capsule, Pose.At(new Vector3(centre.X, probeStart, centre.Z)),
            -Vector3.UnitY, maxProbe, out SweepHit sweep, QueryFilter.StaticsOnly));
        Assert.Equal<StaticHandle?>(installed, sweep.Body);
        Assert.True(float.IsFinite(sweep.Distance) && sweep.Distance > 0f && sweep.Distance <= maxProbe);
        StaticHandle target = sweep.Body!.Value;
        Pose candidate = Pose.At(new Vector3(centre.X, probeStart - sweep.Distance, centre.Z));

        // Identity installation means this affine sum is exact real geometry, without binary32 summation.
        R edgeX = R.From(offset.X), edgeY = R.From(offset.Y) + R.From(RestingLip);
        R edgeMinZ = R.From(offset.Z) - new R(2, 1), edgeMaxZ = R.From(offset.Z) + new R(2, 1);
        ExactVector axis = AxisLower(capsule, candidate);
        ExactVector edge = new(edgeX, edgeY, axis.Z);
        Assert.True(axis.X < edgeX && axis.Y > edgeY && axis.Z > edgeMinZ && axis.Z < edgeMaxZ);
        // Every top point has x >= edgeX and y = edgeY. Increasing the axis parameter increases its
        // already positive Y gap. The unique global minimum is therefore this lower endpoint/finite edge pair.
        ExactVector delta = axis - edge;
        R squared = delta.LengthSquared();
        Assert.True(squared > R.Zero);
        Assert.True(axis.Y < edgeY + R.From(capsule.Radius), "A plane-height snap would erase the corner relation.");
        AssertNearContactSquared(squared, R.From(capsule.Radius));

        CapsuleIncidentFace[] faces = Sentinels(), original = (CapsuleIncidentFace[])faces.Clone();
        CapsuleFeatureResult result = features.QueryCapsuleFeature(lease, target, capsule, candidate,
            0.0001f, faces, QueryFilter.StaticsOnly);
        AssertComplete(view, world, features, lease, target, capsule, result, faces, original,
            CapsuleFeatureKind.OpenBoundary, axis, edge, [V(0, 1, 0)]);
        Assert.Equal(0, faces[0].FaceId);
        R faceError = R.From(faces[0].NormalError);
        Assert.True(R.From(faces[0].Normal.Y) - faceError >= R.From(0.9f));
        Assert.True(R.From(result.SeparationNormal.Y) - R.From(result.NormalError) > R.Zero);
        Assert.True(R.From(result.SeparationNormal.Y) - R.From(result.NormalError) >= R.From(MathF.Cos(Tuning.MaxSlopeRadians)));
        Assert.True(R.From(result.GeometryPoint.Z) + R.From(result.PositionErrorMetres) < edgeMaxZ);
        Assert.True(R.From(result.GeometryPoint.Z) - R.From(result.PositionErrorMetres) > edgeMinZ);
        // The backend's error is tested against the exact finite edge, rather than added to an old contact point.
        AssertVectorWithin(result.GeometryPoint, edge, R.From(result.PositionErrorMetres), "actual authored boundary");
        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => features.AssertFeatureCurrent(result, lease));
    }
}
