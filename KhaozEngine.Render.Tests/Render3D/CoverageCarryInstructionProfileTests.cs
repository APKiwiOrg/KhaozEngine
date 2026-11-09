using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageCarryInstructionProfileTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [Trait("Category", "Diagnostic")]
    public void Body_size_carry_reports_executed_instructions_per_stage_and_helper(int entry)
    {
        var fixture = new CoverageRecessionBodySizeFixture(33, 24, 2, 103, true);
        var topology = fixture.Topology;
        uint[] host = CoverageCarryUpload.Host(topology, 1), table = CoverageCarryUpload.Table(fixture.Table);
        uint[] work = Enumerable.Repeat(CoverageCarryFailureFixture.Poison,
            CoverageCarryLayout.WorkWords(1, topology.SourceVertexCount, topology.VertexCount, topology.TriangleCount)).ToArray();
        CoverageCarryLayout.WorkHeader(topology).CopyTo(work, 0);
        uint[] result = [uint.MaxValue, uint.MaxValue, uint.MaxValue, 0, 0, 0, 0, 0];
        uint[] current = Enumerable.Repeat(CoverageCarryFailureFixture.Poison, topology.VertexCount * 16).ToArray();
        uint[] frames = new uint[6 + CoverageCarryLayout.FrameWords(1)];
        frames[0] = 1; frames[1] = (uint)CoverageCarryLayout.Round64(topology.SourceVertexCount);
        frames[2] = (uint)CoverageCarryLayout.Round64(topology.PositionGroupCount);
        frames[3] = (uint)CoverageCarryLayout.Round64(topology.VertexCount);
        frames[4] = (uint)CoverageCarryLayout.Round64(topology.TriangleCount); frames[5] = 6;
        // One identity palette, row zero or a raw hole. The selected row has one full-transition +Z command.
        const int f = 6;
        frames[f + 5] = (uint)topology.SourceVertexCount; frames[f + 6] = (uint)topology.VertexCount;
        frames[f + 7] = (uint)topology.TriangleCount; frames[f + 8] = (uint)topology.PositionGroupCount;
        frames[f + 11] = 1; frames[f + 12] = entry < 0 ? 0u : 1u; frames[f + 13] = 1;
        frames[f + 14] = (uint)(topology.PositionGroupCount * (entry + 1)); frames[f + 15] = entry < 0 ? 1u : 0u;
        frames[f + 16] = frames[f + 21] = frames[f + 26] = frames[f + 31] = BitConverter.SingleToUInt32Bits(1);
        if (entry >= 0) frames[f + 34] = frames[f + 35] = BitConverter.SingleToUInt32Bits(1);
        frames.AsSpan(f + 64, 32).Fill(uint.MaxValue);
        if (entry >= 0) frames[f + 64] = 0;
        output.WriteLine("COVERAGE_INSTRUCTION_FIXTURE=" + JsonSerializer.Serialize(fixture.Extents));
        output.WriteLine("Relative executed SPIR-V instruction counts, not GPU timing. Labels and control flow count once, vectors are not scalarized.");
        for (int stage = 0; stage < CoverageCarryShaders.StageCount; stage++)
        {
            var kernel = CoverageCarryShaders.Kernels[stage];
            uint[][] buffers = stage == 1 ? [host, frames, work, result, table]
                : stage == 4 ? [host, frames, work, result, current] : [host, frames, work, result];
            var executor = new CoverageSpirvExecutor(kernel.Source, kernel.Name, buffers);
            int extent = stage == 0 ? topology.SourceVertexCount : stage == 2 ? topology.TriangleCount : topology.VertexCount;
            executor.RunGroups((extent + 63) / 64);
            var profile = executor.InstructionProfile.Where(p => p.Calls > 0).OrderBy(p => p.Name).ToArray();
            Assert.NotEmpty(profile);
            output.WriteLine("COVERAGE_INSTRUCTION_STAGE=" + JsonSerializer.Serialize(new
            {
                stage = kernel.Name["CoverageCarry".Length..],
                entry,
                invocations = CoverageCarryLayout.Round64(extent),
                instructions = profile.Sum(p => p.Instructions),
                exactInstructions = profile.Sum(p => p.ExactInstructions),
                helpers = profile,
            }));
        }
        Assert.Equal(0u, result[6]);
        Assert.DoesNotContain(CoverageCarryFailureFixture.Poison, current);
        var expected = new ModelVertex[topology.VertexCount];
        Assert.True(CoverageCarryReference.TryCarry(topology, [Matrix4x4.Identity], fixture.Table, entry,
            entry < 0 ? [] : [new(0, Vector3.UnitZ, 1)], new(topology, 1), expected, out var failure), failure.ToString());
        Assert.Equal(MemoryMarshal.Cast<ModelVertex, uint>(expected.AsSpan()).ToArray(), current);
    }
}
