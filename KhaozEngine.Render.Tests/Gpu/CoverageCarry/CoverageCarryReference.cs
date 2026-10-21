using System;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Research-only output. It has no certificate and is never a drawable coverage frame.</summary>
internal static class CoverageCarryReference
{
    internal const string Label = "uncertified cost floor";

    internal static bool TryCarry(CoverageRefinement topology, ReadOnlySpan<Matrix4x4> palette,
        ReadOnlySpan<CoverageCommandPose> commands, CoverageCarryAnchor anchor, CoverageReferenceScratch scratch,
        Span<ModelVertex> output, out CoverageFailure failure)
    {
        if (!ReferenceEquals(topology, anchor.Topology) || output.Length != topology.VertexCount)
            throw new ArgumentException("Carry requires the anchor topology and complete output storage.");
        var posed = new ModelVertex[topology.SourceVertexCount];
        var f = scratch.Arithmetic;
        f.Reset();
        if (!CoverageReferenceSkinning.TrySkin(f, new(topology.Source, palette.Length), topology.Source, palette, posed, out var code))
        { failure = new(code, topology.HostIdentity, 0, 0, 0, 0); return false; }
        if (!CoverageReferenceFrames.TryPrepare(topology, Matrix4x4.Identity, commands, scratch, out failure)
            || !CoverageReferenceConstructor.TryInterpolate(topology, posed, scratch, out failure)) return false;
        anchor.Recession.CopyTo(scratch.FullRecessions);
        anchor.Representatives.CopyTo(scratch.ActiveGroupRepresentatives);
        scratch.Before.CopyTo(scratch.After, 0);
        for (int g = 0; g < topology.PositionGroupCount; g++)
        {
            int representative = anchor.Representatives[g];
            if (representative < 0) continue;
            int slot = scratch.RegionCommands[topology.Vertices[representative].RegionIndex];
            if (slot < 0) { scratch.ActiveGroupRepresentatives[g] = -1; continue; }
            var frame = scratch.Frames[slot];
            float weight = commands[frame.Command].TransitionWeight;
            float full = f.Read(anchor.Recession[g]);
            var before = scratch.Before[representative].Position;
            // Zero scalar and zero weight are bit copies. All active nonzero aliases use the same representative.
            if (f.Equal(full, 0) || f.Equal(weight, 0)) scratch.GroupPositions[g] = before;
            else
            {
                float applied = f.Equal(weight, 1) ? full : f.Mul(weight, full);
                scratch.GroupPositions[g] = new(f.Sub(before.X, f.Mul(applied, frame.Recess.X)),
                    f.Sub(before.Y, f.Mul(applied, frame.Recess.Y)), f.Sub(before.Z, f.Mul(applied, frame.Recess.Z)));
            }
        }
        for (int v = 0; v < topology.VertexCount; v++)
        {
            int g = topology.Vertices[v].PositionGroup, representative = scratch.ActiveGroupRepresentatives[g];
            if (representative < 0 || f.Equal(anchor.Recession[g], 0)) continue;
            int slot = scratch.RegionCommands[topology.Vertices[representative].RegionIndex];
            if (f.NotEqual(commands[scratch.Frames[slot].Command].TransitionWeight, 0))
                scratch.After[v].Position = scratch.GroupPositions[g];
        }
        if (!CoverageReferenceNormals.TryUpdate(topology, scratch, out failure)) return false;
        scratch.Published.AsSpan().CopyTo(output);
        return true;
    }
}
