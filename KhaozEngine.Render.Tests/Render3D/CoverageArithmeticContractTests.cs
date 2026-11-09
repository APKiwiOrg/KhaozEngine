using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Independent position words retained from v1 and v2 direction bounds from dyadic fixtures and the 3-4-5 triangle.
/// Never refresh these from carry output. Incompatible arithmetic needs a new contract version and vector set.</summary>
public sealed class CoverageArithmeticContractTests
{
    internal const string V1Digest = "e7a4f4b8e168f7524ab3cdc9c0e2b67e71e477a80e6218e726c2ba593444a9d6";

    internal const string V2Digest = "490ef1bde077971ef594afddf336d2220b687e9291a664fd33501d2d8c09957d";

    [Fact]
    public void Arithmetic_identity_is_the_frozen_versioned_contract()
    {
        Assert.Equal(2, CoverageArithmeticContract.Version);
        Assert.Equal(V2Digest, CoverageTableBinding.ArithmeticSha256);
    }

    [Fact]
    public void Version_one_flush_and_separate_rounding_words_are_frozen()
    {
        var f = new CoverageReferenceArithmetic();
        uint[] actual = [Word(f.Read(Float(0x00800000))), Word(f.Read(Float(0x80800000))),
            Word(f.Read(Float(0x00800001))), Word(f.Mul(Float(0x00800000), 2)),
            Word(f.Mul(-MathF.ScaleB(1, -63), MathF.ScaleB(1, -63))),
            Word(f.Sub(16777216, f.Mul(4097, 4097))), Word(f.Div(1, 3)), Word(f.Sqrt(2))];
        // 4097^2 rounds to 16785408 before subtraction, giving -8192 rather than the fused -8193.
        Assert.Equal(new uint[] { 0, 0x80000000, 0x00800001, 0, 0x80000000, 0xc6000000, 0x3eaaaaab, 0x3fb504f3 }, actual);
    }

    [Fact]
    public void Version_two_noncommuting_palette_composition_and_skin_words_are_frozen()
    {
        var bind = Matrix4x4.CreateScale(2, 3, 4) * Matrix4x4.CreateTranslation(-1, 2, -3);
        var joint = Matrix4x4.CreateScale(5, 7, 11) * Matrix4x4.CreateTranslation(13, 17, 19);
        var composed = CoverageReferenceSkinning.Compose(joint, bind);
        // bind * joint has scale (10,21,44) and translation (8,31,-14).
        Assert.Equal(new uint[] { 0x41200000, 0, 0, 0, 0, 0x41a80000, 0, 0,
            0, 0, 0x42300000, 0, 0x41000000, 0x41f80000, 0xc1600000, 0x3f800000 },
            MemoryMarshal.Cast<Matrix4x4, uint>(MemoryMarshal.CreateReadOnlySpan(in composed, 1)).ToArray());
        var a = Matrix4x4.Identity; var b = Matrix4x4.Identity;
        a.M11 = 4097; a.M21 = -16777216; b.M11 = 4097; b.M12 = 1;
        Assert.Equal(0x46000000u, Word(CoverageReferenceSkinning.Compose(a, b).M11));
        var fixture = new CoverageRecessionBodySizeFixture(4, 4);
        var vertices = fixture.Source.SkinVertices.ToArray();
        foreach (ref var v in vertices.AsSpan())
        { v.BoneIndices = new(0, 1, 0, 0); v.BoneWeights = new(.25f, .75f, 0, 0); }
        var source = new CoverageSourceTopology("weighted-contract", fixture.Source.SourceSha256,
            fixture.Source.Positions, fixture.Source.Vertices, fixture.Source.Triangles, vertices);
        var posed = new ModelVertex[vertices.Length];
        Assert.True(CoverageReferenceSkinning.TrySkin(new(), new(source, 2), source,
            [composed, Matrix4x4.Identity], posed, out var failure));
        Assert.Equal(CoverageFailureCode.None, failure);
        // Weighted scale (13/4,6,47/4), translation (2,31/4,-7/2).
        // First source (-1/8,-1/8,0) becomes (51/32,7,-7/2). Axis directions and attributes stay exact.
        CoverageDirectionAssertions.EqualRecords(new uint[] { 0x3fcc0000, 0x40e00000, 0xc0600000, 0, 0, 0x3f800000,
            0x3f400000, 0x3f000000, 0x3e800000, 0x3f800000, 0, 0,
            0x3f800000, 0, 0, 0xbf800000 }, Words(posed[0]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Version_two_refinement_representatives_transition_direction_and_gathers_are_frozen(bool move)
    {
        var fixture = new CoverageRecessionBodySizeFixture(4, 4);
        var topology = fixture.Topology;
        int[] representatives = new int[topology.PositionGroupCount];
        CoverageReferenceTopology.FillActiveRepresentatives(topology, [0], representatives, new());
        // First ten groups own source surfaces 0,1,6,5,2,7,3,8,4,9. Seam aliases choose the first source.
        Assert.Equal(new[] { -1, -1, 2, -1, -1, 8, -1, 14, -1, -1 }, representatives[..10]);
        int witness = Enumerable.Range(0, topology.VertexCount).Single(i => topology.Vertices[i] is
        { SourceTriangle: 8, SourceVertex: -1, A: 6, B: 0, C: 6 });
        var recipe = topology.GetPositionRecipe(topology.Vertices[witness].PositionGroup);
        Assert.Equal(new CoveragePositionRecipe(5, 2, 26, 6, 0, 6, 12), recipe);
        var output = new ModelVertex[topology.VertexCount];
        Assert.True(CoverageCarryReference.TryCarry(topology, [Matrix4x4.Identity], fixture.Table, 0,
            [new(0, new(0, 0, 6), move ? .5f : 0)], new(topology, 1), output, out var failure));
        Assert.True(failure.IsSuccess);
        // Midpoint (-3/32,-1/32) has tent 1/32, scalar 1/128, applied 1/256 and Z recession 3/128.
        // The left ramp derivative is (1,0,-3/4). Ideal normal/tangent are (3/5,0,4/5)/(4/5,0,-3/5).
        // Six rounded normal corner contributions gather to X=0x3f199999. The three tangent corners retain -0x3f19999a.
        CoverageDirectionAssertions.EqualRecords(new uint[] { 0xbdc00000, 0xbd000000, move ? 0xbcc00000u : 0,
            move ? 0x3f199999u : 0, 0, move ? 0x3f4ccccdu : 0x3f800000,
            0x3f400000, 0x3f000000, 0x3e800000, 0x3f800000, 0x3d000000, 0x3dc00000,
            move ? 0x3f4ccccdu : 0x3f800000, 0, move ? 0xbf19999au : 0, 0xbf800000 }, Words(output[witness]));
    }

    [Fact]
    public void Version_two_interior_refinement_recipe_and_attribute_words_are_frozen()
    {
        var fixture = new CoverageRecessionBodySizeFixture(4, 4, level: 3);
        var topology = fixture.Topology;
        int witness = Enumerable.Range(0, topology.VertexCount).Single(i => topology.Vertices[i] is
        { SourceTriangle: 8, SourceVertex: -1, A: 4, B: 4, C: 4 });
        Assert.Equal(new CoveragePositionRecipe(5, 2, 26, 4, 4, 4, 12),
            topology.GetPositionRecipe(topology.Vertices[witness].PositionGroup));
        var output = new ModelVertex[topology.VertexCount];
        Assert.True(CoverageCarryReference.TryCarry(topology, [Matrix4x4.Identity], fixture.Table, -1,
            [], new(topology, 1), output, out _));
        // Centroid of (-1/8,-1/16),(-1/16,-1/16),(-1/16,0) is (-1/12,-1/24).
        // UV is (1/24,1/12). Each literal is the independently rounded rational, not captured carry output.
        CoverageDirectionAssertions.EqualRecords(new uint[] { 0xbdaaaaab, 0xbd2aaaab, 0, 0, 0, 0x3f800000,
            0x3f400000, 0x3f000000, 0x3e800000, 0x3f800000, 0x3d2aaaab, 0x3daaaaab,
            0x3f800000, 0, 0, 0xbf800000 }, Words(output[witness]));
    }

    [Theory]
    [InlineData(false, CoverageFailureCode.FoldedPatch)]
    [InlineData(true, CoverageFailureCode.UnsupportedTransform)]
    public void Version_two_refusals_never_publish_partial_output(bool nonfinite, CoverageFailureCode expected)
    {
        var fixture = new CoverageRecessionBodySizeFixture(4, 4);
        var matrix = Matrix4x4.Identity;
        if (nonfinite) matrix.M11 = float.NaN;
        var output = Enumerable.Repeat(new ModelVertex(Vector3.One, Vector3.UnitZ, Vector4.One), fixture.Topology.VertexCount).ToArray();
        byte[] before = MemoryMarshal.AsBytes(output.AsSpan()).ToArray();
        Assert.False(CoverageCarryReference.TryCarry(fixture.Topology, [matrix], fixture.Table, 0,
            [new(0, nonfinite ? Vector3.UnitZ : new(100, 0, 0), 1)], new(fixture.Topology, 1), output, out var failure));
        Assert.Equal(expected, failure.Code);
        Assert.Equal(before, MemoryMarshal.AsBytes(output.AsSpan()).ToArray());
        Assert.True(CoverageCarryReference.TryCarry(fixture.Topology, [Matrix4x4.Identity], fixture.Table, -1,
            [], new(fixture.Topology, 1), output, out _));
    }

    static uint[] Words(ModelVertex value) => MemoryMarshal.Cast<ModelVertex, uint>(MemoryMarshal.CreateReadOnlySpan(in value, 1)).ToArray();
    static uint Word(float value) => BitConverter.SingleToUInt32Bits(value);
    static float Float(uint word) => BitConverter.UInt32BitsToSingle(word);
}
