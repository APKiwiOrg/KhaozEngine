using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using Xunit;
using Xunit.Sdk;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class CoverageGpuArithmeticTests
{
    [Theory]
    [InlineData("", "Results[d] = floatBitsToUint(a[i % 3u]);", "OpAccessChain")]
    [InlineData("", "a[i % 3u] = b.x; Results[d] = floatBitsToUint(a.x);", "OpAccessChain")]
    [InlineData("", "Results[d] = floatBitsToUint(m[i % 4u][0]);", "OpAccessChain")]
    [InlineData("", "Results[d] = floatBitsToUint(m[0][i % 4u]);", "OpAccessChain")]
    [InlineData("vec3 privateVector;", "privateVector = a; Results[d] = floatBitsToUint(privateVector[i % 3u]);", "OpAccessChain")]
    [InlineData("mat4 privateMatrix;", "privateMatrix = m; Results[d] = floatBitsToUint(privateMatrix[i % 4u][0]);", "OpAccessChain")]
    [InlineData("", "vec3 v[2]; v[0] = a; v[1] = b; Results[d] = floatBitsToUint(v[i % 2u][i % 3u]);", "OpAccessChain")]
    [InlineData("", "Results[d] = floatBitsToUint(covCross(a, b)[i % 3u]);", "OpVectorExtractDynamic")]
    public void Coverage_audit_rejects_dynamic_component_access(string globals, string operation, string opcode)
    {
        string source = CoverageGpuArithmeticProbe.Source.Replace("void main()", globals + "\nvoid main()", StringComparison.Ordinal)
            .Replace("Results[d] = floatBitsToUint(covF(a.x));", operation, StringComparison.Ordinal);
        byte[] module = SpirvFrontEnd.ToSpirv(source, GpuShaderStages.Compute, "coverage dynamic component control", Precise);
        XunitException error = Assert.ThrowsAny<XunitException>(() => CoverageArithmeticSpirvAudit.MissingSites(module, output));
        Assert.Contains(opcode, error.Message);
    }

    [Fact]
    public void Coverage_audit_rejects_in_bounds_dynamic_component_access()
    {
        string source = CoverageGpuArithmeticProbe.Source.Replace("Results[d] = floatBitsToUint(covF(a.x));",
            "Results[d] = floatBitsToUint(a[i % 3u]);", StringComparison.Ordinal);
        byte[] module = SpirvFrontEnd.ToSpirv(source, GpuShaderStages.Compute, "coverage in-bounds control", Precise);
        Span<uint> words = MemoryMarshal.Cast<byte, uint>(module.AsSpan());
        for (int offset = 5; offset < words.Length; offset += (int)(words[offset] >> 16))
            if ((words[offset] & 0xffff) == 65) words[offset] = (words[offset] & 0xffff0000) | 66;
        XunitException error = Assert.ThrowsAny<XunitException>(() => CoverageSpirvAccessAudit.Validate(module, "in-bounds control"));
        Assert.Contains("OpInBoundsAccessChain", error.Message);
    }

    [Fact]
    public void Coverage_audit_allows_dynamic_array_indices_and_constant_components()
    {
        string source = CoverageGpuArithmeticProbe.Source.Replace("Results[d] = floatBitsToUint(covF(a.x));", """
            float scalars[2]; scalars[0] = a.x; scalars[1] = b.x;
            vec3 vectors[2]; vectors[0] = a; vectors[1] = b;
            mat4 matrices[2]; matrices[0] = m; matrices[1] = m;
            Results[d] = floatBitsToUint(scalars[i % 2u]);
            Results[d+1u] = floatBitsToUint(vectors[i % 2u].y);
            Results[d+2u] = floatBitsToUint(matrices[i % 2u][1][2]);
            """, StringComparison.Ordinal);
        byte[] module = SpirvFrontEnd.ToSpirv(source, GpuShaderStages.Compute, "coverage dynamic arrays control", Precise);
        Assert.Equal(0, CoverageArithmeticSpirvAudit.MissingSites(module, output));
    }

    [Theory]
    [InlineData(77u, "OpVectorExtractDynamic")]
    [InlineData(78u, "OpVectorInsertDynamic")]
    public void Coverage_audit_rejects_dynamic_vector_instructions(uint opcode, string name)
    {
        // Complete SPIR-V 1.0 compute modules. The index is an OpIAdd result, not an OpConstant.
        var words = new List<uint> { 0x07230203, 0x00010000, 0, 14, 0 };
        void Op(uint code, params uint[] operands)
        {
            words.Add((uint)(operands.Length + 1) << 16 | code);
            words.AddRange(operands);
        }
        Op(17, 1); // Shader capability
        Op(14, 0, 1); // Logical, GLSL450
        Op(15, 5, 10, 0x6e69616d, 0); // GLCompute, main
        Op(16, 10, 17, 1, 1, 1); // LocalSize
        Op(19, 1); // void
        Op(33, 2, 1); // function type
        Op(22, 3, 32); // float
        Op(21, 4, 32, 0); // uint
        Op(23, 5, 3, 3); // vec3
        Op(43, 3, 6, 0x3f800000); // 1.0
        Op(44, 5, 7, 6, 6, 6); // vec3(1.0)
        Op(43, 4, 8, 0); // 0u
        Op(54, 1, 10, 0, 2); // function
        Op(248, 11); // label
        Op(128, 4, 12, 8, 8); // index = 0u + 0u
        if (opcode == 77) Op(opcode, 3, 13, 7, 12);
        else Op(opcode, 5, 13, 7, 6, 12);
        Op(253); // return
        Op(56); // function end
        byte[] module = MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(words)).ToArray();
        XunitException error = Assert.ThrowsAny<XunitException>(() => CoverageArithmeticSpirvAudit.MissingSites(module, output));
        Assert.Contains(name, error.Message);
    }
}
