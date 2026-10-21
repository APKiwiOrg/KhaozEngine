using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageGpuReducerTests
{
    public static IEnumerable<object[]> Reducers() => Enum.GetValues<CoverageGpuPhase>()
        .Where(CoverageGpuSkinRefineGlsl.IsReducer).Select(p => new object[] { p.ToString() });

    [Fact]
    public void Every_adversarial_outcome_is_constructible_before_opening_a_device()
    {
        int count = 0;
        foreach (var phase in Enum.GetValues<CoverageGpuPhase>().Where(CoverageGpuSkinRefineGlsl.IsReducer))
            foreach (var item in CoverageReducerCase.For(phase))
            {
                var expected = item.ExpectedFailure("reducer-boundaries", 1, 10);
                Assert.Equal((CoverageFailureCode)item.Code, expected.Code);
                Assert.Equal((int)item.Matching, expected.Count);
                Assert.Equal(item.Code == 0, expected.IsSuccess);
                count++;
            }
        Assert.Equal(219, count);
    }

    [Theory]
    [MemberData(nameof(Reducers))]
    public void Reducer_executes_a_complete_workgroup_with_shared_memory_barriers(string name)
    {
        var phase = Enum.Parse<CoverageGpuPhase>(name);
        byte[] spirv = SpirvFrontEnd.ToSpirv(CoverageGpuSkinRefineGlsl.Source(phase), GpuShaderStages.Compute,
            name, new(PreciseMath: true));
        uint[] words = MemoryMarshal.Cast<byte, uint>(spirv).ToArray();
        uint[]? localSize = null;
        int barriers = 0;
        for (int offset = 5; offset < words.Length; offset += (int)(words[offset] >> 16))
        {
            uint opcode = words[offset] & 0xffff;
            if (opcode == 16 && words[offset + 2] == 17) localSize = words.AsSpan(offset + 3, 3).ToArray();
            if (opcode == 224) barriers++;
        }
        Assert.Equal(new uint[] { 64, 1, 1 }, localSize);
        Assert.True(barriers > 0, "The winner must wait for every lane's shared-memory contribution.");
        CoverageSpirvAccessAudit.Validate(spirv, name, constantPrivateArrays: true);
    }
}
