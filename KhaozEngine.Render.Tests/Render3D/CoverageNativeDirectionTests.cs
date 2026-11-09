using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageNativeDirectionTests
{
    [Theory]
    [InlineData(0u, 8u)]
    [InlineData(0x20000000u, 8u)]
    [InlineData(0x20800000u, 0u)]
    [InlineData(0x7fc00000u, 4u)]
    [InlineData(0x7f800000u, 4u)]
    public void Output_unit_keeps_retained_zero_and_nonfinite_refusals(uint x, uint code)
    {
        // 2^-63 squared is exactly 2^-126, the CPU flush boundary. 2^-62 is the passing control.
        uint[] result = Execute([x, 0, 0]);
        Assert.Equal(code, result[0]);
        if (code == 0) CoverageDirectionAssertions.Equal(Vector3.UnitX, Direction(result));
        else Assert.Equal(new uint[] { code, 0, 0, 0 }, result);
    }

    [Fact]
    public void Multi_component_flush_boundary_matches_the_CPU_unit_decision()
    {
        var f = new CoverageReferenceArithmetic();
        Vector3 value = new(MathF.ScaleB(1, -63), MathF.ScaleB(1, -63), 0);
        float squared = CoverageReferenceMath.Dot3(f, value, value);
        Assert.Equal(0f, squared);
        uint cpuCode = f.Greater(f.Sqrt(squared), 0) ? 0u : 8u;
        uint[] result = Execute([Bits(value.X), Bits(value.Y), 0]);
        Assert.Equal(cpuCode, result[0]);
    }

    [Fact]
    public void Native_unit_matches_the_independent_three_four_five_direction()
    {
        uint[] result = Execute([Bits(3), Bits(4), 0]);
        Assert.Equal(0u, result[0]);
        CoverageDirectionAssertions.Equal(new(.6f, .8f, 0), Direction(result));
    }

    [Fact]
    public void Output_unit_does_not_software_flush_the_smallest_normal_component()
    {
        uint[] result = Execute([0x00800000u, 0, Bits(1)]);
        Assert.Equal(0u, result[0]);
        Assert.Equal(0x00800000u, result[1]);
    }

    [Fact]
    public void Ill_conditioned_skin_keeps_separate_rounding_even_when_fused_error_exceeds_the_output_bound()
    {
        float q = BitConverter.UInt32BitsToSingle(0x3f3504f3);
        float rounded = q * 4097;
        float fused = MathF.FusedMultiplyAdd(q, 4097, -rounded);
        Assert.True(Vector3.Normalize(new(fused, q, 0)).X > CoverageDirectionAssertions.ComponentTolerance);
        Matrix4x4 palette = new(4097, 1, 0, 0, -4097, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
        var body = new KhaozEngine.Tests.Gpu.CoverageRecessionBodySizeFixture(4, 4);
        Assert.True(new CoverageSkinOverflow(body.Source).Accepts([palette]));
        var fixture = new CoverageCarryFailureFixture();
        fixture.RawHole(illConditioned: true);
        Assert.Equal(0u, fixture.Errors[6]);
        Assert.Equal(fixture.Expected, fixture.Current);
    }

    static uint[] Execute(uint[] input)
    {
        string source = """
            #version 450
            layout(local_size_x = 64) in;
            layout(set=0,binding=0) buffer WorkBuf { uint CovWorkWords[]; };
            """ + CoverageCarryArithmeticShader.Source + CoverageCarryOutputDirectionShader.Source + """
            void main() {
                if (gl_GlobalInvocationID.x != 0u) return;
                vec3 unit;
                vec3 value = vec3(uintBitsToFloat(CovWorkWords[0]),uintBitsToFloat(CovWorkWords[1]),uintBitsToFloat(CovWorkWords[2]));
                uint code = covDirectionUnit(value,unit);
                CovWorkWords[4] = floatBitsToUint(unit.x); CovWorkWords[5] = floatBitsToUint(unit.y); CovWorkWords[6] = floatBitsToUint(unit.z);
                CovWorkWords[3] = code;
            }
            """;
        uint[] words = [.. input, uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue];
        new CoverageSpirvExecutor(source, "native unit", new[] { words }).Run(1);
        return words[3..];
    }

    static Vector3 Direction(uint[] words) => new(BitConverter.UInt32BitsToSingle(words[1]),
        BitConverter.UInt32BitsToSingle(words[2]), BitConverter.UInt32BitsToSingle(words[3]));
    static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);
}
