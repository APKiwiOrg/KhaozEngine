using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageRecessionDirectionTests
{
    [Theory]
    [InlineData(0, .5f)]
    [InlineData(0, 1f)]
    [InlineData(1, 1f)]
    public void Sloped_direction_assertions_reject_flat_vectors_and_stale_rows_or_weights(int entry, float weight)
    {
        var fixture = new CoverageRecessionBodySizeFixture(4, 4);
        uint[] current = Carry(entry, weight);
        AssertCurrent(current);
        // Change direction words only. Positions and every other field remain valid controls.
        uint[] flatNormals = current.ToArray(), flatTangents = current.ToArray();
        for (int vertex = 0; vertex < fixture.Topology.VertexCount; vertex++)
        {
            Write(flatNormals, vertex, 3, Vector3.UnitZ);
            Write(flatTangents, vertex, 12, Vector3.UnitX);
        }
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertCurrent(flatNormals));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertCurrent(flatTangents));
        AssertStale(Carry(1 - entry, weight));
        AssertStale(Carry(entry, weight == 1 ? .5f : 1));

        uint[] Carry(int row, float transition)
        {
            var result = new ModelVertex[fixture.Topology.VertexCount];
            Assert.True(CoverageCarryReference.TryCarry(fixture.Topology, [Matrix4x4.Identity], fixture.Table, row,
                [new(0, Vector3.UnitZ, transition)], new(fixture.Topology, 1), result, out var failure), failure.ToString());
            return MemoryMarshal.Cast<ModelVertex, uint>(result.AsSpan()).ToArray();
        }
        void AssertCurrent(uint[] words) => CoverageRecessionComputeGpuTests.AssertWords(fixture, words, 0,
            entry, Vector3.UnitZ, weight, Vector3.Zero);
        void AssertStale(uint[] earlier)
        {
            uint[] changed = current.ToArray();
            for (int vertex = 0; vertex < fixture.Topology.VertexCount; vertex++)
            {
                earlier.AsSpan(vertex * 16 + 3, 3).CopyTo(changed.AsSpan(vertex * 16 + 3));
                earlier.AsSpan(vertex * 16 + 12, 3).CopyTo(changed.AsSpan(vertex * 16 + 12));
            }
            Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertCurrent(changed));
        }
    }

    static void Write(uint[] words, int vertex, int offset, Vector3 value)
    {
        words[vertex * 16 + offset] = BitConverter.SingleToUInt32Bits(value.X);
        words[vertex * 16 + offset + 1] = BitConverter.SingleToUInt32Bits(value.Y);
        words[vertex * 16 + offset + 2] = BitConverter.SingleToUInt32Bits(value.Z);
    }
}
