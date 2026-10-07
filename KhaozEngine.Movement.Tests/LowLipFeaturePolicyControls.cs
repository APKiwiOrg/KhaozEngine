using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;
using Xunit;
using static KhaozEngine.Tests.Movement.CornerFeatureOracle;
using static KhaozEngine.Tests.Movement.CornerFeatureControlScene;

namespace KhaozEngine.Tests.Movement;

public class LowLipFeaturePolicyControls
{
    // Catches blanket completion, omitted incidence, incorrect front-side topology, or curved-leaf fallback.
    [Theory]
    [InlineData("open")]
    [InlineData("convex")]
    [InlineData("concave")]
    [InlineData("competing")]
    [InlineData("underside")]
    [InlineData("wall")]
    [InlineData("dome")]
    [InlineData("unsupported-compound")]
    public void InstalledFiniteNeighborhoodHasTheIndependentlyRequiredCorrespondence(string fixture)
    {
        using var scene = new CornerFeatureControlScene(fixture);
        CapsuleFeatureResult result = scene.AssertCorrespondence(out CapsuleIncidentFace[] faces);
        if (!scene.ExpectedEligibility) return;

        int supporting = 0;
        for (int i = 0; i < result.Written; i++)
        {
            R normalY = R.From(faces[i].Normal.Y), error = R.From(faces[i].NormalError);
            Assert.True(normalY - error >= R.Zero,
                "Every actual incident face must prove a nonnegative upward derivative, including exact zero at the wall.");
            if (normalY - error >= R.From(0.9f)) supporting++;
        }
        Assert.Equal(1, supporting);
        Assert.True(R.From(result.SeparationNormal.Y) - R.From(result.NormalError) >= R.From(0.9f));
        if (fixture == "convex") Assert.Equal(2, result.Written);
    }

    [Fact]
    public void ActualConvexEdgeCallerCapacityRefusesAtomically()
    {
        // Catches publication of the first face when the actual two-face edge does not fit.
        using var scene = new CornerFeatureControlScene("convex");
        CapsuleIncidentFace[] faces = Sentinels(1), original = (CapsuleIncidentFace[])faces.Clone();
        CapsuleFeatureResult result = scene.Query(faces);
        AssertRefused(result, faces, original, CapsuleFeatureStatus.CapacityExceeded, required: 2);
        scene.Lease.AssertCurrent();
    }

    [Fact]
    public void OneHundredTwentyNinthLocalCandidateRefusesTheWholeObservedScene()
    {
        // The 129 disjoint horizontal planes each have one strict interior lower-endpoint candidate.
        // Their outer vertices and edges are farther than radius + 1 mm. No candidate is silently truncated.
        var triangles = new Triangle[129];
        for (int i = 0; i < triangles.Length; i++)
        {
            float y = i / 1048576f;
            triangles[i] = new(new(-1, y, -1), new(1, y, -1), new(0, y, 1));
        }
        var capsule = new CapsuleShape(0.25f, 0.5f);
        Pose candidate = Pose.At(new Vector3(0, 0.5f, 0));
        using var scene = new CornerFeatureControlScene(triangles, capsule, candidate,
            new RayControl(new Vector3(0, 128f / 1048576, 0), Vector3.UnitY));
        R band = R.From(0.001f), radius = R.From(capsule.Radius);
        for (int i = 0; i < triangles.Length; i++)
        {
            R distance = new R(1, 4) - new R(i, 1048576);
            Assert.True(distance > R.Zero && distance <= radius + band);
        }
        // The sloping outer edges have projected horizontal distance sqrt(1/5), the bottom edge distance 1.
        Assert.True(new R(1, 5) > (radius + band) * (radius + band));
        CapsuleIncidentFace[] faces = Sentinels(), original = (CapsuleIncidentFace[])faces.Clone();
        CapsuleFeatureResult result = scene.Features.QueryCapsuleFeature(scene.Lease, scene.Target, capsule,
            candidate, 0.001f, faces, QueryFilter.StaticsOnly);
        AssertRefused(result, faces, original, CapsuleFeatureStatus.CapacityExceeded);
        scene.Lease.AssertCurrent();
    }

    [Fact]
    public void TwoHundredFiftySeventhActualIncidentFaceRefusesBeforeAnyOutput()
    {
        // The positive XZ determinants below prove Bepu's upward front convention for every fan triangle.
        // The fan is a single exact flat disk. Adding the 257th incident triangle must refuse its source capture.
        var ring = new List<Vector3>(257);
        for (int i = 0; i < 64; i++)
        {
            ring.Add(new Vector3(-1 + i / 32f, 0, -1));
            if (i == 0) ring.Add(new Vector3(-63f / 64, 0, -1));
        }
        for (int i = 0; i < 64; i++) ring.Add(new Vector3(1, 0, -1 + i / 32f));
        for (int i = 0; i < 64; i++) ring.Add(new Vector3(1 - i / 32f, 0, 1));
        for (int i = 0; i < 64; i++) ring.Add(new Vector3(-1, 0, 1 - i / 32f));
        Assert.Equal(257, ring.Count);
        var triangles = new Triangle[257];
        for (int i = 0; i < triangles.Length; i++)
        {
            Vector3 a = ring[i], b = ring[(i + 1) % ring.Count];
            Assert.True(R.From(a.X) * R.From(b.Z) - R.From(a.Z) * R.From(b.X) > R.Zero);
            triangles[i] = new(Vector3.Zero, a, b);
        }
        using var scene = new CornerFeatureControlScene(triangles, new CapsuleShape(0.25f, 0.5f),
            Pose.At(new Vector3(0, 0.5f, 0)), new RayControl(new Vector3(0.25f, 0, -0.75f), Vector3.UnitY));
        CapsuleIncidentFace[] faces = Sentinels(), original = (CapsuleIncidentFace[])faces.Clone();
        AssertRefused(scene.Query(faces), faces, original, CapsuleFeatureStatus.CapacityExceeded);
        scene.Lease.AssertCurrent();
    }
}
