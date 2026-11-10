using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageDirectionRecoveryTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public void CPU_and_shader_use_candidate_then_uncovered_then_authored_then_fixed(int branch, bool tangent)
    {
        Vector3 candidate = branch == 0 ? Vector3.UnitY : new(float.NaN);
        Vector3 before = branch <= 1 ? -Vector3.UnitX : new(float.PositiveInfinity);
        Vector3 authored = branch <= 2 ? -Vector3.UnitZ : new(float.NaN);
        Vector3 expected = branch == 0 ? Vector3.UnitY : branch == 1 ? -Vector3.UnitX : branch == 2 ? -Vector3.UnitZ : tangent ? Vector3.UnitX : Vector3.UnitZ;
        Assert.Equal(expected, CoverageDirectionRecovery.Resolve(candidate, branch == 0, before, authored, tangent));
        string source = """
            #version 450
            layout(local_size_x = 64) in;
            layout(set=0,binding=0) readonly buffer InputBuf { uint Inputs[]; };
            layout(set=0,binding=1) buffer OutputBuf { uint Outputs[]; };
            """ + CoverageDirectionRecovery.ShaderSource + """
            void main() {
                if (gl_GlobalInvocationID.x != 0u) return;
                vec3 a = vec3(uintBitsToFloat(Inputs[0]),uintBitsToFloat(Inputs[1]),uintBitsToFloat(Inputs[2]));
                vec3 b = vec3(uintBitsToFloat(Inputs[3]),uintBitsToFloat(Inputs[4]),uintBitsToFloat(Inputs[5]));
                vec3 c = vec3(uintBitsToFloat(Inputs[6]),uintBitsToFloat(Inputs[7]),uintBitsToFloat(Inputs[8]));
                vec3 result = covRecoverDirection(a,Inputs[9] != 0u,b,c,Inputs[10] != 0u);
                Outputs[0] = floatBitsToUint(result.x); Outputs[1] = floatBitsToUint(result.y); Outputs[2] = floatBitsToUint(result.z);
            }
            """;
        uint[] input = new[] { candidate.X, candidate.Y, candidate.Z, before.X, before.Y, before.Z, authored.X, authored.Y, authored.Z }
            .Select(BitConverter.SingleToUInt32Bits).Concat(new uint[] { branch == 0 ? 1u : 0u, tangent ? 1u : 0u }).ToArray();
        uint[] result = new uint[3];
        new CoverageSpirvExecutor(source, "direction recovery chain", new[] { input, result }).Run(1);
        Assert.Equal(new[] { expected.X, expected.Y, expected.Z }.Select(BitConverter.SingleToUInt32Bits), result);
    }
}
