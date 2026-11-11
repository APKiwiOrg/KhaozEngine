using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Tools.Coverage;
using Xunit;

namespace KhaozEngine.Tests.Tools.Coverage;

public sealed class CoverageWholeSkinDepthTests
{
    [Fact]
    public void Whole_skin_detects_uncommanded_triangle_interior_penetration()
    {
        var fixture = Box();
        ModelVertex[] raw = [new() { Position = new(-4, -3, 0) }, new() { Position = new(4, -3, 0) }, new() { Position = new(0, 6, 0) }];
        uint[] indices = [0, 1, 2];
        Assert.All(raw, v => Assert.True(Math.Abs(v.Position.X) > 1 || Math.Abs(v.Position.Y) > 1));
        var after = raw.ToArray();
        after[0].Normal = Vector3.UnitY;
        var classes = new CoverageSkinClasses(indices, raw, after);
        Assert.Equal(1, classes.ChangedTriangleCount);
        Assert.True(classes.AttributesOnly[0]);
        Assert.False(classes.PositionsChanged[0]);
        var depth = fixture.Measure(indices, after, Matrix4x4.Identity, Matrix4x4.Identity, classes.ChangedRecords);
        Assert.True(depth.ObservedMm > 0);
        Assert.True(depth.UpperEstimateMm >= depth.ObservedMm);
        Assert.True(depth.Queries > 1);
        var inherited = new CoverageSkinClasses(indices, raw, raw);
        Assert.Equal(1, inherited.InheritedTriangleCount);
        var rawDepth = fixture.Measure(indices, raw, Matrix4x4.Identity, Matrix4x4.Identity, inherited.Inherited);
        Assert.Equal(depth.ObservedMm, rawDepth.ObservedMm);
    }

    [Fact]
    public void Whole_skin_replay_blocks_cross_command_intrusion_by_a_changed_triangle_at_partial_transition_weight()
    {
        var fixture = new CoverageCrossCommandFixture();
        var crossing = fixture.Input(crossing: true);
        var table = CoverageCrossCommandFixture.Table(crossing);
        var sample = crossing.Domains[0].DenseRows[0].Sample;
        Assert.Equal(2, sample.Commands.Length);
        Assert.All(sample.Commands.ToArray(), c => Assert.Contains(crossing.Topology.Triangles, t => t.RegionIndex == c.RegionIndex));
        var surfaces = CoverageCrossCommandFixture.KnownPositions(crossing, table);
        // Exact certification checks equipment for full-weight commands only, and the box command is at partial weight.
        // It separates other commanded regions from full-weight equipment, so this is not a full-weight validator gap.
        Assert.True(CoverageCertification.ValidateGeometry(crossing.Topology, surfaces.Raw, surfaces.Final,
            sample.ActorModel, sample.Commands, new(crossing.Topology), out var failure), failure.ToString());
        var box = sample.Commands.ToArray().Single(c => c.Id == CoverageCrossCommandFixture.BoxCommand);
        Assert.NotEqual(box.RegionIndex, crossing.Topology.Triangles[0].RegionIndex);
        Vector3 center = default;
        for (int corner = 0; corner < 3; corner++)
        {
            int vertex = (int)crossing.Topology.Indices[corner];
            Assert.NotEqual(surfaces.Raw[vertex].Position, surfaces.Final[vertex].Position);
            var point = surfaces.Final[vertex].Position;
            Assert.True(Math.Abs(point.X) > 1 || Math.Abs(point.Y) > 1 || Math.Abs(point.Z) > 1);
            center += point / 3;
        }
        Assert.True(Math.Abs(center.X) < 1 && Math.Abs(center.Y) < 1 && Math.Abs(center.Z) < 1);
        var replay = CoverageReplay.Verify(crossing, table);
        var pose = Assert.Single(replay.Poses);
        Assert.True(pose.ExactValid);
        Assert.Equal(0, replay.SelectedFailures);
        Assert.True(pose.PositionChangedTriangleCount > 0);
        Assert.True(replay.ChangedRecordObservedMm > 0);
        Assert.False(replay.Accepted);
        var refusal = Assert.Throws<CoverageVerificationException>(replay.EnsureAccepted);
        Assert.Equal(CoverageVerificationRefusal.ChangedRecordPenetration, refusal.Reason);
        Assert.Equal(CoverageCrossCommandFixture.Domain, refusal.DomainId);
        Assert.Equal(0, refusal.DenseIndex);
        Assert.Equal("current", refusal.SampleId);
        Assert.Equal(1ul, refusal.StateKey);
        Assert.Equal(0, refusal.EntryIndex);
        Assert.Equal(CoverageCrossCommandFixture.BoxCommand, refusal.CommandId);
        Assert.Equal(CoverageCrossCommandFixture.BoxEquipment, refusal.EquipmentInstanceId);

        // The same rig, commands and table with only the moving assembly translated clear of the box.
        var control = fixture.Input(crossing: false);
        var controlSample = control.Domains[0].DenseRows[0].Sample;
        var controlSurfaces = CoverageCrossCommandFixture.KnownPositions(control, table);
        Assert.Equal(2, controlSample.Commands.Length);
        Assert.True(CoverageCertification.ValidateGeometry(control.Topology, controlSurfaces.Raw, controlSurfaces.Final,
            controlSample.ActorModel, controlSample.Commands, new(control.Topology), out failure), failure.ToString());
        var accepted = CoverageReplay.Verify(control, table);
        Assert.True(accepted.Accepted);
        Assert.Equal(0d, accepted.ChangedRecordObservedMm);
        Assert.Equal(0d, accepted.ChangedRecordUpperEstimateMm);
        Assert.Equal(0, accepted.UnresolvedChangedRecordPoses);
    }

    [Fact]
    public void Unbanded_search_keeps_positive_depth_and_never_rounds_a_small_intrusion_to_zero()
    {
        var query = Box();
        ModelVertex[] triangle = [new() { Position = new(0, 0, 0) }, new() { Position = new(1.49f, .25f, 0) }, new() { Position = new(1.49f, -.25f, 0) }];
        var depth = query.Measure([0, 1, 2], triangle, Matrix4x4.Identity, Matrix4x4.Identity, [true]);
        Assert.True(depth.ObservedMm > 400);
        ModelVertex[] shallow = [new() { Position = new(.999999f, 0, 0) }, new() { Position = new(.999999f, .1f, 0) }, new() { Position = new(.999999f, 0, .1f) }];
        Assert.True(query.Measure([0, 1, 2], shallow, Matrix4x4.Identity, Matrix4x4.Identity, [true]).ObservedMm > 0);
    }

    [Fact]
    public void Separated_triangles_resolve_zero_and_budget_exhaustion_retains_a_finite_upper_estimate()
    {
        var query = Box();
        ModelVertex[] outside = [new() { Position = new(1.5f, .9f, .9f) }, new() { Position = new(.9f, 1.5f, .9f) }, new() { Position = new(.9f, .9f, 1.5f) }];
        var separated = query.Measure([0, 1, 2], outside, Matrix4x4.Identity, Matrix4x4.Identity, [true], 1);
        Assert.Equal(new CoverageDepthResult(0, 0, 1, true), separated);
        ModelVertex[] crossing = [new() { Position = new(0, 0, 0) }, new() { Position = new(4, 1, 1) }, new() { Position = new(4, -1, 1) }];
        var unresolved = query.Measure([0, 1, 2], crossing, Matrix4x4.Identity, Matrix4x4.Identity, [true], 1);
        Assert.Equal(1, unresolved.Queries);
        Assert.False(unresolved.Resolved);
        Assert.True(double.IsFinite(unresolved.UpperEstimateMm));
        Assert.True(unresolved.UpperEstimateMm > unresolved.ObservedMm);
        var complete = query.Measure([0, 1, 2], crossing, Matrix4x4.Identity, Matrix4x4.Identity, [true]);
        Assert.True(complete.ObservedMm > 0);
        // Initialization itself is budgeted. The unqueried second triangle must retain uncertainty.
        ModelVertex[] two = [.. outside, new() { Position = new(0, 0, 0) },
            new() { Position = new(.125f, 0, 0) }, new() { Position = new(0, .125f, 0) }];
        var pending = query.Measure([0, 1, 2, 3, 4, 5], two, Matrix4x4.Identity, Matrix4x4.Identity, [true, true], 1);
        Assert.Equal(1, pending.Queries);
        Assert.Equal(0, pending.ObservedMm);
        Assert.True(pending.UpperEstimateMm > 0);
        Assert.False(pending.Resolved);
    }

    [Fact]
    public void World_transforms_apply_to_both_surfaces_and_nonfinite_geometry_is_a_typed_refusal()
    {
        var query = Box();
        ModelVertex[] triangle = [new() { Position = new(0, 0, 0) }, new() { Position = new(.25f, 0, 0) }, new() { Position = new(0, .25f, 0) }];
        var identity = query.Measure([0, 1, 2], triangle, Matrix4x4.Identity, Matrix4x4.Identity, [true]);
        var moved = Matrix4x4.CreateScale(2) * Matrix4x4.CreateTranslation(8, 16, 32);
        var world = query.Measure([0, 1, 2], triangle, moved, moved, [true]);
        // Resolution is absolute world millimetres, so scaling may select different refinement depths.
        Assert.InRange(1000 - identity.ObservedMm, 0, .1);
        Assert.InRange(2000 - world.ObservedMm, 0, .1);
        Assert.True(identity.UpperEstimateMm >= 1000);
        Assert.True(world.UpperEstimateMm >= 2000);
        triangle[0].Position.X = float.NaN;
        Assert.Equal(CoverageVerificationRefusal.DepthArithmetic, Assert.Throws<CoverageVerificationException>(() =>
            query.Measure([0, 1, 2], triangle, moved, moved, [true])).Reason);
    }

    [Fact]
    public void Open_equipment_is_refused_instead_of_reporting_an_empty_interior()
    {
        Vector3[] positions = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)];
        var source = new CoverageSourceTopology("synthetic-open-sheet", CoverageBuildFixture.Hash, positions,
            positions.Select((_, i) => new CoverageSourceVertex(0, 0, i, -1, 0, i)), [new(0, 0, 0, -1, 0, 0, 1, 2)]);
        var support = CoverageSupportSurface.Compile(source, [new(0, 0, 0, 1)], Matrix4x4.Identity);
        Assert.Equal(CoverageVerificationRefusal.OpenEquipmentSolid,
            Assert.Throws<CoverageVerificationException>(() => new CoverageWholeSkinDepth(support)).Reason);
        Assert.NotNull(Box());
    }

    internal static CoverageWholeSkinDepth Box() => new(CoverageCrossCommandFixture.BoxSupport(Vector3.One, Vector3.Zero));
}
