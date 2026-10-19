using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageGpuUploadTests
{
    [Fact]
    public void Cold_uploads_and_uniforms_preserve_reference_words_and_rows()
    {
        var fixture = CoverageFixture.CurvedPanel(seams: true, level: 3);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var reference = new CoverageReferenceTopology(topology);
        var upload = CoverageGpuUpload.Host(topology, 2);
        Assert.Equal(16, CoverageGpuLayout.ModelWords);
        Assert.Equal(64, CoverageGpuLayout.ModelBytes);
        Assert.Equal(new uint[] { (uint)topology.SourceVertexCount, (uint)topology.VertexCount, (uint)topology.TriangleCount,
            (uint)topology.PositionGroupCount, (uint)topology.SourceAliases.Count, (uint)topology.ShadingGroupCount, 2, 0 }, upload.Words[..8]);
        Assert.Equal(new uint[12], upload.Words[52..64]);
        EqualWords(reference.Indices, upload.Indices);
        EqualInts(reference.CoreFaceOffsets, upload.CoreOffsets); EqualInts(reference.CoreFaces, upload.CoreFaces);
        EqualInts(reference.VertexFaceOffsets, upload.VertexOffsets); EqualInts(reference.VertexCorners, upload.VertexCorners);
        EqualInts(reference.ShadingCornerOffsets, upload.ShadingOffsets); EqualInts(reference.ShadingCorners, upload.ShadingCorners);
        EqualInts(reference.GroupRepresentatives, upload.GroupRepresentatives);
        EqualInts(reference.Recipes.SelectMany(r => new[] { r.A, r.B, r.C, r.WeightA, r.WeightB, r.WeightC, r.Denominator }), upload.PositionRecipes);
        EqualInts(reference.SourceAliases.SelectMany(r => new[] { r.SourceVertex, r.Representative }), upload.SourceAliases);
        EqualInts(reference.SourceTriangles.SelectMany(r => new[] { r.Mesh, r.Primitive, r.Triangle, r.Node, r.MaterialSlice, r.A, r.B, r.C }), upload.SourceTriangles);
        EqualInts(reference.Triangles.SelectMany(r => new[] { r.SourceTriangle, r.RegionIndex, (int)r.Role }), upload.RefinedTriangles);
        EqualWords(reference.Vertices.SelectMany(r => new uint[] { U(r.SourceTriangle), U(r.SourceVertex), U(r.A), U(r.B), U(r.C),
            U(r.Denominator), U(r.PositionGroup), U(r.ShadingGroup), U(r.RegionIndex), Bits(r.Strength) }), upload.RefinedVertices);
        EqualWords(reference.GroupWeights.SelectMany(w => new[] { Bits(w.A), Bits(w.B), Bits(w.C) }), upload.GroupWeights);
        EqualWords(reference.VertexWeights.SelectMany(w => new[] { Bits(w.A), Bits(w.B), Bits(w.C) }), upload.VertexWeights);
        EqualWords(reference.EdgeParameters.Select(Bits), upload.EdgeParameters);
        EqualInts(new CoverageReferenceSkin(fixture.Source, 2).Indices, upload.SkinIndices);
        EqualWords(fixture.Source.SkinVertices.ToArray().SelectMany(v => CoverageReferenceBits.Fields(new(v.Position, v.Normal, v.Color, v.Uv, v.Tangent))
            .Concat(new[] { v.BoneWeights.X, v.BoneWeights.Y, v.BoneWeights.Z, v.BoneWeights.W }).Select(Bits)), upload.SourceAttributes);
        Assert.NotEmpty(reference.SourceAliases);
        Assert.Contains(reference.Recipes, r => r.WeightA == 0 && r.B > r.C && r.WeightB > 0 && r.WeightC > 0);
        Assert.Contains(Enumerable.Range(0, topology.ShadingGroupCount), g =>
            reference.ShadingCorners[reference.ShadingCornerOffsets[g]..reference.ShadingCornerOffsets[g + 1]].Length > 1);
        var offsets = Table(upload.ActiveCandidateOffsets).Select(x => (int)x).ToArray();
        var candidates = Table(upload.ActiveCandidates);
        for (int g = 0; g < topology.PositionGroupCount; g++)
            Assert.Equal(topology.Vertices.Select((v, i) => (v, i)).Where(p => p.v.PositionGroup == g && p.v.RegionIndex >= 0 && p.v.Strength > 0)
                .Select(p => (uint)p.i), candidates[offsets[g]..offsets[g + 1]]);
        int cursor = 64;
        int[] counts = [reference.SourceVertexCount, reference.SourceVertexCount, reference.VertexCount, reference.TriangleCount, reference.SourceTriangles.Count,
            reference.AliasCount, reference.Indices.Length, reference.Recipes.Length, reference.GroupWeights.Length, reference.VertexWeights.Length,
            reference.EdgeParameters.Length, reference.GroupRepresentatives.Length, reference.CoreFaceOffsets.Length, reference.CoreFaces.Length,
            reference.VertexFaceOffsets.Length, reference.VertexCorners.Length, reference.ShadingCornerOffsets.Length, reference.ShadingCorners.Length,
            offsets.Length, candidates.Length, reference.Indices.Length, reference.Indices.Length];
        int[] strides = [20, 4, 10, 3, 8, 2, 1, 7, 3, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1];
        for (int table = 0; table < counts.Length; table++)
        {
            Assert.Equal((uint)cursor, upload.Words[8 + table * 2]);
            Assert.Equal((uint)counts[table], upload.Words[9 + table * 2]);
            cursor += counts[table] * strides[table];
        }
        Assert.Equal(cursor, upload.Words.Length);
        AssertPulls(topology, upload);
        Assert.Equal((uint)(64 + 2 * topology.SourceVertexCount * 16 + 5 * topology.VertexCount * 16
            + 6 * topology.PositionGroupCount + 3 * topology.TriangleCount
            + 6 * Math.Max(Math.Max(topology.SourceVertexCount, topology.SourceAliases.Count), Math.Max(Math.Max(topology.PositionGroupCount, topology.VertexCount),
                Math.Max(3 * topology.TriangleCount, 2 * topology.PositionGroupCount)))), CoverageGpuLayout.WorkWords(upload));

        uint[] Table(uint offset)
        {
            uint[] starts = Enumerable.Range(0, 22).Select(i => upload.Words[8 + i * 2]).Append((uint)upload.Words.Length).ToArray();
            int table = Array.IndexOf(starts, offset);
            return upload.Words[(int)offset..(int)starts[table + 1]];
        }
        void EqualInts(IEnumerable<int> values, uint offset) => EqualWords(values.Select(U), offset);
        void EqualWords(IEnumerable<uint> values, uint offset) => Assert.Equal(values, Table(offset));
    }

    [Fact]
    public void Support_upload_preserves_Task3_reference_words_for_every_probe_fixture()
    {
        CoverageGpuSupportProbe.Data((words, support) =>
        {
            var actual = CoverageGpuUpload.Support(support.Support);
            Assert.Equal(257, support.CellOffsets.Length);
            Assert.Equal(support.TriangleCount, actual.TriangleCount); Assert.Equal(support.BoundaryCount, actual.BoundaryCount);
            Assert.Equal(new uint[] { Bits(support.MaxSlope), Bits(support.GridMin.X), Bits(support.GridMin.Y), Bits(support.GridMax.X), Bits(support.GridMax.Y),
                (uint)support.TriangleCount, (uint)support.BoundaryCount }, actual.Words[..7]);
            Assert.Equal(new uint[3], actual.Words[13..16]);
            Assert.Equal(support.Positions.SelectMany(p => new[] { Bits(p.X), Bits(p.Y), Bits(p.Z) }), Row(actual.Positions, actual.SourceIndices));
            Assert.Equal(support.SourceIndices.Select(U), Row(actual.SourceIndices, actual.Triangles));
            Assert.Equal(support.Triangles.SelectMany(t => new[] { t.Mesh, t.Primitive, t.Triangle, t.Node, t.MaterialSlice, t.A, t.B, t.C }).Select(U),
                Row(actual.Triangles, actual.BoundaryVertices));
            Assert.Equal(support.BoundarySourceVertices.Select(U), Row(actual.BoundaryVertices, actual.CellOffsets));
            Assert.Equal(support.CellOffsets.Select(U), Row(actual.CellOffsets, actual.CellTriangles));
            Assert.Equal(support.CellTriangles.Select(U), actual.Words[(int)actual.CellTriangles..]);
            Assert.Equal(16u, actual.Positions);
            uint[] Row(uint start, uint end) => actual.Words[(int)start..(int)end];
            uint offset = checked((uint)words.Count);
            words.AddRange(actual.Words);
            return offset;
        });
    }

    [Fact]
    public void Four_integer_skin_indices_are_validated_even_for_zero_weights()
    {
        var fixture = CoverageFixture.CurvedPanel();
        var skin = fixture.Source.SkinVertices.ToArray();
        for (int i = 0; i < skin.Length; i++) skin[i].BoneIndices = new(3, 2, 1, 0);
        var source = new CoverageSourceTopology("four-bones", CoverageFixture.Hash, fixture.Source.Positions,
            fixture.Source.Vertices, fixture.Source.Triangles, skin);
        var topology = CoverageRefinement.Build(source, fixture.Map);
        var upload = CoverageGpuUpload.Host(topology, 4);
        Assert.Equal(Enumerable.Range(0, skin.Length).SelectMany(_ => new uint[] { 3, 2, 1, 0 }),
            upload.Words.AsSpan((int)upload.SkinIndices, skin.Length * 4).ToArray());
        Assert.Throws<ArgumentException>(() => CoverageGpuUpload.Host(topology, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => CoverageGpuUpload.Host(topology, 0));
    }

    [Fact]
    public void Public_vertex_limit_keeps_uint_indices_and_empty_gather_rows()
    {
        const int count = 65_536;
        var skin = Enumerable.Range(0, count).Select(i => new SkinnedVertex { Position = new(i, i == count - 1 ? 1 : 0, 0), Normal = Vector3.UnitZ }).ToArray();
        var source = new CoverageSourceTopology("uint-host", CoverageFixture.Hash, skin.Select(v => v.Position).ToArray(),
            Enumerable.Range(0, count).Select(i => new CoverageSourceVertex(0, 0, i, -1, 0, i)),
            [new(0, 0, 0, -1, 0, count - 3, count - 2, count - 1)], skin);
        var map = new HostCoverageMap(CoverageFixture.Hash, Enumerable.Range(0, count).Select(i => new CoverageTopologyEntry(0, 0, i, i)),
            [new("unused", [new(new(0, 0, 0, 1), CoverageTriangleRole.Collar)], [new(count - 3, 0), new(count - 2, 0), new(count - 1, 0)])], [], new("test", "uint"));
        var topology = CoverageRefinement.Build(source, map);
        var upload = CoverageGpuUpload.Host(topology, 1);
        Assert.Equal(count, upload.VertexCount);
        Assert.Equal(new uint[] { 65533, 65534, 65535 }, upload.Words.AsSpan((int)upload.Indices, 3).ToArray());
        Assert.Equal(new uint[] { 65533, 65534, 65535 }, upload.Words.AsSpan((int)upload.FallbackPullIndices, 3).ToArray());
        Assert.Equal(upload.CoreFaces, upload.VertexOffsets);
        Assert.Equal(upload.ActiveCandidates, upload.RefinedPullIndices);
        Assert.Equal(0u, upload.Words[47]);
        Assert.Equal(checked((uint)upload.Words.Length * 4), upload.ByteCount);
    }

    [Fact]
    public void Model_words_copy_every_bit_and_checked_layout_rejects_address_wrap()
    {
        var v = new ModelVertex(new(-0f, float.Epsilon, -float.Epsilon), Vector3.UnitZ, new(1, 2, 3, 4), new(5, 6), new(7, 8, 9, -1));
        uint[] words = new uint[16];
        CoverageGpuUpload.WriteModel(v, words);
        Assert.Equal(CoverageReferenceBits.Fields(v).Select(Bits), words);
        Assert.Throws<ArgumentException>(() => CoverageGpuUpload.WriteModel(v, new uint[15]));
        var fixture = CoverageFixture.CurvedPanel();
        var host = CoverageGpuUpload.Host(CoverageRefinement.Build(fixture.Source, fixture.Map), 2);
        var r = CoverageGpuLayout.CreateReservation(1, 32, 64, 96, 128, 160, host);
        Assert.Equal((1, 32u, 64u, 96u, 128u, 160u), (r.ActorSlot, r.StaticBase, r.FrameBase, r.WorkBase, r.CurrentBase, r.PreviousBase));
        Assert.Throws<OverflowException>(() => CoverageGpuLayout.CreateReservation(0, uint.MaxValue, 0, 0, 0, 0, host));
        Assert.Throws<OverflowException>(() => CoverageGpuLayout.CreateReservation(0, 0, uint.MaxValue, 0, 0, 0, host));
        Assert.Throws<OverflowException>(() => CoverageGpuLayout.CreateReservation(0, 0, 0, uint.MaxValue, 0, 0, host));
        Assert.Throws<OverflowException>(() => CoverageGpuLayout.CreateReservation(0, 0, 0, 0, uint.MaxValue, 0, host));
        Assert.Throws<OverflowException>(() => CoverageGpuLayout.CreateReservation(0, 0, 0, 0, 0, uint.MaxValue, host));
        Assert.Throws<OverflowException>(() => CoverageGpuLayout.CreateReservation(int.MaxValue, 0, 0, 0, 0, 0, host));
    }

    static void AssertPulls(CoverageRefinement topology, CoverageGpuHostUpload upload)
    {
        uint[] refined = upload.Words.AsSpan((int)upload.RefinedPullIndices, topology.TriangleCount * 3).ToArray();
        Assert.Equal(topology.Indices.ToArray(), refined);
        uint[] fallback = upload.Words.AsSpan((int)upload.FallbackPullIndices, topology.TriangleCount * 3).ToArray();
        var seen = new HashSet<int>();
        for (int t = 0; t < topology.TriangleCount; t++)
        {
            int source = topology.Triangles[t].SourceTriangle;
            var triangle = topology.Source.Triangles[source];
            uint[] expected = seen.Add(source) ? [U(triangle.A), U(triangle.B), U(triangle.C)] : [U(triangle.A), U(triangle.A), U(triangle.A)];
            Assert.Equal(expected, fallback[(t * 3)..(t * 3 + 3)]);
        }
    }
    static uint U(int x) => unchecked((uint)x);
    static uint Bits(float f) => BitConverter.SingleToUInt32Bits(f);
}
