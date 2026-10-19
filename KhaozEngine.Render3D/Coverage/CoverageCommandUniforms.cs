using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Actor-owned palette and command preparation without reference construction scratch or vertex arrays.</summary>
internal sealed class CoverageCommandUniforms
{
    readonly CoverageRefinement topology;
    readonly Matrix4x4[] composedPalette;
    readonly CoverageReferenceFrame[] frames = new CoverageReferenceFrame[CoverageScratch.MaxCommands];
    readonly int[] order = new int[CoverageScratch.MaxCommands];
    readonly int[] regionCommands = new int[HostCoverageMap.MaxRegions];
    readonly CoverageReferenceArithmetic uniformArithmetic = new();
    int count;
    bool prepared;

    internal CoverageCommandUniforms(CoverageRefinement topology, int boneCount)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(boneCount);
        _ = CoverageGpuLayout.FrameWords(boneCount);
        this.topology = topology;
        composedPalette = new Matrix4x4[boneCount];
        Array.Fill(regionCommands, -1);
    }

    internal ReadOnlySpan<Matrix4x4> ComposedPalette => prepared ? composedPalette : ReadOnlySpan<Matrix4x4>.Empty;
    internal ReadOnlySpan<CoverageReferenceFrame> Frames => frames.AsSpan(0, count);
    internal ReadOnlySpan<int> Order => order.AsSpan(0, count);
    internal ReadOnlySpan<int> RegionCommands => regionCommands;

    internal bool TryPrepare(ReadOnlySpan<Matrix4x4> palette, ReadOnlySpan<Matrix4x4> inverseBind, in Matrix4x4 model,
        ReadOnlySpan<CoverageCommandPose> commands, out CoverageFailure failure)
    {
        prepared = false; count = 0;
        Array.Clear(frames); Array.Clear(order); Array.Fill(regionCommands, -1);
        if (palette.Length != composedPalette.Length || inverseBind.Length != composedPalette.Length)
            return CoverageFrames.Refuse(topology, CoverageFailureCode.IdentityMismatch, 0, 0, 0, 0, out failure);
        if (!CoverageReferenceFrames.TryPrepare(topology, model, commands, order, regionCommands, frames, uniformArithmetic, out failure))
        {
            Array.Fill(regionCommands, -1);
            return false;
        }
        CoverageReferenceSkinning.Compose(palette, inverseBind, composedPalette);
        count = commands.Length; prepared = true;
        return true;
    }
}
