using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
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
    public void Commanded_certification_does_not_replace_the_uncommanded_whole_skin_gate()
    {
        var data = CoverageFixture.CurvedPanel(seams: false, level: 1, crossingSegment: true);
        var topology = CoverageRefinement.Build(data.Source, data.Map);
        var constructor = new CoverageExactConstructor(topology, [Matrix4x4.Identity, Matrix4x4.Identity]);
        var raw = new ModelVertex[topology.VertexCount];
        Assert.True(constructor.TryConstruct(CoverageFixture.Pose, Matrix4x4.Identity, [], raw,
            new float[topology.PositionGroupCount], new int[topology.PositionGroupCount], out _));
        Assert.True(CoverageCertification.ValidateGeometry(topology, raw, raw, Matrix4x4.Identity, [], new(topology), out _));
        bool[] uncommanded = topology.Triangles.Select(t => t.RegionIndex < 0).ToArray();
        Assert.Single(uncommanded, value => value);
        var measured = Box().Measure(topology.Indices, raw, Matrix4x4.Identity, Matrix4x4.Identity, uncommanded);
        Assert.True(measured.ObservedMm > 0);
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

    internal static CoverageWholeSkinDepth Box()
    {
        Vector3[] points = [new(-1, -1, -1), new(1, -1, -1), new(1, 1, -1), new(-1, 1, -1),
            new(-1, -1, 1), new(1, -1, 1), new(1, 1, 1), new(-1, 1, 1)];
        int[] indices = [0, 1, 2, 0, 2, 3, 4, 6, 5, 4, 7, 6, 0, 5, 1, 0, 4, 5, 1, 6, 2, 1, 5, 6, 2, 7, 3, 2, 6, 7, 3, 4, 0, 3, 7, 4];
        var source = new CoverageSourceTopology("synthetic-depth-solid", CoverageBuildFixture.Hash, points,
            points.Select((_, i) => new CoverageSourceVertex(0, 0, i, -1, 0, i)),
            Enumerable.Range(0, indices.Length / 3).Select(i => new CoverageSourceTriangle(0, 0, i, -1, 0, indices[i * 3], indices[i * 3 + 1], indices[i * 3 + 2])));
        return new(CoverageSupportSurface.Compile(source, [new(0, 0, 0, 2)], Matrix4x4.Identity));
    }
}
