using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageReferenceSkinningTests
{
    [Fact]
    public void Compose_pins_scalar_row_vector_order()
    {
        var a = new Matrix4x4(1.1f, -.2f, .3f, 0, .4f, 1.2f, -.5f, 0, -.6f, .7f, .9f, 0, 123.4f, -23.5f, 45.6f, 1);
        var b = new Matrix4x4(.8f, .3f, -.4f, 0, -.2f, 1.3f, .6f, 0, .5f, -.7f, 1.1f, 0, -87.6f, 54.3f, -21.2f, 1);
        foreach (var (joint, inverse) in new[] { (a, b), (b, a), (Matrix4x4.Identity, a) })
        {
            float[] j = Fields(joint), v = Fields(inverse), expected = new float[16];
            for (int row = 0; row < 4; row++)
                for (int column = 0; column < 4; column++)
                    expected[row * 4 + column] = (((v[row * 4] * j[column]) + (v[row * 4 + 1] * j[4 + column]))
                        + (v[row * 4 + 2] * j[8 + column])) + (v[row * 4 + 3] * j[12 + column]);
            float[] actual = Fields(CoverageReferenceSkinning.Compose(joint, inverse));
            AssertBits(expected, actual);
            Close(Fields(SkinningMath.Compose(joint, inverse)), actual, 1e-6f);
        }
    }

    [Fact]
    public void Reference_skinning_pins_blend_transform_and_normalize_order()
    {
        var vertex = new SkinnedVertex
        {
            Position = new(.7f, -.3f, 1.2f),
            Normal = new(.2f, .3f, .9f),
            Color = new(.1f, .2f, .3f, 1),
            Uv = new(.25f, .75f),
            Tangent = new(.9f, .2f, -.1f, -1),
            BoneIndices = new(0, 1, 0, 1),
            BoneWeights = new(.3f, .5f, .1f, .1f)
        };
        Matrix4x4[] palette = [new(1.1f, .2f, .3f, 0, .4f, .9f, -.2f, 0, -.3f, .1f, 1.2f, 0, 3, -2, 1, 1),
            new(.8f, -.3f, .1f, 0, .2f, 1.2f, .4f, 0, .1f, -.2f, .9f, 0, -1, 4, 2, 1)];
        var source = Source(vertex);
        var skin = new CoverageReferenceSkin(source, 2);
        var posed = new ModelVertex[1];
        var f = new CoverageReferenceArithmetic();
        Assert.True(CoverageReferenceSkinning.TrySkin(f, skin, source, palette, posed, out var code));
        Assert.Equal(CoverageFailureCode.None, code);
        float[] first = Fields(palette[0]), second = Fields(palette[1]), blend = new float[16];
        for (int i = 0; i < 16; i++) blend[i] = ((first[i] * .3f + second[i] * .5f) + first[i] * .1f) + second[i] * .1f;
        Vector3 position = Transform(vertex.Position, blend, true);
        Vector3 normal = Normalize(Transform(vertex.Normal, blend, false), vertex.Normal);
        Vector3 tangent = Normalize(Transform(new(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z), blend, false),
            new(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z));
        var expected = new ModelVertex(position, normal, vertex.Color, vertex.Uv, new(tangent, vertex.Tangent.W));
        AssertBits(CoverageReferenceBits.Fields(expected), CoverageReferenceBits.Fields(posed[0]));
        Close(CoverageReferenceBits.Fields(SkinningMath.SkinVertex(vertex, palette)), CoverageReferenceBits.Fields(posed[0]), 1e-5f);
        Assert.Equal(0, f.EventCount);
    }

    [Fact]
    public void Zero_weights_and_degenerate_directions_keep_the_documented_fallbacks()
    {
        var v = new SkinnedVertex { Position = new(1, 2, 3), Normal = new(0, 0, 1), Color = Vector4.One };
        var source = Source(v);
        var posed = new ModelVertex[1];
        var f = new CoverageReferenceArithmetic();
        var skin = new CoverageReferenceSkin(source, 1);
        Assert.True(CoverageReferenceSkinning.TrySkin(f, skin, source, new[] { Matrix4x4.CreateTranslation(10, 20, 30) }, posed, out _));
        Assert.Equal(v.Position, posed[0].Position);
        Assert.Equal(Vector4.Zero, posed[0].Tangent);
        v.BoneWeights = new(1, 0, 0, 0);
        source = Source(v);
        skin = new(source, 1);
        Assert.True(CoverageReferenceSkinning.TrySkin(f, skin, source, new[] { Matrix4x4.CreateScale(1e-10f) }, posed, out _));
        Assert.Equal(v.Normal, posed[0].Normal);
        var invalid = Matrix4x4.Identity;
        invalid.M11 = float.PositiveInfinity;
        Assert.False(CoverageReferenceSkinning.TrySkin(f, skin, source, new[] { invalid }, posed, out var code));
        Assert.Equal(CoverageFailureCode.UnsupportedTransform, code);
    }

    [Fact]
    public void Reference_skin_rejects_invalid_bone_indices()
    {
        var source = Source(new() { BoneIndices = new(0, 1, 0, 0) });
        Assert.Throws<ArgumentException>(() => new CoverageReferenceSkin(source, 1));
    }

    [Fact]
    public void Warm_skinning_allocates_nothing_and_traces_the_source_vertex()
    {
        var source = Source(new() { Position = Vector3.One, Normal = Vector3.UnitZ, Color = new(MathF.ScaleB(1, -130), 1, 1, 1) });
        var skin = new CoverageReferenceSkin(source, 1);
        var f = new CoverageReferenceArithmetic();
        var output = new ModelVertex[1];
        Matrix4x4[] palette = [Matrix4x4.Identity];
        Assert.True(CoverageReferenceSkinning.TrySkin(f, skin, source, palette, output, out _));
        f.Reset();
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool success = CoverageReferenceSkinning.TrySkin(f, skin, source, palette, output, out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(success);
        Assert.Equal(0, allocated);
        Assert.Equal(CoverageExactnessPhase.Skinning, f.FirstSite.Phase);
        Assert.Equal(0, f.FirstSite.Element);
        Assert.Equal(0u, BitConverter.SingleToUInt32Bits(output[0].Color.X));
    }

    static CoverageSourceTopology Source(SkinnedVertex v) => new("reference-skin", CoverageFixture.Hash, new[] { v.Position },
        new[] { new CoverageSourceVertex(0, 0, 0, -1, 0, 0) }, Array.Empty<CoverageSourceTriangle>(), new[] { v });

    static Vector3 Transform(Vector3 p, float[] m, bool point) => new(
        (((p.X * m[0]) + (p.Y * m[4])) + (p.Z * m[8])) + (point ? m[12] : 0),
        (((p.X * m[1]) + (p.Y * m[5])) + (p.Z * m[9])) + (point ? m[13] : 0),
        (((p.X * m[2]) + (p.Y * m[6])) + (p.Z * m[10])) + (point ? m[14] : 0));
    static Vector3 Normalize(Vector3 n, Vector3 fallback)
    {
        float length = MathF.Sqrt(((n.X * n.X) + (n.Y * n.Y)) + (n.Z * n.Z));
        return length > 1e-8f ? new(n.X / length, n.Y / length, n.Z / length) : fallback;
    }
    static float[] Fields(Matrix4x4 m) => [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];
    static void AssertBits(float[] expected, float[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++) Assert.Equal(BitConverter.SingleToUInt32Bits(expected[i]), BitConverter.SingleToUInt32Bits(actual[i]));
    }
    static void Close(float[] expected, float[] actual, float relative)
    {
        for (int i = 0; i < expected.Length; i++) Assert.True(MathF.Abs(actual[i] - expected[i]) <= relative * MathF.Max(1, MathF.Abs(expected[i])));
    }
}
