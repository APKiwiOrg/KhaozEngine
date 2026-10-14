using System;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Fact]
    public void PlanarProposalCertificatePreservesFiniteContactsAndExhaustiveHeights()
    {
        Vector3[][] faces =
        [
            [new(-1, 0, -1), new(1, 0, -1), new(1, 0, 1), new(-1, 0, 1)],
            [Vector3.Zero, new(1, 0, 0), new(1, 0, 1)],
            [new(-0.25f, 0, 0), new(0.25f, 0, 0), new(0, 0, 1)],
            [new(-0.25f, 0, -0.25f), new(0.25f, 0, -0.25f), new(0.25f, 0, 0.25f)],
        ];
        var kernel = new SupportSegmentPolygon();
        foreach (Vector3[] face in faces)
        {
            SupportPolygon polygon = PlanarSource(face);
            var proposal = new SupportFootprintProposal(new CapsuleShape(0.25f, 0.01f),
                new Pose(new(0, 1, 0), Quaternion.Identity), 2);
            FeaturePoint start = FeaturePoint.Exact(new(0, 0.249f, 0));
            FeaturePoint direction = FeaturePoint.Exact(new(0, 0.01f, 0));
            Assert.True(SupportPlanarProposal.Contact(polygon, start, direction, 0.25f, 0.0001f));
            Assert.True(ExhaustiveContact(kernel, polygon, start, direction, 0.25f));
            Assert.Equal(GeometrySign.Positive, SupportFootprintProposalGeometry.LowestExhaustive(polygon, proposal,
                out var exhaustive, sourceHeight: true));
            Assert.True(SupportPlanarProposal.Lowest(polygon, proposal, true, out var fast));
            Assert.Equal(GeometryInterval.Exact(0), fast);
            Assert.Equal(exhaustive, fast);
        }
    }

    [Fact]
    public void PlanarProposalCertificateFallsBackAtUnprovedMembershipAndSourceUncertainty()
    {
        SupportPolygon polygon = PlanarSource([new(-1, 0, -1), new(1, 0, -1),
            new(1, 0, 1), new(-1, 0, 1)]);
        foreach (Vector3 start in new[] { new Vector3(1.01f, 0.249f, 0), new(0, -0.26f, 0),
            new(0, 0.251f, 0), new(300, 0.249f, 0) })
            Assert.False(SupportPlanarProposal.Contact(polygon, FeaturePoint.Exact(start),
                FeaturePoint.Exact(new(0, 0.01f, 0)), 0.25f, 0.0001f));
        var proposal = new SupportFootprintProposal(new CapsuleShape(0.25f, 0.01f),
            new Pose(new(1.01f, 1, 0), Quaternion.Identity), 2);
        Assert.False(SupportPlanarProposal.Lowest(polygon, proposal, true, out _));
        proposal = proposal with { Start = new Pose(new(0, 1, 0), Quaternion.Identity) };
        Assert.False(SupportPlanarProposal.Lowest(polygon, proposal, false, out _));
        Assert.True(InstalledPoseOperator.TryCreate(new BepuPhysics.RigidPose(Vector3.Zero,
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.37f)), out var map));
        polygon.BeginSource(map);
        Assert.False(SupportPlanarProposal.Lowest(polygon, proposal, true, out _));
    }

    static bool ExhaustiveContact(SupportSegmentPolygon kernel, SupportPolygon polygon,
        FeaturePoint start, FeaturePoint direction, double radius)
    {
        SupportDistance distance = kernel.MeasureFootprintContact(start, direction, polygon, 0.0001f);
        Assert.True(distance.IsResolved);
        return GeometryInterval.Enclose(distance.Lower, distance.Upper).Subtract(GeometryInterval.Exact(radius)).Lower
            <= 0.0001f && distance.Front;
    }

    [Fact]
    public void PlanarFanAndClosedContactBoundaryAgreeWithEveryExhaustiveBranch()
    {
        var kernel = new SupportSegmentPolygon();
        var proposal = Request(radius: 0.25f).FootProposal;
        for (int i = 0; i < 96; i++)
        {
            float a = 2 * MathF.PI * i / 96, b = 2 * MathF.PI * (i + 1) / 96;
            SupportPolygon polygon = PlanarSource([Vector3.Zero, new(MathF.Cos(a), 0, MathF.Sin(a)),
                new(MathF.Cos(b), 0, MathF.Sin(b))]);
            Assert.True(SupportPlanarProposal.Lowest(polygon, proposal, true, out var fast));
            Assert.Equal(GeometrySign.Positive, SupportFootprintProposalGeometry.LowestExhaustive(polygon,
                proposal, out var exhaustive, sourceHeight: true));
            Assert.Equal(exhaustive, fast);
            foreach (float y in new[] { 0f, 0.249f, 0.25f, 0.2501f, MathF.BitIncrement(0.2501f), -0.26f })
            {
                FeaturePoint start = FeaturePoint.Exact(new(0, y, 0));
                FeaturePoint direction = FeaturePoint.Exact(new(0, 0.01f, 0));
                bool certified = SupportPlanarProposal.Contact(polygon, start, direction, 0.25f, 0.0001f);
                bool old = ExhaustiveContact(kernel, polygon, start, direction, 0.25f);
                if (certified) Assert.True(old);
            }
        }
    }

    static SupportPolygon PlanarSource(Vector3[] vertices)
    {
        SupportPolygon polygon = Polygon(vertices, out _);
        Assert.True(InstalledPoseOperator.TryCreate(new BepuPhysics.RigidPose(Vector3.Zero, Quaternion.Identity), out var map));
        polygon.BeginSource(map);
        // Polygon normalizes the winding to upward before storing it.
        for (int i = 0; i < polygon.Vertices.Length; i++)
            polygon.SetSource(i, new((float)polygon.Vertices[i].X.Value,
                (float)polygon.Vertices[i].Y.Value, (float)polygon.Vertices[i].Z.Value));
        return polygon;
    }
}
