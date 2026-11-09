using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoveragePreciseExecutionTests
{
    [Fact]
    public void Frozen_version_one_scalar_words_execute_in_the_precise_module()
    {
        string source = """
            #version 450
            layout(local_size_x = 64) in;
            layout(set=0,binding=0) readonly buffer InputBuf { uint Inputs[]; };
            layout(set=0,binding=1) buffer OutputBuf { uint Outputs[]; };
            """ + CoverageCarryArithmeticShader.Source + CoverageCarryScalarShader.Source + """
            void main() {
                if (gl_GlobalInvocationID.x != 0u) return;
                Outputs[0] = floatBitsToUint(covRead(Inputs[0]));
                Outputs[1] = floatBitsToUint(covRead(Inputs[1]));
                Outputs[2] = floatBitsToUint(covRead(Inputs[2]));
                Outputs[3] = floatBitsToUint(covMul(uintBitsToFloat(Inputs[0]),uintBitsToFloat(Inputs[3])));
                Outputs[4] = floatBitsToUint(covMul(uintBitsToFloat(Inputs[4]),uintBitsToFloat(Inputs[5])));
                Outputs[5] = floatBitsToUint(covSub(uintBitsToFloat(Inputs[6]),covMul(uintBitsToFloat(Inputs[7]),uintBitsToFloat(Inputs[7]))));
                uint iterations;
                Outputs[6] = covRoundOperation(uintBitsToFloat(Inputs[8]),uintBitsToFloat(Inputs[9]),false,false,0u,iterations);
                Outputs[7] = covRoundOperation(uintBitsToFloat(Inputs[3]),0.0,true,false,0u,iterations);
            }
            """;
        uint[] inputs = [0x00800000, 0x80800000, 0x00800001, 0x40000000, 0xa0000000,
            0x20000000, 0x4b800000, 0x45800800, 0x3f800000, 0x40400000];
        uint[] result = new uint[8];
        byte[] module = SpirvFrontEnd.ToSpirv(source, GpuShaderStages.Compute, "frozen scalar execution", CoverageCarryShaders.Options);
        new CoverageSpirvExecutor(module, new[] { inputs, result }).Run(1);
        // Same independently authored words as the version-one CPU contract, never refreshed from GPU output.
        Assert.Equal(new uint[] { 0, 0x80000000, 0x00800001, 0, 0x80000000, 0xc6000000, 0x3eaaaaab, 0x3fb504f3 }, result);
    }

    [Fact]
    public void Precise_arithmetic_inventory_executes_the_existing_frozen_guards_and_reference_words()
    {
        uint[] inputs = CoverageGpuArithmeticProbe.Inputs();
        uint[] expected = CoverageGpuArithmeticProbe.Reference(inputs), result = new uint[expected.Length];
        byte[] module = SpirvFrontEnd.ToSpirv(CoverageGpuArithmeticProbe.Source, GpuShaderStages.Compute,
            "arithmetic inventory execution", CoverageCarryShaders.Options);
        new CoverageSpirvExecutor(module, new[] { inputs, result }).RunGroups(checked((int)(inputs[0] + 63) / 64));
        CoverageGpuArithmeticTests.AssertPinnedWords(inputs, result);
        Assert.NotEmpty(result);
        for (int i = 0; i < expected.Length; i++)
            if (float.IsNaN(BitConverter.UInt32BitsToSingle(expected[i])))
                Assert.True(float.IsNaN(BitConverter.UInt32BitsToSingle(result[i])));
            else Assert.Equal(expected[i], result[i]);
    }
}
