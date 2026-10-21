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

    public static IEnumerable<object[]> AdversarialCases()
    {
        foreach (var phase in Enum.GetValues<CoverageGpuPhase>().Where(CoverageGpuSkinRefineGlsl.IsReducer))
        {
            CoverageReducerCase[] cases = CoverageReducerCase.For(phase);
            for (int i = 0; i < cases.Length; i++) yield return new object[] { phase.ToString(), i, cases[i].Name };
        }
    }

    [Theory]
    [MemberData(nameof(AdversarialCases))]
    public void Serial_reference_preserves_each_adversarial_outcome(string name, int index, string scenario)
    {
        var phase = Enum.Parse<CoverageGpuPhase>(name);
        var item = CoverageReducerCase.For(phase)[index];
        Assert.Equal(scenario, item.Name);
        var expected = CoverageReducerReference.Evaluate(phase, item);
        Assert.Equal(item.Code, expected.Code);
        Assert.Equal(item.Slot, expected.Slot);
        Assert.Equal(item.Matching, expected.Matching);
        var failure = expected.Failure("reducer-boundaries", 1, 10);
        Assert.Equal((CoverageFailureCode)item.Code, failure.Code);
        Assert.Equal((int)item.Matching, failure.Count);
        Assert.Equal(item.Code == 0, failure.IsSuccess);
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
