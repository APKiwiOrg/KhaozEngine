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
        Assert.True(CoverageSurfaceNormals.Update(topology, before, after, new(topology), out var failure));
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
        Assert.True(CoverageSurfaceNormals.Update(topology, before, after, new(topology), out _));
        Assert.Equal(MemoryMarshal.AsBytes(before.AsSpan()).ToArray(), MemoryMarshal.AsBytes(after.AsSpan()).ToArray());
        for (int i = 0; i < before.Length; i++) { before[i].Tangent = Vector4.Zero; after[i].Tangent = Vector4.Zero; after[i].Position.Z -= after[i].Position.X * .1f; }
        Assert.True(CoverageSurfaceNormals.Update(topology, before, after, new(topology), out _));
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
        Assert.False(CoverageSurfaceNormals.Update(topology, before, after, new(topology), out var failure));
        Assert.Equal(CoverageFailureCode.FoldedPatch, failure.Code);
        Assert.Equal(expected, MemoryMarshal.AsBytes(after.AsSpan()).ToArray());
    }

    [Fact]
    public void Undisplaced_attributes_stay_exact_next_to_deformation()
    {
        var fixture = CoverageFixture.CurvedPanel(); var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var before = new ModelVertex[topology.VertexCount]; topology.Interpolate(fixture.Posed, before);
        var after = before.ToArray();
        for (int i = 0; i < after.Length; i++) after[i].Position.Z -= topology.Vertices[i].Strength * .05f;
        Assert.True(CoverageSurfaceNormals.Update(topology, before, after, new(topology), out _));
        for (int i = 0; i < after.Length; i++)
            if (topology.Vertices[i].Strength == 0) Assert.Equal(before[i], after[i]);
    }

    [Fact]
    public void Warm_interpolation_and_changed_normal_update_allocate_nothing()
    {
        var fixture = CoverageFixture.CurvedPanel(); var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var before = new ModelVertex[topology.VertexCount]; var after = new ModelVertex[topology.VertexCount];
        var scratch = new CoverageScratch(topology);
        topology.Interpolate(fixture.Posed, before); before.CopyTo(after, 0);
        for (int i = 0; i < after.Length; i++) after[i].Position.Z -= topology.Vertices[i].Strength * .05f;
        Assert.True(CoverageSurfaceNormals.Update(topology, before, after, scratch, out _));
        long start = GC.GetAllocatedBytesForCurrentThread();
        bool interpolated = topology.TryInterpolate(fixture.Posed, before, out _);
        bool updated = CoverageSurfaceNormals.Update(topology, before, after, scratch, out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
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
        Assert.True(CoverageSurfaceNormals.Update(topology, before, after, new(topology), out _));
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
            Assert.True(CoverageSurfaceNormals.Update(topology, before, after, new(topology), out _));
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
