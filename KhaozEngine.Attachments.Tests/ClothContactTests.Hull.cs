using System;
using System.Numerics;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

public sealed partial class ClothContactTests
{
    [Theory]
    [InlineData(1f, false)]
    [InlineData(1.015f, false)]
    [InlineData(1f, true)]
    public void A_rotating_hull_corner_clears_the_independently_tessellated_hardware(float closingScale, bool pinned)
    {
        const float angle = MathF.PI / 6, plane = .0254f;
        Vector3 center = .024f * Vector3.UnitX;
        Vector3[] rest = [new(plane, -.036f, -.022f), new(plane, .036f, -.022f), new(plane, 0, .022f)];
        ClothRig rig = Triangle(rest, pinned ? [0, 1] : []);
        var simulation = new ClothSimulation(rig, ClothWorkLimits.LayeredContact);
        simulation.Reset(rest);
        ClothSupportHull hull = Box(center, new Vector3(.002f, .003f, .003f));
        var support = new ClothHullSupport(Identity, hull, Pose(0), Pose(1));
        var visibleHalfExtent = new Vector3(.0019f, .0029f, .0029f);
        Vector3[] visibleSamples = Samples(visibleHalfExtent);

        Assert.True(MaxVisibleX(0) + Material.HalfThickness < plane);
        Assert.True(MaxVisibleX(1) + Material.HalfThickness < plane);
        Assert.True(MaxVisibleX(.5f) > plane);
        ClothStepResult step = simulation.Advance(new ClothSubstep(pinned ? rest.AsSpan(0, 2) : [],
            [], [], Seconds, Vector3.Zero, [support]));

        Assert.Equal(ClothStepStatus.Accepted, step.Status);
        Assert.InRange(step.SweepRefinements, 1, 8 * step.ContactCandidates);
        for (int sample = 0; sample <= 32; sample++)
        {
            float time = sample / 32f;
            var pose = Pose(time);
            Assert.True(Matrix4x4.Invert(pose, out Matrix4x4 inverse));
            Vector3 a = Vector3.Lerp(rest[0], simulation.Positions[0], time);
            Vector3 b = Vector3.Lerp(rest[1], simulation.Positions[1], time);
            Vector3 c = Vector3.Lerp(rest[2], simulation.Positions[2], time);
            Assert.False(ClipsBox(Local(a), Local(b), Local(c), visibleHalfExtent));
            foreach (Vector3 vertex in visibleSamples)
                Assert.True(DistanceToTriangle(Vector3.Transform(center + vertex, pose), a, b, c) >=
                    Material.HalfThickness - .00025f);

            Vector3 Local(Vector3 point) => Vector3.Transform(point, inverse) - center;
        }

        Matrix4x4 Pose(float t) => Matrix4x4.CreateScale(float.Lerp(1, closingScale, t)) *
            Matrix4x4.CreateRotationZ(float.Lerp(-angle, angle, t));

        float MaxVisibleX(float t)
        {
            // The appearance witness is a separate scan of the visible front plate.
            float maximum = float.NegativeInfinity;
            for (int row = 0; row <= 6; row++)
            for (int column = 0; column <= 6; column++)
            {
                var visible = new Vector3(.0259f, float.Lerp(-.0029f, .0029f, row / 6f),
                    float.Lerp(-.0029f, .0029f, column / 6f));
                maximum = MathF.Max(maximum, Vector3.Transform(visible, Pose(t)).X);
            }
            return maximum;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Fine_hull_selection_replaces_only_its_group_of_coarse_capsules(bool sameGroupDifferentPart)
    {
        Vector3[] rest = [Vector3.Zero, .06f * Vector3.UnitY, .06f * Vector3.UnitZ];
        var simulation = new ClothSimulation(Triangle(rest), ClothWorkLimits.LayeredContact);
        simulation.Reset(rest);
        ClothSupport grouped = Ball(new Vector3(-.002f, 0, 0), Identity);
        ClothSupportIdentity independentId = sameGroupDifferentPart
            ? Identity with { PartId = Identity.PartId + 1 }
            : Identity with { FeatureGroup = 2 };
        ClothSupport independent = Ball(new Vector3(-.002f, .06f, 0), independentId);
        ClothSupport[] capsules = [grouped, independent];
        var hull = new ClothHullSupport(Identity, Box(-.05f * Vector3.UnitX, new Vector3(.01f)),
            Matrix4x4.Identity, Matrix4x4.Identity);

        ClothStepResult step = simulation.Advance(new ClothSubstep([], capsules, capsules, Seconds, Vector3.Zero, [hull]));

        Assert.Equal(ClothStepStatus.Accepted, step.Status);
        Assert.InRange(MathF.Abs(simulation.Positions[0].X), 0, .00025f);
        Assert.True(simulation.Positions[1].X >= Material.HalfThickness + .001f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_nonuniform_hull_uses_only_a_checked_fitted_fallback(bool openingTransform)
    {
        Vector3[] rest = [Vector3.Zero, .06f * Vector3.UnitY, .06f * Vector3.UnitZ];
        Vector3[] fitted = Array.ConvertAll(rest, position => position + .008f * Vector3.UnitX);
        var simulation = new ClothSimulation(Triangle(rest), ClothWorkLimits.LayeredContact);
        simulation.Reset(rest);
        ClothSupport[] capsules = [Ball(new Vector3(-.002f, 0, 0), Identity)];
        Matrix4x4 unsupported = Matrix4x4.CreateScale(1, 2, 1);
        var hull = new ClothHullSupport(Identity, Box(-.005f * Vector3.UnitX, new Vector3(.004f)),
            openingTransform ? unsupported : Matrix4x4.Identity, openingTransform ? Matrix4x4.Identity : unsupported);
        var input = new ClothSubstep([], capsules, capsules, Seconds, Vector3.Zero, [hull]);

        ClothStepResult invalidFit = simulation.Advance(input, rest);

        Assert.Equal(ClothStepStatus.UnsupportedHullTransform, invalidFit.Status);
        Assert.Equal(Identity.PartId, invalidFit.PartId);
        Assert.Equal(ClothStepStatus.UnresolvedContact, invalidFit.FallbackStatus);
        Assert.Equal(Identity.PartId, invalidFit.FallbackPartId);
        Assert.Equal(rest, simulation.Positions.ToArray());

        ClothStepResult recovered = simulation.Advance(input, fitted);

        Assert.Equal(ClothStepStatus.UnsupportedHullTransform, recovered.Status);
        Assert.Equal(Identity.PartId, recovered.PartId);
        Assert.Equal(ClothStepStatus.Accepted, recovered.FallbackStatus);
        Assert.Equal(fitted, simulation.Positions.ToArray());
    }

    [Fact]
    public void Hull_authoring_reports_the_offending_vertex_face_and_edge()
    {
        ClothSupportHull good = Box(Vector3.Zero, Vector3.One);
        Assert.Equal(8, good.Vertices.Length);
        Assert.Equal(12, good.Faces.Length);

        Vector3[] badVertex = good.Vertices.ToArray();
        badVertex[2] = new Vector3(float.NaN, 1, 1);
        Reject(badVertex, good.Faces, ClothSupportHullRefusal.InvalidVertex, null, 2);

        var faceIndex = good.Faces.ToArray();
        faceIndex[0] = new ClothTriangle(8, 2, 1);
        Reject(good.Vertices, faceIndex, ClothSupportHullRefusal.InvalidFaceVertex, 0, 8);

        var inward = good.Faces.ToArray();
        inward[0] = new ClothTriangle(0, 1, 2);
        Reject(good.Vertices, inward, ClothSupportHullRefusal.InvalidWinding, 0, null);

        Vector3[] nonconvex = good.Vertices.ToArray();
        nonconvex[0].Z = -.25f;
        Reject(nonconvex, good.Faces, ClothSupportHullRefusal.Nonconvex, 0, 3);

        var repeated = good.Faces.ToArray();
        repeated[1] = repeated[0];
        Reject(good.Vertices, repeated, ClothSupportHullRefusal.RepeatedDirectedEdge, 1, 0, 2);

        Reject(good.Vertices, good.Faces[1..], ClothSupportHullRefusal.UnpairedEdge, 7, 0, 1);

        static void Reject(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<ClothTriangle> faces,
            ClothSupportHullRefusal reason, int? face, int? vertex, int? otherVertex = null)
        {
            Vector3[] vertexData = vertices.ToArray();
            ClothTriangle[] faceData = faces.ToArray();
            ClothSupportHullException failure = Assert.Throws<ClothSupportHullException>(
                () => ClothSupportHull.Create(vertexData, faceData));
            Assert.Equal(reason, failure.Reason);
            Assert.Equal(face, failure.FaceIndex);
            Assert.Equal(vertex, failure.VertexIndex);
            Assert.Equal(otherVertex, failure.OtherVertexIndex);
        }
    }

    private static readonly ClothSupportIdentity Identity = new(7, uint.MaxValue - 1, 1, AttachmentLayer.Armour);

    private static ClothSupport Ball(Vector3 center, ClothSupportIdentity identity) =>
        ClothSupport.Capsule(new ColliderCapsule(Vector3.Zero, Vector3.Zero, .003f),
            Quaternion.Identity, center, identity);

    private static ClothSupportHull Box(Vector3 center, Vector3 size) => ClothSupportHull.Create(
        [center + size * new Vector3(-1, -1, -1), center + size * new Vector3(1, -1, -1),
         center + size * new Vector3(1, 1, -1), center + size * new Vector3(-1, 1, -1),
         center + size * new Vector3(-1, -1, 1), center + size * new Vector3(1, -1, 1),
         center + size * new Vector3(1, 1, 1), center + size * new Vector3(-1, 1, 1)],
        [new(0, 2, 1), new(0, 3, 2), new(4, 5, 6), new(4, 6, 7), new(0, 4, 7), new(0, 7, 3),
         new(1, 2, 6), new(1, 6, 5), new(0, 1, 5), new(0, 5, 4), new(3, 7, 6), new(3, 6, 2)]);
}
