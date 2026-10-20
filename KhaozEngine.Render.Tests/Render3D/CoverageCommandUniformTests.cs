using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class CoverageCommandUniformTests(ITestOutputHelper output)
{
    [Fact]
    public void Cold_uploads_and_uniforms_preserve_reference_words_and_rows()
    {
        var fixture = CoverageFixture.CurvedPanel(secondRegion: true);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var commands = CoverageCommands.Poses(fixture, topology, CoverageCommands.Plate(fixture), Matrix4x4.Identity);
        commands[0] = commands[0] with { Id = 20 };
        commands[1] = commands[1] with { Id = 10 };
        var uniforms = new CoverageCommandUniforms(topology, 2);
        Matrix4x4 a = new(1.1f, -.2f, .3f, 0, .4f, 1.2f, -.5f, 0, -.6f, .7f, .9f, 0, 123.4f, -23.5f, 45.6f, 1);
        Matrix4x4 b = new(.8f, .3f, -.4f, 0, -.2f, 1.3f, .6f, 0, .5f, -.7f, 1.1f, 0, -87.6f, 54.3f, -21.2f, 1);
        Assert.True(uniforms.TryPrepare([a, b], [b, a], Matrix4x4.Identity, commands, out var failure), failure.ToString());
        Assert.Equal(new[] { 1, 0 }, uniforms.Order.ToArray());
        Assert.Equal(new[] { 1, 0 }, uniforms.RegionCommands[..2].ToArray());
        Assert.All(uniforms.RegionCommands[2..].ToArray(), x => Assert.Equal(-1, x));
        uint[] referencePaletteWords = [.. Product(a, b), .. Product(b, a)];
        uint[] uploadedPaletteWords = uniforms.ComposedPalette.ToArray().SelectMany(m =>
        {
            uint[] words = new uint[16];
            CoverageGpuUpload.WriteMatrix(m, words);
            return words;
        }).ToArray();
        Assert.Equal(referencePaletteWords, uploadedPaletteWords);
        output.WriteLine("palette=" + string.Join(",", uploadedPaletteWords.Select(w => w.ToString("x8"))));
        var scratch = new CoverageReferenceScratch(topology);
        Assert.True(CoverageReferenceFrames.TryPrepare(topology, Matrix4x4.Identity, commands, scratch, out _));
        for (int i = 0; i < commands.Length; i++)
        {
            var frame = uniforms.Frames[i];
            Assert.Equal(FrameWords(scratch.Frames[i]), FrameWords(frame));
            uint[] packed = new uint[80];
            CoverageGpuUpload.WriteCommand(frame, commands[frame.Command], 123, packed);
            Assert.Equal(FrameWords(frame), packed[..56]);
            Assert.Equal((uint)frame.Command, packed[56]);
            Assert.Equal(123u, packed[67]);
            Assert.Equal(new uint[12], packed[68..]);
        }
    }

    [Fact]
    public void Frame_words_match_explicit_stored_scalar_expectations()
    {
        var fixture = CoverageFixture.CurvedPanel();
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var points = fixture.RigidPositions.Select((p, i) => p with { Z = i < 5 ? 0 : 1 }).ToArray();
        var support = CoverageSupportSurface.Compile(CoverageCommands.Solid("flat-uniform", points, fixture.RigidIndices),
            [new(0, 0, 0, 4)], Matrix4x4.Identity);
        Matrix4x4 model = new(2, 0, 0, 0, 0, 4, 0, 0, 0, 0, 8, 0, 3, 5, 7, 1);
        var command = new CoverageCommandPose(10, 0x123456789abcdef0, topology, 0, support, Matrix4x4.Identity,
            Matrix4x4.Identity, -2, 3, .5f, .125f, 4, .25f);
        var uniforms = new CoverageCommandUniforms(topology, 1);
        Assert.True(uniforms.TryPrepare([Matrix4x4.Identity], [Matrix4x4.Identity], model, [command], out var failure), failure.ToString());
        uint[] expected = [.. Words(model), .. Words(model), .. Words(model with { M41 = 0, M42 = 0, M43 = 0 }),
            0, 0, 0x3e000000, 0x3f800000, 0x3e000000, 0x3e200000, 0x3e200000, 0x3f800000,
            0, 0xc0000000, 0x40400000, 0x3f000000, 0x3e000000, 0x40800000, 0x3e800000, 10,
            0x9abcdef0, 0x12345678, 0, 91, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        uint[] actual = new uint[80];
        CoverageGpuUpload.WriteCommand(uniforms.Frames[0], command, 91, actual);
        Assert.Equal(expected, actual);
        output.WriteLine("frame=" + string.Join(",", actual.Select(w => w.ToString("x8"))));
    }

    [Fact]
    public void Every_frozen_case_prepares_reference_frames_and_independent_integer_palette_products()
    {
        foreach (var c in CoverageReferenceCases.All())
        {
            var uniforms = new CoverageCommandUniforms(c.Topology, c.Palette.Length);
            Assert.True(uniforms.TryPrepare(c.Palette, c.InverseBind, c.Model, c.Commands, out var failure), failure.ToString());
            Assert.Equal(c.Palette.SelectMany((m, i) => Product(m, c.InverseBind[i])), uniforms.ComposedPalette.ToArray().SelectMany(Words));
            var scratch = new CoverageReferenceScratch(c.Topology);
            Assert.True(CoverageReferenceFrames.TryPrepare(c.Topology, c.Model, c.Commands, scratch, out _));
            Assert.Equal(scratch.Order[..c.Commands.Length], uniforms.Order.ToArray());
            for (int slot = 0; slot < c.Commands.Length; slot++) Assert.Equal(FrameWords(scratch.Frames[slot]), FrameWords(uniforms.Frames[slot]));
        }
    }

    [Fact]
    public void Admission_failures_clear_published_spans_and_warm_preparation_allocates_nothing()
    {
        var fixture = CoverageFixture.CurvedPanel();
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var commands = CoverageCommands.Poses(fixture, topology, CoverageCommands.Plate(fixture), Matrix4x4.Identity);
        Matrix4x4[] palette = [Matrix4x4.Identity, Matrix4x4.Identity];
        var uniforms = new CoverageCommandUniforms(topology, 2);
        Assert.True(uniforms.TryPrepare(palette, palette, Matrix4x4.Identity, commands, out _));
        using var region = AllocationRegion.Start("warm command uniform preparation");
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool accepted = uniforms.TryPrepare(palette, palette, Matrix4x4.Identity, commands, out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        region.Dispose();
        Assert.True(accepted); Assert.Equal(0, allocated);
        Assert.False(uniforms.TryPrepare(palette[..1], palette, Matrix4x4.Identity, commands, out var failure));
        Assert.Equal(CoverageFailureCode.IdentityMismatch, failure.Code);
        Assert.True(uniforms.Frames.IsEmpty); Assert.True(uniforms.Order.IsEmpty); Assert.True(uniforms.ComposedPalette.IsEmpty);
        Assert.False(uniforms.TryPrepare(palette, palette, Matrix4x4.Identity, [commands[0] with { Id = 0 }], out failure));
        Assert.Equal(CoverageFailureCode.IdentityMismatch, failure.Code);
        Assert.True(uniforms.Frames.IsEmpty);
        Assert.True(uniforms.TryPrepare(palette, palette, Matrix4x4.Identity, [], out _));
        Assert.True(uniforms.Frames.IsEmpty);
        Assert.All(uniforms.RegionCommands.ToArray(), x => Assert.Equal(-1, x));
    }

    [Fact]
    public void Scratch_independent_admission_preserves_refusal_codes_and_capacity()
    {
        var fixture = CoverageFixture.CurvedPanel();
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var command = CoverageCommands.Poses(fixture, topology, CoverageCommands.Plate(fixture), Matrix4x4.Identity)[0];
        Matrix4x4[] palette = [Matrix4x4.Identity, Matrix4x4.Identity];
        var uniforms = new CoverageCommandUniforms(topology, 2);
        (CoverageCommandPose[] Commands, Matrix4x4 Model, CoverageFailureCode Code)[] cases = [
            ([command with { Id = 0 }], Matrix4x4.Identity, CoverageFailureCode.IdentityMismatch),
            ([command with { RegionIndex = 1 }], Matrix4x4.Identity, CoverageFailureCode.UnknownRegion),
            ([command, command with { Id = 2 }], Matrix4x4.Identity, CoverageFailureCode.OverlappingConformers),
            ([command with { Clearance = -1 }], Matrix4x4.Identity, CoverageFailureCode.UnsupportedSupport),
            ([command with { CoverageWorld = Matrix4x4.CreateTranslation(1, 0, 0) }], Matrix4x4.Identity, CoverageFailureCode.StaleFrame),
            ([command], Matrix4x4.CreateScale(-1), CoverageFailureCode.UnsupportedTransform),
            ([command, command, command, command, command], Matrix4x4.Identity, CoverageFailureCode.CapacityExceeded)];
        foreach (var c in cases)
        {
            Assert.False(uniforms.TryPrepare(palette, palette, c.Model, c.Commands, out var failure));
            Assert.Equal(c.Code, failure.Code);
            Assert.True(uniforms.Frames.IsEmpty); Assert.True(uniforms.Order.IsEmpty);
            Assert.All(uniforms.RegionCommands.ToArray(), x => Assert.Equal(-1, x));
        }
        Assert.False(CoverageReferenceFrames.TryPrepare(topology, Matrix4x4.Identity, [command], new int[4], new int[32],
            Span<CoverageReferenceFrame>.Empty, new(), out var capacity));
        Assert.Equal(CoverageFailureCode.CapacityExceeded, capacity.Code);
        Assert.Equal((1, 0), (capacity.Count, capacity.Limit));
        Assert.False(CoverageReferenceFrames.TryPrepare(topology, Matrix4x4.Identity, [command], new int[4], Span<int>.Empty,
            new CoverageReferenceFrame[4], new(), out capacity));
        Assert.Equal(CoverageFailureCode.CapacityExceeded, capacity.Code);
    }

    static uint[] FrameWords(CoverageReferenceFrame f) => [.. Words(f.HostToCoverage), .. Words(f.HostToActualSheet), .. Words(f.ActorLinear),
        .. new[] { f.Recess.X, f.Recess.Y, f.Recess.Z, f.ZScale, f.CapMargin, f.ClearanceCoverage, f.ActualClearanceCoverage, f.CorrectionRate }.Select(BitConverter.SingleToUInt32Bits)];
    static uint[] Words(Matrix4x4 m) => new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 }.Select(BitConverter.SingleToUInt32Bits).ToArray();

    // Every product and sum is rounded by test-only BigInteger arithmetic, independent of the CPU's float instructions.
    static uint[] Product(Matrix4x4 joint, Matrix4x4 inverse)
    {
        uint[] a = Words(inverse), b = Words(joint), result = new uint[16];
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
            {
                uint value = Mul(a[r * 4], b[c]);
                for (int k = 1; k < 4; k++) value = Add(value, Mul(a[r * 4 + k], b[k * 4 + c]));
                result[r * 4 + c] = value;
            }
        return result;
    }
    static BigInteger Signed(uint w) => (w & 0x80000000) == 0 ? CoverageBinary32IntegerOracle.Units(w) : -CoverageBinary32IntegerOracle.Units(w);
    static uint Mul(uint a, uint b) => Round(Signed(a) * Signed(b), -298, (a ^ b) >> 31 != 0);
    static uint Add(uint a, uint b) => Round(Signed(a) + Signed(b), -149, a == 0x80000000 && b == 0x80000000);
    static uint Round(BigInteger n, int exponent, bool negativeZero) => CoverageBinary32IntegerOracle.Round(BigInteger.Abs(n), exponent,
        n.Sign < 0 || n.IsZero && negativeZero ? -1 : 1);
}
