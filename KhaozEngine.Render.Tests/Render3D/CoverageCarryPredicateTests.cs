using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageCarryPredicateTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Direction_alignment_never_changes_CPU_or_GPU_position_publication(bool tinyAlignment)
    {
        float l = MathF.ScaleB(1, -30), e = MathF.ScaleB(1, tinyAlignment ? -40 : -31);
        Vector3[] positions = [Vector3.Zero, new(l, 0, 0), new(0, l, 0)];
        var source = new CoverageSourceTopology("predicate", CoverageFixture.Hash, positions,
            Enumerable.Range(0, 3).Select(i => new CoverageSourceVertex(0, 0, i, -1, 0, i)),
            [new(0, 0, 0, -1, 0, 0, 1, 2)],
            positions.Select(p => new SkinnedVertex
            {
                Position = p,
                Normal = Vector3.UnitZ,
                Color = Vector4.One,
                Tangent = new(1, 0, 0, -1),
                BoneWeights = new(1, 0, 0, 0)
            }).ToArray());
        var map = new HostCoverageMap(CoverageFixture.Hash,
            Enumerable.Range(0, 3).Select(i => new CoverageTopologyEntry(0, 0, i, i)),
            [new("predicate", [new(new(0, 0, 0, 1), CoverageTriangleRole.Collar)],
                Enumerable.Range(0, 3).Select(i => new CoverageSurfaceStrength(i, 0)))], [], new("synthetic", "predicate"));
        var topology = CoverageRefinement.Build(source, map);
        Assert.Equal(3, topology.VertexCount);
        var scratch = new CoverageCarryScratch(topology, 1);
        var before = positions.Select(p => new ModelVertex(p, Vector3.UnitZ, Vector4.One, Vector2.Zero, new(1, 0, 0, -1))).ToArray();
        var after = before.ToArray(); after[2].Position = new(0, e, l - e);
        before.CopyTo(scratch.BeforeVertices, 0); after.CopyTo(scratch.After, 0);
        bool accepted = CoverageReferenceNormals.TryUpdate(topology, scratch, out var failure);
        Assert.True(accepted);
        Assert.True(failure.IsSuccess);

        uint[] host = CoverageCarryUpload.Host(topology, 1);
        uint[] work = new uint[CoverageCarryLayout.WorkWords(1, 3, 3, 1)];
        CoverageCarryLayout.WorkHeader(topology).CopyTo(work, 0);
        uint[] beforeWords = MemoryMarshal.Cast<ModelVertex, uint>(before.AsSpan()).ToArray();
        uint[] afterWords = MemoryMarshal.Cast<ModelVertex, uint>(after.AsSpan()).ToArray();
        beforeWords.CopyTo(work, (int)work[4]); afterWords.CopyTo(work, (int)work[8]);
        afterWords.CopyTo(work, (int)work[10]); work[(int)work[44] + 2] = 1;
        uint[] frames = new uint[6 + CoverageCarryLayout.FrameWords(1)];
        frames[0] = 1; frames[1] = frames[2] = frames[3] = frames[4] = 64; frames[5] = 6;
        frames[11] = 3; frames[12] = 3; frames[13] = 1;
        uint[] flags = [uint.MaxValue, uint.MaxValue, uint.MaxValue, 0, 0, 0, 0, 0], current = new uint[48];
        var basis = CoverageCarryShaders.Kernels[2];
        new CoverageSpirvExecutor(basis.Source, basis.Name, new[] { host, frames, work, flags }).Run(3);
        var publish = CoverageCarryShaders.Kernels[4];
        new CoverageSpirvExecutor(publish.Source, publish.Name, new[] { host, frames, work, flags, current }).Run(3);
        Assert.Equal(accepted ? 0u : (uint)failure.Code, flags[6]);
        uint[] expected = accepted ? afterWords : beforeWords;
        for (int vertex = 0; vertex < 3; vertex++)
            Assert.Equal(expected.AsSpan(vertex * 16, 3).ToArray(), current.AsSpan(vertex * 16, 3).ToArray());
    }
    [Theory]
    [InlineData(-24, false)]
    [InlineData(-23, true)]
    public void Refinement_near_cancellation_matches_CPU_survival_decision(int exponent, bool accepted)
    {
        Vector3[] positions = [new(-1, 0, 0), new(1, 0, 0), new(0, 1, 0)];
        var skin = positions.Select(p => new SkinnedVertex
        {
            Position = p,
            Normal = Vector3.UnitX,
            Color = Vector4.One,
            BoneWeights = new(1, 0, 0, 0)
        }).ToArray();
        var source = new CoverageSourceTopology("refinement-predicate", CoverageFixture.Hash, positions,
            Enumerable.Range(0, 3).Select(i => new CoverageSourceVertex(0, 0, i, -1, 0, i)),
            [new(0, 0, 0, -1, 0, 0, 1, 2)], skin);
        var map = new HostCoverageMap(CoverageFixture.Hash,
            Enumerable.Range(0, 3).Select(i => new CoverageTopologyEntry(0, 0, i, i)),
            [new("predicate", [new(new(0, 0, 0, 1), CoverageTriangleRole.Collar)],
                Enumerable.Range(0, 3).Select(i => new CoverageSurfaceStrength(i, 0)))],
            [new(new(0, 0, 0, 1), 2)], new("synthetic", "predicate"));
        var topology = CoverageRefinement.Build(source, map);
        var posed = positions.Select(p => new ModelVertex(p, Vector3.UnitX, Vector4.One)).ToArray();
        posed[1].Normal = new(-1, MathF.ScaleB(1, exponent), 0);
        var scratch = new CoverageCarryScratch(topology, 1);
        bool cpuAccepted = CoverageCarryInterpolation.TryInterpolate(topology, posed, scratch, out var failure);
        Assert.True(cpuAccepted);
        Assert.True(failure.IsSuccess);
        int midpoint = Enumerable.Range(0, topology.VertexCount).Single(i => topology.Vertices[i] is { SourceVertex: -1, A: 6, B: 6, C: 0 });
        Assert.Equal(accepted ? Vector3.UnitY : Vector3.UnitX, scratch.BeforeVertices[midpoint].Normal);
        // Midpoint weights are (6,6,0). Squared surviving and total magnitudes equal the 2^-50 limit at h=2^-24.
        uint[] host = CoverageCarryUpload.Host(topology, 1);
        uint[] work = Enumerable.Repeat(CoverageCarryFailureFixture.Poison,
            CoverageCarryLayout.WorkWords(1, 3, topology.VertexCount, topology.TriangleCount)).ToArray();
        CoverageCarryLayout.WorkHeader(topology).CopyTo(work, 0);
        MemoryMarshal.Cast<ModelVertex, uint>(posed.AsSpan()).CopyTo(work.AsSpan((int)work[0]));
        uint[] frames = new uint[6 + CoverageCarryLayout.FrameWords(1)];
        frames[0] = 1; frames[1] = frames[2] = frames[3] = frames[4] = 64; frames[5] = 6;
        frames[11] = 3; frames[12] = (uint)topology.VertexCount; frames[13] = (uint)topology.TriangleCount; frames[21] = 1;
        uint[] flags = [uint.MaxValue, uint.MaxValue, uint.MaxValue, 0, 0, 0, 0, 0];
        uint[] table = Enumerable.Repeat(uint.MaxValue, topology.PositionGroupCount).ToArray(), current = new uint[topology.VertexCount * 16];
        var transport = CoverageCarryShaders.Kernels[1];
        new CoverageSpirvExecutor(transport.Source, transport.Name, new[] { host, frames, work, flags, table }).Run(topology.VertexCount);
        var publish = CoverageCarryShaders.Kernels[4];
        new CoverageSpirvExecutor(publish.Source, publish.Name, new[] { host, frames, work, flags, current }).Run(topology.VertexCount);
        Assert.Equal(cpuAccepted ? 0u : (uint)failure.Code, flags[6]);
        for (int vertex = 0; vertex < topology.VertexCount; vertex++)
        {
            var record = topology.Vertices[vertex];
            Vector3 expected = record.SourceVertex >= 0 ? posed[record.SourceVertex].Position : scratch.GroupPositions[record.PositionGroup];
            Assert.Equal(new[] { Bits(expected.X), Bits(expected.Y), Bits(expected.Z) }, current.AsSpan(vertex * 16, 3).ToArray());
        }
    }

    static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);

}
