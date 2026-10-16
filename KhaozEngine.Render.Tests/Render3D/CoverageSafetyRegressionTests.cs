using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageSafetyRegressionTests
{
    [Theory]
    [InlineData("shared-corner")]
    [InlineData("touching")]
    [InlineData("shared-edge-crossing")]
    public void Topology_or_baseline_contact_never_waives_a_later_crossing(string kind)
    {
        var (topology, before, command) = Fan(kind);
        var after = before.ToArray();
        after[0].Position.Z = kind == "shared-edge-crossing" ? -.25f : -.3f;
        Assert.False(CoverageCertification.Validate(topology, before, after, Matrix4x4.Identity, [command],
            new CoverageScratch(topology), out var certificate, out var failure));
        Assert.Equal(CoverageFailureCode.HostIntersection, failure.Code);
        Assert.Equal(default, certificate);
        for (int i = 1; i < before.Length; i++) Assert.Equal(before[i], after[i]);
        Assert.Equal(1u, failure.CommandId);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("corner-contact")]
    [InlineData("edge-contact")]
    [InlineData("corner-final-contact")]
    public void Legitimate_manifold_and_common_topology_contact_remains_valid(string kind)
    {
        var (topology, before, command) = Fan(kind);
        var after = before.ToArray();
        after[0].Position.Z = -.3f;
        Assert.True(CoverageCertification.Validate(topology, before, after, Matrix4x4.Identity, [command],
            new CoverageScratch(topology), out var failure), failure.ToString());
    }

    [Fact]
    public void Thin_positive_projected_overlap_is_not_touching()
    {
        const float e = 1e-11f;
        Assert.True(CoverageTriangleGeometry.ProjectedInteriorsOverlap(new(0, 0, 0), new(1, 0, 0), new(0, 1, 0),
            new(0, e, 0), new(1, -1, 0), new(1, e, 0), 1));
        Assert.False(CoverageTriangleGeometry.ProjectedInteriorsOverlap(new(0, 0, 0), new(1, 0, 0), new(0, 1, 0),
            new(1, 0, 0), new(1, 1, 0), new(0, 1, 0), 1));
    }

    [Fact]
    public void Validate_reaches_the_thin_projected_overlap_gate()
    {
        const float e = 1e-11f;
        Vector3[] points = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(0, e, 0), new(1, -1, 0), new(1, e, 0)];
        var topology = Host(points, [0, 1, 2, 3, 4, 5], 2, -1);
        var before = points.Select(Vertex).ToArray();
        Assert.False(CoverageCertification.Validate(topology, before, before, Matrix4x4.Identity, [Command(topology)],
            new CoverageScratch(topology), out var failure));
        Assert.Equal(CoverageFailureCode.FoldedPatch, failure.Code);
    }

    [Fact]
    public void Actual_projection_retains_a_thin_overlap_lost_by_rounded_coordinate_sums()
    {
        const float e = 1e-11f;
        Vector3[] points = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(0, e, 0), new(1, -1, 0), new(1, e, 0)];
        var topology = Host(points, [0, 1, 2, 3, 4, 5], 2, -1);
        var before = points.Select(Vertex).ToArray();
        var shell = CoverageCommands.Gabled(0, 0, 1, 1e9f, 1e9f, outward: true);
        var support = CoverageSupportSurface.Compile(CoverageCommands.Solid("large-closed-shell", shell.Positions, shell.Indices),
            [new(0, 0, 0, 4)], Matrix4x4.Identity);
        var equipment = Matrix4x4.CreateTranslation(0, 0, 1e6f);
        var actor = Matrix4x4.CreateTranslation(1e8f, 1e8f, 0);
        var command = new CoverageCommandPose(1, 1, topology, 0, support, equipment, equipment, -2e6f, 2e6f, .4f, 20000, 0);
        Assert.False(CoverageCertification.Validate(topology, before, before, actor, [command], new CoverageScratch(topology), out var failure));
        Assert.Equal(CoverageFailureCode.FoldedPatch, failure.Code);
    }

    [Fact]
    public void Shared_large_translation_does_not_hide_stale_linear_frame()
    {
        var fixture = CoverageFixture.CurvedPanel(level: 1);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var actor = Matrix4x4.CreateTranslation(10000, 10000, 10000);
        var command = CoverageCommands.Poses(fixture, topology, CoverageCommands.Plate(fixture), actor)[0];
        var drifted = command.CoverageWorld;
        drifted.M11 += .05f;
        command = command with { CoverageWorld = drifted };
        var sentinel = Vertex(new(7, 7, 7));
        var output = Enumerable.Repeat(sentinel, topology.VertexCount).ToArray();
        Assert.False(CoverageEvaluator.TryEvaluate(topology, fixture.Posed, actor, [command], new CoverageScratch(topology), output, out var failure));
        Assert.Equal(CoverageFailureCode.StaleFrame, failure.Code);
        Assert.All(output, v => Assert.Equal(sentinel, v));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Equipment_and_coverage_mirrors_refuse(bool equipmentMirror)
    {
        var (topology, before, command) = Fan("none");
        var mirror = Matrix4x4.CreateScale(-1, 1, 1);
        command = equipmentMirror ? command with { EquipmentWorld = mirror } : command with { CoverageWorld = mirror };
        Assert.False(CoverageCertification.Validate(topology, before, before, Matrix4x4.Identity, [command], new CoverageScratch(topology), out var failure));
        Assert.Equal(CoverageFailureCode.UnsupportedTransform, failure.Code);
    }

    [Fact]
    public void Cap_uncertainty_does_not_widen_the_authored_margin()
    {
        var (topology, before, command) = Fan("none");
        // The centre is 2e-7 below the required min cap plus clearance, within the old slack waiver.
        command = command with { MinZ = -1.02f + 2e-7f };
        Assert.False(CoverageCertification.Validate(topology, before, before, Matrix4x4.Identity, [command], new CoverageScratch(topology), out var failure));
        Assert.Equal(CoverageFailureCode.CapContact, failure.Code);
    }

    [Fact]
    public void Budget_uncertainty_does_not_allow_actual_excess_recession()
    {
        var (topology, before, command) = Fan("none");
        var after = before.ToArray();
        after[0].Position.Z = -.300001f;
        command = command with { MaxWorldRecession = .3f };
        Assert.False(CoverageCertification.Validate(topology, before, after, Matrix4x4.Identity, [command], new CoverageScratch(topology), out var failure));
        Assert.Equal(CoverageFailureCode.RecessionExceeded, failure.Code);
    }

    [Fact]
    public void Exact_world_recession_budget_equality_is_accepted_without_allocation()
    {
        var (topology, before, command) = Fan("none");
        var after = before.ToArray();
        after[0].Position.Z = -.25f;
        CoverageCommandPose[] commands = [command with { MaxWorldRecession = .25f }];
        var scratch = new CoverageScratch(topology);
        Assert.True(CoverageCertification.Validate(topology, before, after, Matrix4x4.Identity, commands, scratch, out var failure), failure.ToString());
        long start = GC.GetAllocatedBytesForCurrentThread();
        bool accepted = CoverageCertification.Validate(topology, before, after, Matrix4x4.Identity, commands, scratch, out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.True(accepted);
        Assert.Equal(0, allocated);
        Assert.True(MemoryMarshal.AsBytes(before.AsSpan(1)).SequenceEqual(MemoryMarshal.AsBytes(after.AsSpan(1))));
    }

    [Fact]
    public void Adjacent_representable_world_recession_above_budget_is_refused()
    {
        var (topology, before, command) = Fan("none");
        var after = before.ToArray();
        after[0].Position.Z = MathF.BitDecrement(-.25f);
        command = command with { MaxWorldRecession = .25f };
        Assert.False(CoverageCertification.Validate(topology, before, after, Matrix4x4.Identity, [command], new CoverageScratch(topology),
            out var certificate, out var failure));
        Assert.Equal(CoverageFailureCode.RecessionExceeded, failure.Code);
        Assert.Equal(default, certificate);
        Assert.True(MemoryMarshal.AsBytes(before.AsSpan(1)).SequenceEqual(MemoryMarshal.AsBytes(after.AsSpan(1))));
    }

    [Fact]
    public void Max_norm_uncertainty_accounts_for_both_slope_coordinates()
    {
        var (topology, _, command) = Fan("none");
        var shell = CoverageCommands.Gabled(1, 1, 2, 1, 1, outward: true);
        var support = CoverageSupportSurface.Compile(CoverageCommands.Solid("slope-shell", shell.Positions, shell.Indices),
            [new(0, 0, 0, 4)], Matrix4x4.Identity);
        command = command with { Support = support, EquipmentWorld = Matrix4x4.Identity, CoverageWorld = Matrix4x4.Identity };
        var scratch = new CoverageScratch(topology);
        Assert.True(CoverageFrames.TryPrepare(topology, Matrix4x4.Identity, [command], scratch, 1, out var failure), failure.ToString());
        // With unit frames, magnitude 1 and budget .5 the float-rounding max norm exceeds 1.33e-6.
        // A Euclidean slope of 1 needs factor 1 + sqrt(2), so slack must exceed 3.2e-6.
        Assert.True(scratch.Frames[0].Slack > 3.2e-6);
    }

    [Fact]
    public void Inverted_subshell_at_a_nonmanifold_edge_has_no_inward_certificate()
    {
        var large = CoverageCommands.Gabled(0, 0, 2, 2, 2, outward: true);
        var small = CoverageCommands.Gabled(0, 0, .25f, 2, 2, outward: false);
        // The opposite shell shares its front rim with the large shell. Every edge balances, but rim edges have four owners.
        var positions = large.Positions.Concat(small.Positions).ToArray();
        var indices = large.Indices.Concat(small.Indices.Select(i => i + (uint)large.Positions.Length)).ToArray();
        var solid = CoverageCommands.Solid("nonmanifold-shell", positions, indices);
        var support = CoverageSupportSurface.Compile(solid, [new(0, 0, 0, 4)], Matrix4x4.Identity);
        Assert.False(support.InwardOfClosedSolid);
        var (topology, before, command) = Fan("none");
        command = command with { Support = support };
        Assert.False(CoverageCertification.Validate(topology, before, before, Matrix4x4.Identity, [command], new CoverageScratch(topology), out var failure));
        Assert.Equal(CoverageFailureCode.UnsupportedSupport, failure.Code);
    }

    [Fact]
    public void An_inverted_tetrahedron_glued_at_one_edge_cannot_borrow_the_other_shell_volume()
    {
        var large = CoverageCommands.Gabled(0, 0, 2, 2, 2, outward: true);
        // The tetrahedron lies beyond x=2 and below z=0, meeting the box only on its right front edge.
        Vector3[] tetrahedron = [large.Positions[2], large.Positions[5], new(3, 0, -.5f), new(3.5f, 0, -1)];
        uint[] inverted = [0, 1, 2, 1, 0, 3, 0, 2, 3, 2, 1, 3];
        var indices = large.Indices.Concat(inverted.Select(i => i + (uint)large.Positions.Length)).ToArray();
        var source = CoverageCommands.Solid("edge-glued-inverted-shell", large.Positions.Concat(tetrahedron).ToArray(), indices);
        // This selected face is on the tetrahedron's outer side. The larger component's volume used to mislabel it.
        var support = CoverageSupportSurface.Compile(source, [new(0, 0, large.Indices.Length / 3, 1)], Matrix4x4.Identity);
        Assert.False(support.InwardOfClosedSolid);
    }

    [Fact]
    public void Euclidean_clearance_above_the_whole_solid_does_not_prove_the_inward_side()
    {
        var fixture = CoverageFixture.CurvedPanel(level: 1);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var command = CoverageCommands.Poses(fixture, topology, CoverageCommands.Plate(fixture), Matrix4x4.Identity)[0];
        var above = CoverageCommands.Surface(topology, fixture.Posed);
        for (int i = 0; i < above.Length; i++) above[i].Position.Z = 1;
        Assert.False(CoverageCertification.Validate(topology, above, above, Matrix4x4.Identity, [command], new CoverageScratch(topology), out var failure));
        Assert.Equal(CoverageFailureCode.EquipmentIntersection, failure.Code);
    }

    [Fact]
    public void Runtime_boundary_bounds_refuses_an_uncertifiable_nearest_edge()
    {
        const float s = float.Epsilon;
        Vector3[] points = [new(0, 0, 0), new(s, 0, 0), new(s, s, 0), new(0, s, 0),
            new(0, 0, s), new(s, 0, s), new(s, s, s), new(0, s, s)];
        uint[] indices = [0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4,
            1, 2, 6, 1, 6, 5, 2, 3, 7, 2, 7, 6, 3, 0, 4, 3, 4, 7];
        var support = CoverageSupportSurface.Compile(CoverageCommands.Solid("tiny-closed-cube", points, indices),
            [new(0, 0, 0, 2)], Matrix4x4.Identity);
        Assert.True(support.InwardOfClosedSolid);
        var (topology, posed, command) = Fan("none");
        posed[0].Position.Z = s;
        Matrix4x4 equipment = Matrix4x4.CreateScale(1e38f);
        equipment.M21 = s; equipment.M32 = -s;
        command = command with { Support = support, EquipmentWorld = equipment, CoverageWorld = equipment };
        var scratch = new CoverageScratch(topology);
        Assert.True(CoverageFrames.TryPrepare(topology, Matrix4x4.Identity, [command], scratch, 1, out var failure), failure.ToString());
        CoverageVector q = scratch.Frames[0].HostToCoverage.Apply(posed[0].Position);
        Assert.True(q.X < 0);
        Assert.False(support.TryGetHeightBounds(CoverageAffinePoint.FromDouble(q.X, q.Y), out _));
        Assert.False(support.TryGetBoundaryBounds(CoverageAffinePoint.FromDouble(q.X, q.Y), out _));
        var sentinel = Vertex(new(7, 7, 7));
        var output = Enumerable.Repeat(sentinel, topology.VertexCount).ToArray();
        Assert.False(CoverageEvaluator.TryEvaluate(topology, posed, Matrix4x4.Identity, [command], scratch, output, out var certificate, out failure));
        Assert.Equal(CoverageFailureCode.UnsupportedSupport, failure.Code);
        Assert.Equal(default, certificate);
        Assert.All(output, v => Assert.Equal(sentinel, v));
    }

    static (CoverageRefinement Topology, ModelVertex[] Before, CoverageCommandPose Command) Fan(string kind)
    {
        var positions = new List<Vector3> { new(0, 0, 0), new(1, 1, 0), new(-1, 1, 0), new(-1, -1, 0), new(1, -1, 0) };
        var indices = new List<uint> { 0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 1 };
        switch (kind)
        {
            // The protected bridge connects the corner one-ring without changing the reviewed crossing face.
            // The fan is at z=-.15 at both interior corners, so these depths straddle it.
            case "shared-corner": positions.AddRange([new(.3f, .5f, -.05f), new(.5f, .3f, -.25f)]); indices.AddRange([1, 6, 5, 1, 4, 6]); break;
            case "corner-final-contact": positions.AddRange([new(.3f, .5f, -.05f), new(.5f, .3f, -.05f)]); indices.AddRange([1, 6, 5, 1, 4, 6]); break;
            case "touching": positions.AddRange([new(.2f, .7f, 0), new(-.4f, .7f, -.25f), new(.2f, .3f, -.25f)]); indices.AddRange([5, 6, 7]); break;
            case "shared-edge-crossing": positions.Add(new(0, .5f, -.125f)); indices.AddRange([2, 1, 5]); break;
            case "corner-contact": positions.AddRange([new(1.3f, 1.5f, -.05f), new(1.5f, 1.3f, -.05f)]); indices.AddRange([1, 6, 5, 1, 4, 6]); break;
            case "edge-contact": positions.Add(new(0, 1.5f, -.05f)); indices.AddRange([2, 1, 5]); break;
        }
        var topology = Host(positions.ToArray(), indices.ToArray(), 4, 0);
        return (topology, positions.Select(Vertex).ToArray(), Command(topology));
    }

    static CoverageRefinement Host(Vector3[] positions, uint[] indices, int regionFaces, int movable)
    {
        var skin = positions.Select(p => new SkinnedVertex { Position = p, Normal = Vector3.UnitZ, Color = Vector4.One }).ToArray();
        var source = new CoverageSourceTopology("contact-host", CoverageFixture.Hash, positions,
            Enumerable.Range(0, positions.Length).Select(i => new CoverageSourceVertex(0, 0, i, -1, 0, i)),
            Enumerable.Range(0, indices.Length / 3).Select(t => new CoverageSourceTriangle(0, 0, t, -1, 0,
                (int)indices[t * 3], (int)indices[t * 3 + 1], (int)indices[t * 3 + 2])), skin);
        var region = new HostCoverageRegion("r0", [new(new(0, 0, 0, regionFaces), CoverageTriangleRole.Collar)],
            indices.Take(regionFaces * 3).Distinct().Select(i => new CoverageSurfaceStrength((int)i, i == movable ? .5f : 0)));
        var map = new HostCoverageMap(CoverageFixture.Hash, Enumerable.Range(0, positions.Length).Select(i => new CoverageTopologyEntry(0, 0, i, i)),
            [region], [], new("synthetic", "r0"));
        return CoverageRefinement.Build(source, map);
    }

    static CoverageCommandPose Command(CoverageRefinement topology)
    {
        var shell = CoverageCommands.Gabled(0, 0, 1, 2, 2, outward: true);
        var support = CoverageSupportSurface.Compile(CoverageCommands.Solid("contact-equipment", shell.Positions, shell.Indices),
            [new(0, 0, 0, 4)], Matrix4x4.Identity);
        var placed = Matrix4x4.CreateTranslation(0, 0, 1);
        return new(1, 1, topology, 0, support, placed, placed, -2, 2, .4f, .02f, .5f);
    }

    static ModelVertex Vertex(Vector3 p) => new(p, Vector3.UnitZ, Vector4.One);
}
