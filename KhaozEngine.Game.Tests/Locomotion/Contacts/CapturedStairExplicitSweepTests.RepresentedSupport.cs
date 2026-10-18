using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Physics;
using Xunit;
using static KhaozEngine.Tests.Physics.InstalledPoseOracle;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public sealed partial class CapturedStairExplicitSweepTests
{
    [Fact]
    public void CapturedStairZeroOriginInitialTickUsesRepresentedSupport()
    {
        using var fixture = Fixture(true, true, false);
        fixture.RecordBindings = true;
        var state = fixture.StateAtFeet(fixture.Rotate(new(0, 0.5f, -0.25f)));
        Vector3 along = fixture.Rotate(Vector3.UnitZ);
        var capture = new TickCapture(fixture);
        MovementStepResult result = capture.Step(state, new(along.X, along.Z));
        foreach (var footprint in capture.Footprints)
            output.WriteLine($"Request {footprint.Request}, status {footprint.Result.Status}, bits " +
                InstalledSupportPoseTests.Raw(footprint.Request, fixture.World, footprint.Result.Lease!) + ".");
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.Equal(MovementAvailability.Known, fixture.LastAcquisitionStatus);
        Assert.Equal(6, capture.Footprints.Count);
        SupportFootprintRequest boundary = capture.Footprints[1].Request;
        Assert.Equal(unchecked((int)0xbe199999), BitConverter.SingleToInt32Bits(boundary.Axis.X));
        Assert.Equal(0x3e19999a, BitConverter.SingleToInt32Bits(boundary.Radius));
        new CapturedStairEdgeOracle(fixture).AssertMapDisagreement(boundary);
        AssertRepresentedDiscriminantWins(fixture, boundary);
        Assert.All(capture.Footprints, f => Assert.Equal(CapsuleFeatureStatus.Complete, f.Result.Status));
        AssertAccepted(fixture, state, result, capture.Reads);
        Assert.True(Vector3.Dot(result.State.State.Position - state.State.Position, along) > 0);
        Assert.Equal(new MovementSupportKey("world", "stairs"), result.State.Selection!.Value.Support);
        Assert.Throws<ObjectDisposedException>(fixture.LastLease!.AssertCurrent);
    }

    static void AssertRepresentedDiscriminantWins(CapturedStairSweepFixture fixture, SupportFootprintRequest request)
    {
        using var lease = fixture.World.AcquireQueryReadLease();
        var sim = CertifiedGeneralSweepFixture.Sim(fixture.World);
        var scratch = new CapsuleFeatureCaptureScratch();
        var leaves = new List<CapsuleFeaturePolyhedron>();
        var pool = new SupportPolygonPool();
        var polygons = new List<SupportPolygon>();
        bool disagreement = false;
        foreach (StaticHandle handle in fixture.StairHandles)
        {
            var entry = CertifiedGeneralSweepFixture.Static(fixture.World, handle);
            sim.Statics.GetDescription(entry.Handle, out var installed);
            Assert.Equal(CapsuleFeatureStatus.Complete, SupportNeighborhoodPolyhedra.Capture(sim, entry.Shape, installed.Pose, scratch, leaves));
            foreach (CapsuleFeaturePolyhedron leaf in leaves)
            {
                polygons.Clear();
                SupportNeighborhoodPolyhedra.Polygons(handle, leaf, pool, polygons, footprint: true);
                foreach (SupportPolygon polygon in polygons)
                    for (int edge = 0; edge < polygon.SourceVertices.Length; edge++)
                    {
                        V a = V.From(polygon.SourceVertices[edge]), b = V.From(polygon.SourceVertices[(edge + 1) % polygon.SourceVertices.Length]);
                        R Discriminant(M map)
                        {
                            V e = map.Apply(b - a), p = map.Apply(a) + V.From(leaf.InstalledWorldPose.Position) -
                                new V(R.From(request.Axis.X), R.Zero, R.From(request.Axis.Y));
                            return (e.X.Square() + e.Z.Square()) * R.From(request.Radius).Square() -
                                (p.X * e.Z - p.Z * e.X).Square();
                        }
                        R real = Discriminant(Real(leaf.InstalledWorldPose.Orientation));
                        R represented = Discriminant(InstalledPoseOracle.Represented(leaf.InstalledWorldPose.Orientation));
                        if (real >= R.Zero || represented <= R.Zero) continue;
                        disagreement = true;
                        Assert.Equal(GeometrySign.Positive, polygon.SourcePredicates.EdgeDiscriminant(polygon, edge,
                            FeaturePoint.Exact(new Vector3(request.Axis.X, 0, request.Axis.Y)), FeatureNumber.Exact(request.Radius)));
                    }
            }
        }
        Assert.True(disagreement);
    }
}
