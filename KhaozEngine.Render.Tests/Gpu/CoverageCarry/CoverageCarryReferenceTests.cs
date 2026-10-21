using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageCarryReferenceTests
{
    [Theory]
    [InlineData(0f)]
    [InlineData(.375f)]
    [InlineData(1f)]
    public void Anchor_replay_matches_exact_construction_and_keeps_protected_words(float weight)
    {
        var fixture = new CoverageCarryFixture();
        var topology = fixture.Topology;
        var palette = CoverageCarryFixture.Palette(0);
        var commands = fixture.Commands(0, palette[0], weight);
        var anchor = new CoverageCarryAnchor(topology, palette, commands);
        Assert.Contains(anchor.Recession.ToArray(), r => r > 0);
        var scratch = new CoverageReferenceScratch(topology);
        var actual = new ModelVertex[topology.VertexCount];
        Assert.True(CoverageCarryReference.TryCarry(topology, palette, commands, anchor, scratch, actual, out var failure), failure.ToString());
        var expected = new ModelVertex[topology.VertexCount];
        var exact = new CoverageReferenceScratch(topology);
        Assert.True(CoverageReferenceConstructor.TryConstruct(topology, scratch.FlushedSource, Matrix4x4.Identity,
            commands, exact, expected, out failure), failure.ToString());
        Assert.Equal(Words(expected), Words(actual));
        for (int v = 0; v < actual.Length; v++)
            if (topology.Vertices[v].Strength == 0 || weight == 0)
                Assert.Equal(Words([scratch.Before[v]]), Words([actual[v]]));
    }

    [Fact]
    public void Stored_full_recession_survives_scratch_reuse_and_uses_the_current_frame()
    {
        var fixture = new CoverageCarryFixture();
        var palette = CoverageCarryFixture.Palette(0);
        var anchor = new CoverageCarryAnchor(fixture.Topology, palette, fixture.Commands(0, palette[0]));
        var saved = anchor.Recession.ToArray();
        var scratch = new CoverageReferenceScratch(fixture.Topology);
        var result = new ModelVertex[fixture.Topology.VertexCount];
        var commands = fixture.Commands(0, Matrix4x4.CreateRotationY(.1f), .5f);
        Assert.True(CoverageCarryReference.TryCarry(fixture.Topology, palette, commands, anchor, scratch, result, out var failure), failure.ToString());
        int group = Array.FindIndex(saved, r => r > 0);
        int vertex = anchor.Representatives[group];
        var f = new CoverageReferenceArithmetic();
        float applied = f.Mul(.5f, saved[group]);
        var recess = scratch.Frames[scratch.RegionCommands[fixture.Topology.Vertices[vertex].RegionIndex]].Recess;
        var before = scratch.Before[vertex].Position;
        var expected = new Vector3(f.Sub(before.X, f.Mul(applied, recess.X)), f.Sub(before.Y, f.Mul(applied, recess.Y)),
            f.Sub(before.Z, f.Mul(applied, recess.Z)));
        Assert.Equal(expected, result[vertex].Position);
        Assert.NotEqual(before.X, result[vertex].Position.X);
        Array.Fill(scratch.FullRecessions, float.NaN);
        Array.Fill(scratch.Published, default);
        Assert.Equal(saved, anchor.Recession.ToArray());
    }

    [Fact]
    public void Skin_then_refine_differs_from_skinning_interpolated_rest_weights()
    {
        var fixture = new CoverageCarryFixture();
        var topology = fixture.Topology;
        var rest = new[] { Matrix4x4.Identity, Matrix4x4.Identity };
        var scaled = new[] { Matrix4x4.Identity, Matrix4x4.CreateScale(2, 1, 1) };
        var commands = fixture.Commands(0, Matrix4x4.Identity, 0);
        var anchor = new CoverageCarryAnchor(topology, CoverageCarryFixture.Palette(0),
            fixture.Commands(0, CoverageCarryFixture.Palette(0)[0]));
        var scratch = new CoverageReferenceScratch(topology);
        var original = new ModelVertex[topology.VertexCount];
        var actual = new ModelVertex[topology.VertexCount];
        Assert.True(CoverageCarryReference.TryCarry(topology, rest, commands, anchor, scratch, original, out _));
        Assert.True(CoverageCarryReference.TryCarry(topology, scaled, commands, anchor, scratch, actual, out _));
        int witnesses = 0;
        for (int v = 0; v < topology.VertexCount; v++)
        {
            var record = topology.Vertices[v];
            if (record.SourceVertex >= 0) continue;
            var triangle = topology.Source.Triangles[record.SourceTriangle];
            float weight = (topology.Source.SkinVertices[triangle.A].BoneWeights.Y * record.A
                + topology.Source.SkinVertices[triangle.B].BoneWeights.Y * record.B
                + topology.Source.SkinVertices[triangle.C].BoneWeights.Y * record.C) / 12f;
            float wrong = original[v].Position.X * (1 + weight);
            if (MathF.Abs(actual[v].Position.X - wrong) > 1e-6f) witnesses++;
        }
        Assert.True(witnesses > 0, "Interpolated rest weights must not reproduce the source-skinned surface.");
    }

    [Fact]
    public void Zero_weight_copies_original_words_and_inclusive_flush_keeps_zero_signs()
    {
        var fixture = new CoverageCarryFixture(zeroWords: true);
        var palette = CoverageCarryFixture.Palette(0);
        var commands = fixture.Commands(0, palette[0], 0);
        var anchor = new CoverageCarryAnchor(fixture.Topology, palette, commands);
        var scratch = new CoverageReferenceScratch(fixture.Topology);
        var result = new ModelVertex[fixture.Topology.VertexCount];
        Assert.True(CoverageCarryReference.TryCarry(fixture.Topology, palette, commands, anchor, scratch, result, out _));
        Assert.Equal(Words(scratch.Before), Words(result));
        Assert.Contains(0x80000000u, Words(result));
        Assert.DoesNotContain(0x80800000u, Words(result));
        foreach (var alias in fixture.Topology.SourceAliases)
            Assert.Equal(scratch.FlushedSource[alias.SourceVertex].Position, scratch.FlushedSource[alias.Representative].Position);
    }

    internal static uint[] Words(ModelVertex[] vertices) => vertices.SelectMany(v => CoverageReferenceBits.Fields(v)
        .Select(BitConverter.SingleToUInt32Bits)).ToArray();
}
