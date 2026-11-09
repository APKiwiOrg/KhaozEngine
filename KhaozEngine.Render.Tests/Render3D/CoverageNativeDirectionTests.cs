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
    public void Native_unit_keeps_zero_floor_and_nonfinite_refusals(uint x, uint code)
    {
        // 2^-63 squared is exactly 2^-126, the CPU flush boundary. 2^-62 is the passing control.
        uint[] result = Execute([x, 0, 0]);
        Assert.Equal(code, result[0]);
        if (code == 0) CoverageDirectionAssertions.Equal(Vector3.UnitX, Direction(result));
        else Assert.Equal(new uint[] { code, 0, 0, 0 }, result);
    }

    [Fact]
    public void Native_unit_matches_the_independent_three_four_five_direction()
    {
        uint[] result = Execute([Bits(3), Bits(4), 0]);
        Assert.Equal(0u, result[0]);
        CoverageDirectionAssertions.Equal(new(.6f, .8f, 0), Direction(result));
    }

    [Fact]
    public void Direction_io_does_not_software_flush_the_smallest_normal_component()
    {
        uint[] result = Execute([0x00800000u, 0, Bits(1)]);
        Assert.Equal(0u, result[0]);
        Assert.Equal(0x00800000u, result[1]);
    }

    static uint[] Execute(uint[] input)
    {
        string source = """
            #version 450
            layout(local_size_x = 64) in;
            layout(set=0,binding=0) buffer WorkBuf { uint CovWorkWords[]; };
            """ + CoverageCarryUnitShader.Source + """
            void main() {
                if (gl_GlobalInvocationID.x != 0u) return;
                vec3 unit;
                uint code = covDirectionUnit(covDirectionRead3(0u),unit);
                covDirectionWrite3(4u,unit);
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
