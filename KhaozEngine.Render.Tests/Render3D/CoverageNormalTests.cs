using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class CoverageNormalTests
{
    [Fact]
    public void Normals_follow_deformation_without_crossing_shading_seams()
    {
        var fixture = CoverageFixture.CurvedPanel();
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var before = new ModelVertex[topology.VertexCount]; topology.Interpolate(fixture.Posed, before);
        var after = before.ToArray();
        var transform = new Matrix4x4(1.2f, .1f, .3f, 0, 0, .9f, -.2f, 0, .2f, 0, 1.4f, 0, 0, 0, 0, 1);
        foreach (ref var v in after.AsSpan()) v.Position = Vector3.Transform(v.Position, transform);
        Assert.True(CoverageGeometryTestRun.UpdateNormals(topology, before, after, new(topology), out var failure));
        Assert.True(failure.IsSuccess);
        // Each face has its own unit-normal extension, rather than an arbitrary 3D affine extension.
        int index = 0;
        var expected = CoverageGeometryOracle.ExpectedNormal(topology.Indices.ToArray(), before, after, index);
        Assert.True(Vector3.Distance(expected, after[index].Normal) < 2e-5f);
        var tangent = CoverageGeometryOracle.ExpectedTangent(topology.Indices.ToArray(), before, after, index);
        Assert.True(Vector3.Distance(tangent, new(after[index].Tangent.X, after[index].Tangent.Y, after[index].Tangent.Z)) < 2e-5f);
        Assert.All(after, v => Assert.True(Math.Abs(v.Normal.Length() - 1) < 2e-5f));
        for (int i = 0; i < after.Length; i++)
        {
            Assert.Equal(before[i].Tangent.W, after[i].Tangent.W);
            Assert.Equal(before[i].Color, after[i].Color); Assert.Equal(before[i].Uv, after[i].Uv);
        }
        var seam = before.Select((v, i) => (v, i)).GroupBy(p => p.v.Position).First(g => g.Select(p => p.v.Normal).Distinct().Count() > 1).ToArray();
        Assert.NotEqual(after[seam[0].i].Normal, after[seam[1].i].Normal);
        Assert.NotEqual(topology.Vertices[seam[0].i].ShadingGroup, topology.Vertices[seam[1].i].ShadingGroup);
    }

    [Fact]
    public void Zero_deformation_is_byte_exact_and_no_tbn_stays_zero()
    {
        var fixture = CoverageFixture.CurvedPanel(); var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var before = new ModelVertex[topology.VertexCount]; topology.Interpolate(fixture.Posed, before);
        var after = before.ToArray();
        Assert.True(CoverageGeometryTestRun.UpdateNormals(topology, before, after, new(topology), out _));
        Assert.Equal(MemoryMarshal.AsBytes(before.AsSpan()).ToArray(), MemoryMarshal.AsBytes(after.AsSpan()).ToArray());
        for (int i = 0; i < before.Length; i++) { before[i].Tangent = Vector4.Zero; after[i].Tangent = Vector4.Zero; after[i].Position.Z -= after[i].Position.X * .1f; }
        Assert.True(CoverageGeometryTestRun.UpdateNormals(topology, before, after, new(topology), out _));
        Assert.All(after, v => Assert.Equal(Vector4.Zero, v.Tangent));
    }

    [Fact]
    public void Degenerate_gradient_refuses_without_partial_attributes()
    {
        var fixture = CoverageFixture.CurvedPanel(); var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var before = new ModelVertex[topology.VertexCount]; topology.Interpolate(fixture.Posed, before);
        var after = before.ToArray();
        foreach (ref var v in after.AsSpan()) v.Position = Vector3.Zero;
        var expected = MemoryMarshal.AsBytes(after.AsSpan()).ToArray();
        Assert.False(CoverageGeometryTestRun.UpdateNormals(topology, before, after, new(topology), out var failure));
        Assert.Equal(CoverageFailureCode.FoldedPatch, failure.Code);
        Assert.Equal(expected, MemoryMarshal.AsBytes(after.AsSpan()).ToArray());
    }

    [Fact]
    public void Untouched_degenerate_face_outside_the_region_keeps_normal_update()
    {
        var fixture = CoverageFixture.CurvedPanel(degenerateFace: true); var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var before = new ModelVertex[topology.VertexCount]; topology.Interpolate(fixture.Posed, before);
        var after = before.ToArray();
        for (int i = 0; i < after.Length; i++) after[i].Position.Z -= topology.Vertices[i].Strength * .05f;
        Assert.True(CoverageGeometryTestRun.UpdateNormals(topology, before, after, new(topology), out var failure));
        Assert.True(failure.IsSuccess);
        for (int i = 0; i < after.Length; i++)
            if (topology.Vertices[i].Strength == 0)
                Assert.Equal(MemoryMarshal.AsBytes(before.AsSpan(i, 1)).ToArray(), MemoryMarshal.AsBytes(after.AsSpan(i, 1)).ToArray());
    }

    [Fact]
    public void Unchanged_host_with_degenerate_face_is_byte_exact()
    {
        var fixture = CoverageFixture.CurvedPanel(degenerateFace: true); var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var before = new ModelVertex[topology.VertexCount]; topology.Interpolate(fixture.Posed, before);
        var after = before.ToArray();
        Assert.True(CoverageGeometryTestRun.UpdateNormals(topology, before, after, new(topology), out _));
        Assert.Equal(MemoryMarshal.AsBytes(before.AsSpan()).ToArray(), MemoryMarshal.AsBytes(after.AsSpan()).ToArray());
    }

    [Fact]
    public void Displaced_degenerate_face_refuses_without_partial_attributes()
    {
        var fixture = CoverageFixture.CurvedPanel(degenerateFace: true); var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var before = new ModelVertex[topology.VertexCount]; topology.Interpolate(fixture.Posed, before);
        var after = before.ToArray();
        for (int i = 0; i < after.Length; i++) after[i].Position.Z -= topology.Vertices[i].Strength * .05f;
        int moved = Enumerable.Range(0, topology.VertexCount).Single(i => topology.Vertices[i].SourceVertex == fixture.Source.Vertices.Count - 1);
        after[moved].Position.X = .25f; // Still exactly collinear.
        var expected = MemoryMarshal.AsBytes(after.AsSpan()).ToArray();
        Assert.False(CoverageGeometryTestRun.UpdateNormals(topology, before, after, new(topology), out var failure));
        Assert.Equal(CoverageFailureCode.FoldedPatch, failure.Code);
        Assert.Equal(expected, MemoryMarshal.AsBytes(after.AsSpan()).ToArray());
    }

    [Fact]
    public void Nonfinite_update_input_is_an_unsupported_transform()
    {
        var fixture = CoverageFixture.CurvedPanel(); var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var before = new ModelVertex[topology.VertexCount]; topology.Interpolate(fixture.Posed, before);
        var after = before.ToArray(); after[^1].Position.Y = float.PositiveInfinity;
        var expected = MemoryMarshal.AsBytes(after.AsSpan()).ToArray();
        Assert.False(CoverageGeometryTestRun.UpdateNormals(topology, before, after, new(topology), out var failure));
        Assert.Equal(CoverageFailureCode.UnsupportedTransform, failure.Code);
        Assert.Equal(expected, MemoryMarshal.AsBytes(after.AsSpan()).ToArray());
    }

    [Fact]
    public void Foreign_scratch_and_inexact_spans_are_caller_errors()
    {
        var fixture = CoverageFixture.CurvedPanel(); var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var twin = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var before = new ModelVertex[topology.VertexCount]; topology.Interpolate(fixture.Posed, before);
        var after = before.ToArray();
        foreach (ref var v in after.AsSpan()) v.Position.Z -= .01f;
        var expected = MemoryMarshal.AsBytes(after.AsSpan()).ToArray();
        // Equal sized storage compiled for another topology is still the wrong workspace.
        Assert.Equal(twin.VertexCount, topology.VertexCount);
        Assert.Throws<ArgumentException>(() => CoverageGeometryTestRun.UpdateNormals(topology, before, after, new(twin), out _));
        Assert.Equal(expected, MemoryMarshal.AsBytes(after.AsSpan()).ToArray());
        // Capacity-owned buffers pass their exact active prefix.
        var larger = new ModelVertex[topology.VertexCount + 1]; after.CopyTo(larger, 0);
        Assert.Throws<ArgumentException>(() => CoverageGeometryTestRun.UpdateNormals(topology, before, larger, new(topology), out _));
        Assert.True(CoverageGeometryTestRun.UpdateNormals(topology, before, larger.AsSpan(0, topology.VertexCount), new(topology), out _));
    }

    [Fact]
    public void Flat_patch_follows_an_independent_nonuniform_affine_map()
    {
        var fixture = CoverageFixture.CurvedPanel(false); var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        // Flat rest data: the authored normal is the face normal and the tangent lies in the plane.
        var posed = fixture.Source.Positions.ToArray().Select(p => new ModelVertex(new(p.X, p.Y, 0), Vector3.UnitZ, Vector4.One, Vector2.Zero, new(1, 0, 0, -1))).ToArray();
        var before = new ModelVertex[topology.VertexCount]; topology.Interpolate(posed, before);
        // Rows of A, with a positive determinant and a translation, so every vertex moves.
        double[][] a = [[1.3, .2, .1], [-.15, .8, .25], [.05, -.3, 1.1]];
        double[] offset = [.5, -.25, .75];
        var after = before.ToArray();
        foreach (ref var v in after.AsSpan())
        {
            double x = v.Position.X, y = v.Position.Y, z = v.Position.Z;
            v.Position = new((float)(a[0][0] * x + a[0][1] * y + a[0][2] * z + offset[0]),
                (float)(a[1][0] * x + a[1][1] * y + a[1][2] * z + offset[1]), (float)(a[2][0] * x + a[2][1] * y + a[2][2] * z + offset[2]));
        }
        // The mapped in-plane axes span the deformed plane: normal unit(Ax cross Ay) = unit(A^-T n), tangent unit(A t).
        double[] ax = [a[0][0], a[1][0], a[2][0]], ay = [a[0][1], a[1][1], a[2][1]];
        var normal = Unit([ax[1] * ay[2] - ax[2] * ay[1], ax[2] * ay[0] - ax[0] * ay[2], ax[0] * ay[1] - ax[1] * ay[0]]);
        var tangent = Unit(ax);
        Assert.True(CoverageGeometryTestRun.UpdateNormals(topology, before, after, new(topology), out _));
        for (int i = 0; i < after.Length; i++)
        {
            Assert.NotEqual(before[i].Position, after[i].Position);
            Assert.True(Vector3.Distance(normal, after[i].Normal) < 1e-5f, $"normal {i}: {after[i].Normal} vs {normal}");
            Assert.True(Vector3.Distance(tangent, new(after[i].Tangent.X, after[i].Tangent.Y, after[i].Tangent.Z)) < 1e-5f, $"tangent {i}");
            Assert.Equal(-1, after[i].Tangent.W);
        }
        static Vector3 Unit(double[] v)
        {
            double length = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            return new((float)(v[0] / length), (float)(v[1] / length), (float)(v[2] / length));
        }
    }

    [Fact]
    public void Undisplaced_attributes_stay_exact_next_to_deformation()
    {
        var fixture = CoverageFixture.CurvedPanel(); var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var before = new ModelVertex[topology.VertexCount]; topology.Interpolate(fixture.Posed, before);
        var after = before.ToArray();
        for (int i = 0; i < after.Length; i++) after[i].Position.Z -= topology.Vertices[i].Strength * .05f;
        Assert.True(CoverageGeometryTestRun.UpdateNormals(topology, before, after, new(topology), out _));
        for (int i = 0; i < after.Length; i++)
            if (topology.Vertices[i].Strength == 0) Assert.Equal(before[i], after[i]);
    }

    [Fact]
    public void Warm_interpolation_and_changed_normal_update_allocate_nothing()
    {
        var fixture = CoverageFixture.CurvedPanel(); var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var before = new ModelVertex[topology.VertexCount]; var after = new ModelVertex[topology.VertexCount];
        var scratch = new CoverageReferenceScratch(topology);
        topology.Interpolate(fixture.Posed, before); before.CopyTo(after, 0);
        for (int i = 0; i < after.Length; i++) after[i].Position.Z -= topology.Vertices[i].Strength * .05f;
        Assert.True(CoverageGeometryTestRun.UpdateNormals(topology, before, after, scratch, out _));
        using var region = AllocationRegion.Start("warm interpolation and normal update");
        long start = GC.GetAllocatedBytesForCurrentThread();
        bool interpolated = topology.TryInterpolate(fixture.Posed, before, out _);
        bool updated = CoverageGeometryTestRun.UpdateNormals(topology, before, after, scratch, out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        region.Dispose();
        Assert.True(interpolated && updated);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Equal_zero_work_positions_copy_source_sign_bits()
    {
        var fixture = CoverageFixture.CurvedPanel(); var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var before = new ModelVertex[topology.VertexCount]; topology.Interpolate(fixture.Posed, before);
        before[0].Position.X = 0;
        var after = before.ToArray(); after[0].Position.X = BitConverter.Int32BitsToSingle(int.MinValue);
        Assert.True(CoverageGeometryTestRun.UpdateNormals(topology, before, after, new(topology), out _));
        Assert.Equal(MemoryMarshal.AsBytes(before.AsSpan()).ToArray(), MemoryMarshal.AsBytes(after.AsSpan()).ToArray());
    }

    [Fact]
    public void Smooth_material_and_mirrored_uv_seams_share_normals_but_keep_tangents()
    {
        Check(-1);
        Check(1);
        static void Check(float rightHandedness)
        {
            var fixture = CoverageFixture.CurvedPanel();
            var skin = fixture.Source.SkinVertices.ToArray();
            for (int i = 0; i < skin.Length; i++)
            {
                skin[i].Normal = Vector3.UnitZ;
                if (fixture.Source.Vertices[i].MaterialSlice == 1)
                {
                    skin[i].Uv = new(1, 1); skin[i].Tangent = new(0, 1, 0, rightHandedness);
                }
            }
            var source = new CoverageSourceTopology("smooth-seam", CoverageFixture.Hash, fixture.Source.Positions,
                fixture.Source.Vertices, fixture.Source.Triangles, skin);
            var topology = CoverageRefinement.Build(source, fixture.Map);
            var posed = skin.Select(v => SkinningMath.SkinVertex(v, CoverageFixture.Pose)).ToArray();
            var before = new ModelVertex[topology.VertexCount]; topology.Interpolate(posed, before);
            var after = before.ToArray();
            foreach (ref var v in after.AsSpan()) v.Position.Z -= .1f + .06f * v.Position.X * v.Position.Y + .03f * v.Position.X * v.Position.X;
            Assert.True(CoverageGeometryTestRun.UpdateNormals(topology, before, after, new(topology), out _));
            var seams = Enumerable.Range(0, after.Length).GroupBy(i => topology.Vertices[i].PositionGroup).Where(g => g.Count() > 1).ToArray();
            Assert.NotEmpty(seams);
            foreach (var seam in seams)
            {
                int[] pair = seam.ToArray();
                Assert.Equal(topology.Vertices[pair[0]].ShadingGroup, topology.Vertices[pair[1]].ShadingGroup);
                Assert.Equal(after[pair[0]].Normal, after[pair[1]].Normal);
                Assert.NotEqual(new Vector3(after[pair[0]].Tangent.X, after[pair[0]].Tangent.Y, after[pair[0]].Tangent.Z),
                    new Vector3(after[pair[1]].Tangent.X, after[pair[1]].Tangent.Y, after[pair[1]].Tangent.Z));
                foreach (int i in pair) { Assert.Equal(before[i].Tangent.W, after[i].Tangent.W); Assert.Equal(before[i].Uv, after[i].Uv); }
            }
        }
    }
}
