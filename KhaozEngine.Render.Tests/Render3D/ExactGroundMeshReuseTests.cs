using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class ExactGroundMeshReuseTests
{
    [Fact]
    public void DuplicateTriangleCornersReuseVerticesWithoutChangingAnyOrderedInput()
    {
        GltfMesh quad = MeshPrimitives.Plane(2, 2);
        ModelVertex[] vertices = quad.Indices32.Select(i => quad.Vertices[i]).ToArray();
        Vector3[] outlines = OutlineNormalBuilder.Build(vertices, Enumerable.Range(0, vertices.Length).Select(i => (uint)i).ToArray());
        uint[] indices = Enumerable.Range(0, vertices.Length).Select(i => (uint)i).ToArray();
        GroundUploadStreams actual = ExactGroundMeshReuse.Compact(vertices, outlines, indices);
        Assert.Equal(4, actual.Vertices.Length);
        Assert.Equal(indices.Length, actual.Indices.Length);
        Assert.Equal(MeshBounds.FromVertices(vertices), MeshBounds.FromVertices(actual.Vertices));
        AssertExpanded(vertices, outlines, indices, actual);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    public void EveryWordOfEveryVertexStreamParticipatesInTheByteKey(int word)
    {
        ModelVertex value = new(Vector3.Zero, Vector3.UnitY, Vector4.One, Vector2.Zero, new Vector4(0, 0, 1, 0));
        ModelVertex[] vertices = [value, value, value];
        Vector3[] outlines = [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY];
        if (word < 16) MemoryMarshal.AsBytes(vertices.AsSpan(1, 1))[word * 4] ^= 1;
        else MemoryMarshal.AsBytes(outlines.AsSpan(1, 1))[(word - 16) * 4] ^= 1;
        uint[] indices = [0, 1, 2];
        GroundUploadStreams actual = ExactGroundMeshReuse.Compact(vertices, outlines, indices);
        Assert.Equal(2, actual.Vertices.Length);
        AssertExpanded(vertices, outlines, indices, actual);
    }

    [Fact]
    public void SignedZeroAndNanPayloadsAreNotWeldedByNumericEquality()
    {
        ModelVertex value = new(Vector3.Zero, Vector3.UnitY, Vector4.One);
        ModelVertex[] vertices = [value, value, value, value];
        vertices[1].Position.X = BitConverter.Int32BitsToSingle(int.MinValue);
        vertices[2].Uv.X = BitConverter.Int32BitsToSingle(0x7fc00001);
        vertices[3].Uv.X = BitConverter.Int32BitsToSingle(0x7fc00002);
        Vector3[] outlines = new Vector3[4];
        uint[] indices = [0, 1, 2, 3, 0, 2];
        GroundUploadStreams actual = ExactGroundMeshReuse.Compact(vertices, outlines, indices);
        Assert.Equal(4, actual.Vertices.Length);
        AssertExpanded(vertices, outlines, indices, actual);
    }

    internal static void AssertExpanded(ModelVertex[] vertices, Vector3[] outlines, uint[] indices, GroundUploadStreams actual)
    {
        Assert.NotEmpty(indices);
        Assert.Equal(MemoryMarshal.AsBytes(indices.Select(i => vertices[i]).ToArray().AsSpan()).ToArray(),
            MemoryMarshal.AsBytes(actual.Indices.Select(i => actual.Vertices[i]).ToArray().AsSpan()).ToArray());
        Assert.Equal(MemoryMarshal.AsBytes(indices.Select(i => outlines[i]).ToArray().AsSpan()).ToArray(),
            MemoryMarshal.AsBytes(actual.Indices.Select(i => actual.OutlineNormals[i]).ToArray().AsSpan()).ToArray());
    }
}
