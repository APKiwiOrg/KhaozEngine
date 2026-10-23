using System;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Physics;

public sealed class ConvexFeaturePairCoverageTests(ITestOutputHelper output)
{
    static readonly ConvexQueryShape Shape = new(ConvexQueryKind.ConeCylinder, 0.25f, 0.25f, 0.5f, 0.75f);
    static ConvexPlanarFixture Box(Vector3 half, Vector3 center) => new(half, Pose.At(center));

    static ConvexPlanarFixture Prism(Vector3 center, Vector3 along, Vector3 depth)
    {
        var points = new Vector3[8];
        for (int i = 0; i < 8; i++) points[i] = center + ((i & 1) == 0 ? -along : along) +
            new Vector3(0, 0, (i & 2) == 0 ? -1 : 1) + ((i & 4) == 0 ? Vector3.Zero : depth);
        return new(default, Pose.Identity, hull: true, vertices: points);
    }

    static ConvexPlanarFixture CylinderEdge()
    {
        Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new(1, 2, 3)), 0.7f);
        Vector3 v = Vector3.Transform(Vector3.UnitX, rotation);
        Vector3 n = Vector3.Normalize(new Vector3(-v.Z, 0, v.X));
        return new(new(0.25f, 0.00390625f, 0.00390625f), new(n * 0.5f + new Vector3(0, 0.625f, 0), rotation));
    }

    [Fact]
    public void AllEighteenClosedFeaturePairsHaveIndependentDistanceControls()
    {
        var rows = new (ConvexShellFeature Query, ConvexTargetFeature Target, Func<ConvexPlanarFixture> Fixture)[]
        {
            (ConvexShellFeature.Apex, ConvexTargetFeature.Face, () => Box(new(2, 0.03125f, 2), new(0, 0.125f, 0))),
            (ConvexShellFeature.Apex, ConvexTargetFeature.Edge, () => Box(new(0.125f, 0.03125f, 1), new(0.2f, 0.125f, 0))),
            (ConvexShellFeature.Apex, ConvexTargetFeature.Vertex, () => Box(new(0.00390625f), new(0.01f, 0.125f, 0.01f))),
            (ConvexShellFeature.LowerCone, ConvexTargetFeature.Face, () => Prism(new(0.25f, 0.25f, 0), new(0.125f, 0.125f, 0), new(0.125f, -0.125f, 0))),
            (ConvexShellFeature.LowerCone, ConvexTargetFeature.Edge, () => Box(new(0.00390625f), new(0.1875f, 0.3125f, 0))),
            (ConvexShellFeature.LowerCone, ConvexTargetFeature.Vertex, () => Box(new(0.00390625f), new(0.1875f, 0.3125f, 0.0625f))),
            (ConvexShellFeature.Shoulder, ConvexTargetFeature.Face, () => Prism(new(0.5f, 0.375f, 0), new(0.125f, 0.25f, 0), new(0.125f, -0.0625f, 0))),
            (ConvexShellFeature.Shoulder, ConvexTargetFeature.Edge, () => Box(new(0.03125f), new(0.5f, 0.4375f, 0))),
            (ConvexShellFeature.Shoulder, ConvexTargetFeature.Vertex, () => Box(new(0.00390625f), new(0.5f, 0.4375f, 0.25f))),
            (ConvexShellFeature.Cylinder, ConvexTargetFeature.Face, () => Box(new(0.03125f, 2, 2), new(0.5f, 0.625f, 0))),
            (ConvexShellFeature.Cylinder, ConvexTargetFeature.Edge, CylinderEdge),
            (ConvexShellFeature.Cylinder, ConvexTargetFeature.Vertex, () => Box(new(0.00390625f), new(0.5f, 0.625f, 0.25f))),
            (ConvexShellFeature.HeadDisc, ConvexTargetFeature.Face, () => Box(new(2, 0.03125f, 2), new(0, 1, 0))),
            (ConvexShellFeature.HeadDisc, ConvexTargetFeature.Edge, () => Box(new(0.015625f, 0.03125f, 1), new(0.21875f, 1, 0))),
            (ConvexShellFeature.HeadDisc, ConvexTargetFeature.Vertex, () => Box(new(0.00390625f), new(0.0625f, 1, 0.0625f))),
            (ConvexShellFeature.Rim, ConvexTargetFeature.Face, () => Prism(new(0.5f, 1, 0), new(0.25f, -0.25f, 0), new(0.125f, 0.125f, 0))),
            (ConvexShellFeature.Rim, ConvexTargetFeature.Edge, () => Box(new(0.03125f), new(0.5f, 1, 0))),
            (ConvexShellFeature.Rim, ConvexTargetFeature.Vertex, () => Box(new(0.00390625f), new(0.5f, 1, 0.25f)))
        };
        foreach (var row in rows)
        {
            using var fixture = row.Fixture();
            var request = new ConvexQueryRequest(Shape, Pose.Identity, Vector3.Zero, ConvexQueryOperation.Distance);
            var work = new ConvexQueryWork();
            Assert.True(ConvexFeatureKernel.TryEvaluate(request, fixture.Leaf, ref work, new(), out var evidence), $"{row.Query}/{row.Target}");
            Assert.Equal(row.Query, evidence.QueryFeature);
            Assert.Equal(row.Target, evidence.TargetFeature);
            var oracle = ShortBodyShellOracle.Entry(Shape, Pose.Identity, fixture.Oracle, Vector3.Zero);
            Assert.True(oracle.IsComplete, $"{row.Query}/{row.Target}");
            Assert.True(evidence.LowerDistance <= oracle.Lower && evidence.UpperDistance >= oracle.Upper,
                $"{row.Query}/{row.Target}: [{evidence.LowerDistance:R},{evidence.UpperDistance:R}] oracle [{oracle.Lower:R},{oracle.Upper:R}]");
            output.WriteLine($"{row.Query}/{row.Target}: cells={work.Cells}, features={work.Features}");
        }
    }
}
