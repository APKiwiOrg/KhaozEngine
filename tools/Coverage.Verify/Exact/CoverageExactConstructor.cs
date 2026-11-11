using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tools.Coverage;

/// <summary>Offline per-pose conformation and correction with retained binary32 carry arithmetic. Single threaded.</summary>
public sealed class CoverageExactConstructor
{
    readonly CoverageRefinement topology;
    readonly Matrix4x4[] inverseBind;
    readonly CoverageCarryUniforms uniforms;
    readonly CoverageReferenceScratch scratch;

    /// <summary>Copies the complete inverse-bind palette and reserves scratch for this exact topology.</summary>
    public CoverageExactConstructor(CoverageRefinement topology, ReadOnlySpan<Matrix4x4> inverseBind)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inverseBind.Length);
        this.topology = topology;
        this.inverseBind = inverseBind.ToArray();
        uniforms = new(topology, inverseBind.Length);
        scratch = new(topology, inverseBind.Length);
    }

    /// <summary>Constructs a candidate, without certifying it. All outputs require exact lengths and disjoint storage.
    /// A refusal changes no caller output. Scalars are full unweighted recession values per position group.
    /// Representatives are refined vertex indices, or -1 for inactive groups. All outputs are copied and survive reuse.
    /// Validate the returned candidate separately with CoverageCertification.</summary>
    public bool TryConstruct(ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 model,
        ReadOnlySpan<CoverageCommandPose> commands, Span<ModelVertex> output,
        Span<float> fullRecessions, Span<int> representatives, out CoverageFailure failure)
    {
        if (output.Length != topology.VertexCount || fullRecessions.Length != topology.PositionGroupCount
            || representatives.Length != topology.PositionGroupCount)
            throw new ArgumentException("Exact construction requires complete geometry, scalar and representative outputs.");
        var outputBytes = MemoryMarshal.AsBytes(output);
        var scalarBytes = MemoryMarshal.AsBytes(fullRecessions);
        var representativeBytes = MemoryMarshal.AsBytes(representatives);
        if (outputBytes.Overlaps(scalarBytes) || outputBytes.Overlaps(representativeBytes) || scalarBytes.Overlaps(representativeBytes)
            || MemoryMarshal.AsBytes(palette).Overlaps(outputBytes) || MemoryMarshal.AsBytes(palette).Overlaps(scalarBytes)
            || MemoryMarshal.AsBytes(palette).Overlaps(representativeBytes))
            throw new ArgumentException("Exact construction inputs and outputs must use disjoint storage.");
        if (!uniforms.TryPrepare(palette, inverseBind, model, commands, out failure)
            || !CoverageReferenceFrames.TryPrepare(topology, model, commands, scratch, out failure)) return false;
        var carry = scratch.Carry;
        if (!CoverageReferenceSkinning.TrySkin(carry.Arithmetic, carry.Skin, topology.Source,
            uniforms.ComposedPalette, carry.PosedSource, out var code))
            return CoverageFrames.Refuse(topology, code, 0, 0, 0, 0, out failure);
        if (!CoverageCarryInterpolation.TryInterpolate(topology, carry.PosedSource, carry, out failure)
            || !CoverageReferenceConstructor.TryConformPrepared(topology, commands, scratch, out failure)
            || !CoverageReferenceCorrection.TryApplyPrepared(topology, commands, scratch, out failure)) return false;
        CoverageReferenceCorrection.ApplyTransition(topology, commands, scratch);
        if (!CoverageReferenceNormals.TryUpdate(topology, carry, out failure)) return false;
        carry.Published.AsSpan().CopyTo(output);
        scratch.FullRecessions.AsSpan().CopyTo(fullRecessions);
        scratch.ActiveGroupRepresentatives.AsSpan().CopyTo(representatives);
        return true;
    }
}
