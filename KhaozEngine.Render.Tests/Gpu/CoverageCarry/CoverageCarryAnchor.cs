using System;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Owned anchor data, deliberately independent of scratch and temporal presentation storage.</summary>
internal sealed class CoverageCarryAnchor
{
    readonly float[] recession;
    readonly int[] representatives;
    internal CoverageRefinement Topology { get; }
    internal ReadOnlySpan<float> Recession => recession;
    internal ReadOnlySpan<int> Representatives => representatives;

    // E2 research candidates own a new scalar vector. They never modify the exact anchor or its constructor.
    internal CoverageCarryAnchor(CoverageCarryAnchor exact, ReadOnlySpan<float> candidate)
    {
        if (candidate.Length != exact.recession.Length) throw new ArgumentException("Incomplete candidate.", nameof(candidate));
        Topology = exact.Topology;
        recession = candidate.ToArray();
        representatives = (int[])exact.representatives.Clone();
    }

    internal CoverageCarryAnchor(CoverageRefinement topology, ReadOnlySpan<Matrix4x4> palette,
        ReadOnlySpan<CoverageCommandPose> commands)
    {
        Topology = topology;
        var scratch = new CoverageReferenceScratch(topology);
        var posed = new ModelVertex[topology.SourceVertexCount];
        if (!CoverageReferenceSkinning.TrySkin(scratch.Arithmetic, new(topology.Source, palette.Length),
            topology.Source, palette, posed, out var code)) throw new InvalidOperationException(code.ToString());
        var result = new ModelVertex[topology.VertexCount];
        if (!CoverageReferenceConstructor.TryConstruct(topology, posed, Matrix4x4.Identity, commands, scratch, result,
            out var failure)) throw new InvalidOperationException(failure.ToString());
        recession = (float[])scratch.FullRecessions.Clone();
        representatives = (int[])scratch.ActiveGroupRepresentatives.Clone();
    }
}
