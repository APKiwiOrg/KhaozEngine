using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class CoverageActorBoundsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Palette_escape_and_rebasing_have_conservative_bounds(bool signed)
    {
        var vertices = new[]
        {
            Vertex(Vector3.Zero, new(1, 0, 0, 0)),
            Vertex(new(1, -2, 3), Vector4.Zero),
            Vertex(new(-2, 3, -1), new(2, .5f, 0, 0)),
            Vertex(new(.7f, -.3f, 1.2f), new(.3f, .5f, .1f, .1f)),
            Vertex(new(3, -4, 2), new(1e-9f, 0, 0, 0)),
            Vertex(new(-1, 2, -3), signed ? new(2, -1, .25f, 0) : new(.25f, .5f, .75f, 1))
        };
        var source = Source(vertices);
        Matrix4x4[] palette = [Matrix4x4.CreateTranslation(100, 0, 0),
            new(2, .3f, -.2f, 0, .7f, 3, .4f, 0, -.6f, .5f, .8f, 0, -4, 7, -2, 1)];
        var producer = new CoverageActorBounds(source);
        var skin = producer.BoundSkin(palette);
        Assert.True(skin.Max.X >= 100f);
        Vector3[] posed = vertices.Select(v => Skin(v, palette)).ToArray();
        foreach (var p in posed) Inside(skin, p);
        var refined = producer.BoundRefinement(skin);
        foreach (var a in posed)
            foreach (var b in posed)
            {
                foreach (float t in new[] { 0, 1f / 3, .5f, 1 }) Inside(refined, Edge(a, b, t));
                Inside(refined, Interior(a, b, posed[0]));
            }

        var fixture = CoverageFixture.CurvedPanel(secondRegion: true);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var commands = CoverageCommands.Poses(fixture, topology, CoverageCommands.Plate(fixture), Matrix4x4.Identity);
        commands[0] = commands[0] with { MaxWorldRecession = 2 };
        commands[1] = commands[1] with { MaxWorldRecession = 3 };
        Matrix4x4 model = new(2, .7f, -.3f, 0, .2f, 3, .9f, 0, -.4f, .6f, 1, 0, 17, -9, 4, 1);
        var world = producer.ExpandWorld(skin, model, commands);
        var maxOnly = producer.ExpandWorld(skin, model, commands.AsSpan(1));
        Assert.Equal(maxOnly.Min, world.Min);
        Assert.Equal(maxOnly.Max, world.Max);
        foreach (var p in posed)
            foreach (var displacement in new[] { Vector3.Zero, Vector3.UnitX * 3, -Vector3.UnitY * 3, Vector3.UnitZ * 3 })
                InsideWorld(world, p, model, displacement);
        var relative = CoverageSurfaceBounds.FromWorld(world, new(16, -8, 4));
        Assert.Equal(new Vector3(16, -8, 4), relative.Origin);
        foreach (var p in posed) InsideWorld(relative.Relative, p, model, -relative.Origin);
    }

    [Fact]
    public void Nonnegative_host_equals_the_joined_form_instead_of_a_per_slot_sum()
    {
        // Each slot ranges from zero to one, but each vertex's total is one. H is the point (2,4,6).
        // The joined form plus gamma8 rounding gives these outward binary32 endpoints. A per-slot
        // matrix interval would admit totals up to four and cannot equal this box.
        var source = Source(Enumerable.Range(0, 4).Select(i =>
        {
            Vector4 weights = Vector4.Zero;
            weights[i] = 1;
            return Vertex(new(1, 2, 3), weights) with { BoneIndices = Vector4.Zero };
        }).ToArray());
        var actual = new CoverageActorBounds(source).BoundSkin([Matrix4x4.CreateTranslation(1, 2, 3)]);
        var joinedFormBound = new MeshBounds(new(Bits(0x3ffffff7), Bits(0x407ffff7), Bits(0x40bffff9)),
            new(Bits(0x40000005), Bits(0x40800005), Bits(0x40c00007)));
        Assert.Equal(joinedFormBound.Min, actual.Min);
        Assert.Equal(joinedFormBound.Max, actual.Max);
    }

    [Theory]
    [InlineData(-1, false, true)]
    [InlineData(0, false, false)]
    [InlineData(1, false, false)]
    [InlineData(-1, true, false)]
    public void Identity_cutoff_uses_the_rounded_total_and_a_strict_comparison(int neighbor, bool multiple, bool identity)
    {
        float first = Bits((uint)(0x322bcc77 + neighbor)); // 1e-8f and its immediate binary32 neighbors.
        float second = multiple ? 3 * MathF.ScaleB(1, -52) : 0;
        if (multiple)
        {
            Assert.True((double)first + second < 1e-8f);
            Assert.Equal(1e-8f, Add(first, second));
        }
        var vertex = Vertex(new(3, 5, 7), new(first, second, 0, 0)) with { BoneIndices = Vector4.Zero };
        Matrix4x4[] palette = [Matrix4x4.CreateTranslation(1e10f, 0, 0)];
        var actual = Skin(vertex, palette);
        var bound = new CoverageActorBounds(Source(vertex)).BoundSkin(palette);
        Inside(bound, actual);
        if (identity) Assert.Equal(vertex.Position, actual);
        else
        {
            Assert.InRange(actual.X, 99, 101);
            Assert.True(bound.Min.X > 99); // An erroneous identity join exposes the source point at X=3.
            Assert.InRange(bound.Max.X - bound.Min.X, 0, .001f);
        }
    }

    [Fact]
    public void World_envelope_is_finite_and_tight_under_scale_shear_and_recession()
    {
        var fixture = CoverageFixture.CurvedPanel();
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var command = CoverageCommands.Poses(fixture, topology, CoverageCommands.Plate(fixture), Matrix4x4.Identity)[0]
            with
        { MaxWorldRecession = 1 };
        var producer = new CoverageActorBounds(Source(Vertex(Vector3.Zero, Vector4.Zero)));
        Matrix4x4 model = new(2, .5f, 0, 0, 0, 1, .25f, 0, 0, 0, .5f, 0, 17, -9, 4, 1);
        var world = producer.ExpandWorld(new(Vector3.Zero, Vector3.Zero), model, [command]);
        for (int axis = 0; axis < 3; axis++)
        {
            Assert.True(float.IsFinite(world.Min[axis]) && float.IsFinite(world.Max[axis]));
            Assert.InRange(world.Max[axis] - world.Min[axis], 2, 3);
            Assert.InRange(world.Min[axis], model.Translation[axis] - 1.5f, model.Translation[axis]);
            Assert.InRange(world.Max[axis], model.Translation[axis], model.Translation[axis] + 1.5f);
        }
    }

    [Fact]
    public void Rounded_recession_and_final_model_dot_fit_the_world_envelope_for_an_admitted_ill_conditioned_actor()
    {
        var fixture = CoverageFixture.CurvedPanel();
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        Matrix4x4 model = new(1.3f, .7f, 0, 0, 2.6f, 1.4001f, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
        Matrix4x4 coverage = new(0, 1, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0, 0, 0, 1);
        var command = CoverageCommands.Poses(fixture, topology, CoverageCommands.Plate(fixture), Matrix4x4.Identity)[0]
            with
        { EquipmentWorld = coverage, CoverageWorld = coverage, MaxWorldRecession = 1, Clearance = 1 };
        var scratch = new CoverageReferenceScratch(topology);
        Assert.True(CoverageFrameAdmission.TryActor(model, out _));
        Assert.True(CoverageReferenceFrames.TryPrepare(topology, model, [command], scratch, out var failure), failure.ToString());
        var frame = scratch.Frames[0];
        Assert.Equal(new Vector3(10768.2138671875f, -5383.72216796875f, 0), frame.Recess);
        Assert.True(Mul(1, frame.ZScale) <= command.MaxWorldRecession);
        // Independent integer RN32 operations follow section 4's Conform and final point-dot graphs.
        Vector3 moved = new(Sub(0, Mul(1, frame.Recess.X)), Sub(0, Mul(1, frame.Recess.Y)), Sub(0, Mul(1, frame.Recess.Z)));
        var drawn = DrawPoint(moved, model);
        Assert.Equal(-1.0009765625f, drawn.X);
        var producer = new CoverageActorBounds(Source(Vertex(Vector3.Zero, Vector4.Zero)));
        var skin = new MeshBounds(Vector3.Zero, Vector3.Zero);
        Assert.True(producer.TryAdmitOrigin(skin, model, Vector3.Zero, [command], out _));
        var world = producer.ExpandWorld(skin, model, [command]);
        Inside(world, drawn);
        InsideWorld(world, moved, model, Vector3.Zero);
        for (int axis = 0; axis < 3; axis++)
        {
            Assert.True(float.IsFinite(world.Min[axis]) && float.IsFinite(world.Max[axis]));
            Assert.InRange(world.Max[axis] - world.Min[axis], 2, 3);
        }
    }

    [Fact]
    public void Rounding_envelope_includes_ties_signed_zero_and_the_inclusive_flush_threshold()
    {
        double tiny = Math.ScaleB(1d, -126);
        var flushed = CoverageActorBounds.Round32Envelope(new(-tiny, tiny));
        Assert.Equal(0x8000000000000000ul, BitConverter.DoubleToUInt64Bits(flushed.Min));
        Assert.Equal(0ul, BitConverter.DoubleToUInt64Bits(flushed.Max));
        float survives = MathF.BitIncrement((float)tiny);
        double tie = (tiny + survives) * .5;
        Assert.Equal(0, CoverageActorBounds.Round32Envelope(new(tie, tie)).Max);
        Assert.Equal(survives, CoverageActorBounds.Round32Envelope(new(Math.BitIncrement(tie), Math.BitIncrement(tie))).Max);
        Assert.Equal(-survives, CoverageActorBounds.Round32Envelope(new(-survives, -tiny)).Min);
        double halfway = 1d + Math.ScaleB(1d, -24);
        Assert.Equal(new CoverageScalarBounds(1, 1), CoverageActorBounds.Round32Envelope(new(halfway, halfway)));
        Assert.Equal((double)MathF.BitIncrement(1), CoverageActorBounds.Round32Envelope(new(halfway, Math.BitIncrement(halfway))).Max);
        Assert.False(CoverageActorBounds.Round32Envelope(new(double.MaxValue, double.MaxValue)).IsFinite);
        Assert.False(CoverageActorBounds.Round32Envelope(new(double.NaN, 1)).IsFinite);
    }

    [Fact]
    public void Coefficient_flush_error_is_propagated_through_large_source_coordinates()
    {
        // Each 2*eta/4 coefficient flushes to zero before the point multiply. Bounding only H*T
        // or adding an unscaled flush allowance would miss the actual zero by about 2.35e-8.
        var v = Vertex(new(1e30f, 0, 0), new(.25f)) with { BoneIndices = Vector4.Zero };
        Matrix4x4[] palette = [Matrix4x4.CreateScale(MathF.ScaleB(1, -125), 1, 1)];
        var actual = Skin(v, palette);
        Assert.Equal(Vector3.Zero, actual);
        Inside(new CoverageActorBounds(Source(v)).BoundSkin(palette), actual);
    }

    [Theory]
    [InlineData(124)]
    [InlineData(-126)]
    [InlineData(-130)]
    public void Refinement_gamma6_encloses_independent_positive_weight_graphs(int exponent)
    {
        float s = MathF.ScaleB(1, exponent);
        var producer = new CoverageActorBounds(Source(Vertex(Vector3.Zero, Vector4.Zero)));
        var skin = new MeshBounds(new(-s, -s, -s), new(s, s, s));
        var bound = producer.BoundRefinement(skin);
        Vector3 a = new(s, -s, s), b = new(-s, s, s), c = new(s, s, -s);
        foreach (float t in new[] { 0, 1f / 3, .5f, 1 }) Inside(bound, Edge(a, b, t));
        Inside(bound, Interior(a, b, c));
        Inside(bound, Interior(a, a, a));
        double u = Math.ScaleB(1d, -24), gamma6 = 6 * u / (1 - 6 * u);
        Assert.True((double)bound.Max.X >= s + gamma6 * s + 6 * Math.ScaleB(1d, -126));
        Assert.True((double)bound.Min.X <= -s - gamma6 * s - 6 * Math.ScaleB(1d, -126));
    }

    [Fact]
    public void Independent_source_skin_and_reference_final_surface_fit_the_same_world_bound()
    {
        var fixture = CoverageFixture.CurvedPanel();
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        Matrix4x4[] palette = [Matrix4x4.Identity, Matrix4x4.Identity];
        var posed = fixture.Source.SkinVertices.ToArray().Select(v => new ModelVertex(Skin(v, palette), v.Normal, v.Color, v.Uv, v.Tangent)).ToArray();
        Matrix4x4 model = new(1, .2f, 0, 0, .1f, 1, .3f, 0, 0, .1f, 1, 0, 32, -16, 8, 1);
        var commands = CoverageCommands.Poses(fixture, topology, CoverageCommands.Plate(fixture), model);
        var producer = new CoverageActorBounds(fixture.Source);
        var bound = producer.ExpandWorld(producer.BoundSkin(palette), model, commands);
        var final = new ModelVertex[topology.VertexCount];
        Assert.True(CoverageReferenceConstructor.TryConstruct(topology, posed, model, commands,
            new CoverageReferenceScratch(topology), final, out var failure), failure.ToString());
        foreach (var v in posed.Concat(final)) InsideWorld(bound, v.Position, model, Vector3.Zero);
    }

    [Fact]
    public void Nonfinite_palette_or_bound_refuses_and_warm_bounds_allocate_nothing()
    {
        var producer = new CoverageActorBounds(Source(Vertex(Vector3.One, new(1, 0, 0, 0)) with { BoneIndices = Vector4.Zero }));
        var fixture = CoverageFixture.CurvedPanel();
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var commands = CoverageCommands.Poses(fixture, topology, CoverageCommands.Plate(fixture), Matrix4x4.Identity);
        Matrix4x4[] palette = [Matrix4x4.Identity];
        var skin = producer.BoundSkin(palette);
        producer.ExpandWorld(skin, Matrix4x4.Identity, commands);
        producer.OriginError(skin, Matrix4x4.Identity, Vector3.Zero, commands);
        using var region = AllocationRegion.Start("warm actor bounds");
        long before = GC.GetAllocatedBytesForCurrentThread();
        var result = producer.ExpandWorld(producer.BoundSkin(palette), Matrix4x4.Identity, commands);
        double error = producer.OriginError(skin, Matrix4x4.Identity, Vector3.Zero, commands);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        region.Dispose();
        Assert.Equal(0, allocated);
        Assert.True(double.IsFinite(error));
        Inside(result, Vector3.One);
        Assert.False(float.IsFinite(producer.BoundSkin([Matrix4x4.Identity with { M41 = float.NaN }]).Max.X));
        Assert.False(double.IsFinite(producer.OriginError(new(new(float.NaN), Vector3.One), Matrix4x4.Identity, Vector3.Zero, [])));
    }

    internal static CoverageSourceTopology Source(params SkinnedVertex[] vertices) => new("actor-bound", CoverageFixture.Hash,
        vertices.Select(v => v.Position).ToArray(), vertices.Select((_, i) => new CoverageSourceVertex(0, 0, i, -1, 0, i)),
        Array.Empty<CoverageSourceTriangle>(), vertices);
    static SkinnedVertex Vertex(Vector3 p, Vector4 w) => new() { Position = p, BoneWeights = w, BoneIndices = new(0, 1, 0, 1) };
    static float Bits(uint bits) => BitConverter.UInt32BitsToSingle(bits);
    internal static void Inside(MeshBounds bound, Vector3 p)
    {
        for (int axis = 0; axis < 3; axis++) Assert.InRange(p[axis], bound.Min[axis], bound.Max[axis]);
    }
    static void InsideWorld(MeshBounds bound, Vector3 p, Matrix4x4 m, Vector3 displacement)
    {
        double[] actual = [p.X * (double)m.M11 + p.Y * (double)m.M21 + p.Z * (double)m.M31 + m.M41 + displacement.X,
            p.X * (double)m.M12 + p.Y * (double)m.M22 + p.Z * (double)m.M32 + m.M42 + displacement.Y,
            p.X * (double)m.M13 + p.Y * (double)m.M23 + p.Z * (double)m.M33 + m.M43 + displacement.Z];
        for (int axis = 0; axis < 3; axis++) Assert.InRange(actual[axis], bound.Min[axis], bound.Max[axis]);
    }

    // Independent test-only binary32 graph. Each operation uses integer RN32, then the inclusive F.
    internal static Vector3 Skin(SkinnedVertex v, Matrix4x4[] palette)
    {
        float total = Add(Add(Add(v.BoneWeights.X, v.BoneWeights.Y), v.BoneWeights.Z), v.BoneWeights.W);
        if (total < 1e-8f) return new(F(v.Position.X), F(v.Position.Y), F(v.Position.Z));
        float[] m = new float[12];
        for (int column = 0; column < 3; column++)
            for (int row = 0; row < 4; row++)
            {
                float sum = Mul(palette[(int)v.BoneIndices.X][row, column], v.BoneWeights.X);
                for (int slot = 1; slot < 4; slot++) sum = Add(sum, Mul(palette[(int)v.BoneIndices[slot]][row, column], v.BoneWeights[slot]));
                m[row * 3 + column] = sum;
            }
        return new(Add(Add(Add(Mul(v.Position.X, m[0]), Mul(v.Position.Y, m[3])), Mul(v.Position.Z, m[6])), m[9]),
            Add(Add(Add(Mul(v.Position.X, m[1]), Mul(v.Position.Y, m[4])), Mul(v.Position.Z, m[7])), m[10]),
            Add(Add(Add(Mul(v.Position.X, m[2]), Mul(v.Position.Y, m[5])), Mul(v.Position.Z, m[8])), m[11]));
    }
    internal static Vector3 DrawPoint(Vector3 p, Matrix4x4 m) => new(
        Add(Add(Add(Mul(p.X, m.M11), Mul(p.Y, m.M21)), Mul(p.Z, m.M31)), m.M41),
        Add(Add(Add(Mul(p.X, m.M12), Mul(p.Y, m.M22)), Mul(p.Z, m.M32)), m.M42),
        Add(Add(Add(Mul(p.X, m.M13), Mul(p.Y, m.M23)), Mul(p.Z, m.M33)), m.M43));
    // Positive integer barycentrics sum to 12. The edge and interior trees have at most six
    // rounding operations. Their relative terms are bounded by gamma6 times the largest endpoint,
    // and each F contributes at most 2^-126 beyond its relative rounding term, including equality.
    static Vector3 Edge(Vector3 a, Vector3 b, float t) => new(Add(Mul(a.X, Sub(1, t)), Mul(b.X, t)),
        Add(Mul(a.Y, Sub(1, t)), Mul(b.Y, t)), Add(Mul(a.Z, Sub(1, t)), Mul(b.Z, t)));
    static Vector3 Interior(Vector3 a, Vector3 b, Vector3 c) => new(
        Div(Add(Add(Mul(a.X, 1), Mul(b.X, 4)), Mul(c.X, 7)), 12),
        Div(Add(Add(Mul(a.Y, 1), Mul(b.Y, 4)), Mul(c.Y, 7)), 12),
        Div(Add(Add(Mul(a.Z, 1), Mul(b.Z, 4)), Mul(c.Z, 7)), 12));
    static float F(float f) => (BitConverter.SingleToUInt32Bits(f) & 0x7fffffff) <= 0x00800000
        ? Bits(BitConverter.SingleToUInt32Bits(f) & 0x80000000) : f;
    static BigInteger Signed(float f)
    {
        uint word = BitConverter.SingleToUInt32Bits(F(f));
        return (word & 0x80000000) == 0 ? CoverageBinary32IntegerOracle.Units(word) : -CoverageBinary32IntegerOracle.Units(word);
    }
    static float Add(float a, float b) => Round(Signed(a) + Signed(b), -149);
    static float Sub(float a, float b) => Round(Signed(a) - Signed(b), -149);
    static float Mul(float a, float b) => Round(Signed(a) * Signed(b), -298);
    static float Div(float a, float b) => F(Bits(CoverageBinary32IntegerOracle.Divide(BitConverter.SingleToUInt32Bits(F(a)), BitConverter.SingleToUInt32Bits(F(b)))));
    static float Round(BigInteger value, int exponent) => F(Bits(CoverageBinary32IntegerOracle.Round(BigInteger.Abs(value), exponent, value.Sign < 0 ? -1 : 1)));
}
