using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BepuPhysics.Collidables;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Physics;
using Xunit;
using static KhaozEngine.Tests.Physics.InstalledPoseOracle;
using Compound = BepuPhysics.Collidables.Compound;

namespace KhaozEngine.Tests.Locomotion.Contacts;

/// <summary>Exact edge/disc discriminants from installed source operands under both actual transform readings.</summary>
internal sealed class CapturedStairEdgeOracle
{
    readonly List<(V[] Real, V[] Represented)> faces = [];

    internal CapturedStairEdgeOracle(CapturedStairSweepFixture fixture)
    {
        using var lease = fixture.World.AcquireQueryReadLease();
        var sim = CertifiedGeneralSweepFixture.Sim(fixture.World);
        foreach (var handle in fixture.StairHandles.Append(fixture.Approach).Append(fixture.Landing))
        {
            var entry = CertifiedGeneralSweepFixture.Static(fixture.World, handle);
            sim.Statics.GetDescription(entry.Handle, out var installed);
            if (entry.Shape.Type == default(Compound).TypeId)
            {
                ref Compound compound = ref sim.Shapes.GetShape<Compound>(entry.Shape.Index);
                for (int child = 0; child < compound.Children.Length; child++)
                {
                    var part = compound.Children[child];
                    Compound.GetWorldPose(part.LocalPose, installed.Pose, out var pose);
                    Capture(part.ShapeIndex, pose);
                }
            }
            else Capture(entry.Shape, installed.Pose);
        }
        Assert.NotEmpty(faces);

        void Capture(TypedIndex shape, BepuPhysics.RigidPose pose)
        {
            if (shape.Type == default(Box).TypeId)
            {
                ref Box box = ref sim.Shapes.GetShape<Box>(shape.Index);
                Add([new(-box.HalfWidth, box.HalfHeight, -box.HalfLength), new(-box.HalfWidth, box.HalfHeight, box.HalfLength),
                    new(box.HalfWidth, box.HalfHeight, box.HalfLength), new(box.HalfWidth, box.HalfHeight, -box.HalfLength)], pose);
                return;
            }
            Assert.Equal(default(ConvexHull).TypeId, shape.Type);
            ref ConvexHull hull = ref sim.Shapes.GetShape<ConvexHull>(shape.Index);
            for (int face = 0; face < hull.FaceToVertexIndicesStart.Length; face++)
            {
                hull.GetVertexIndicesForFace(face, out var indices);
                var points = new Vector3[indices.Length];
                for (int i = 0; i < points.Length; i++) hull.GetPoint(indices[i], out points[i]);
                Add(points, pose);
            }
        }
    }

    void Add(Vector3[] local, BepuPhysics.RigidPose pose)
    {
        V[] Transform(M map) => local.Select(p => map.Apply(V.From(p)) + V.From(pose.Position)).ToArray();
        V[] real = Transform(Real(pose.Orientation));
        if (V.Cross(real[1] - real[0], real[2] - real[0]).Y <= R.Zero) return;
        faces.Add((real, Transform(Represented(pose.Orientation))));
    }

    internal void AssertSeparated(IEnumerable<SupportFootprintRequest> requests)
    {
        Assert.NotEmpty(requests);
        foreach (var request in requests)
            foreach (var face in faces)
                for (int edge = 0; edge < face.Real.Length; edge++)
                {
                    R real = Residual(face.Real, edge, request), represented = Residual(face.Represented, edge, request);
                    // Squared-distance separation exceeds 1e-7 m2 in both readings, well outside the 1e-9 conflict.
                    Assert.True(Abs(real) > new R(1, 10_000_000), $"Real edge/disc residual {real} at {request.Axis}.");
                    Assert.True(Abs(represented) > new R(1, 10_000_000), $"Represented edge/disc residual {represented} at {request.Axis}.");
                    Assert.Equal(real > R.Zero, represented > R.Zero);
                }
    }

    internal void AssertMapDisagreement(SupportFootprintRequest request) => Assert.Contains(faces, face =>
        Enumerable.Range(0, face.Real.Length).Any(edge =>
            Residual(face.Real, edge, request) < R.Zero && Residual(face.Represented, edge, request) > R.Zero));

    internal void AssertNearTangency(SupportFootprintRequest request) => Assert.Contains(faces, face =>
        Enumerable.Range(0, face.Real.Length).Any(edge =>
            Abs(Residual(face.Real, edge, request)) < new R(1, 10_000_000) &&
            Abs(Residual(face.Represented, edge, request)) < new R(1, 10_000_000)));

    static R Residual(V[] vertices, int edge, SupportFootprintRequest request)
    {
        V a = vertices[edge], b = vertices[(edge + 1) % vertices.Length];
        R x = b.X - a.X, z = b.Z - a.Z;
        R cross = (a.X - R.From(request.Axis.X)) * z - (a.Z - R.From(request.Axis.Y)) * x;
        R lengthSquared = x.Square() + z.Square();
        Assert.True(lengthSquared > R.Zero);
        // D / dot(E,E), with no rounded coordinates or production support predicates.
        return R.From(request.Radius).Square() - cross.Square() / lengthSquared;
    }
}
