using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Applies one selected offline row to the current posed surface. No geometric selection or conformation.</summary>
internal static class CoverageCarryReference
{
    /// <summary>Entry -1 successfully publishes the current uncovered pose. Other refusals leave output untouched.
    /// Direction problems recover locally and never refuse covered positions.
    /// Digest authentication belongs to host/table binding, before this internal application interface.</summary>
    internal static bool TryCarry(CoverageRefinement topology, ReadOnlySpan<Matrix4x4> composedPalette,
        CoverageRecessionTable table, int entryIndex, ReadOnlySpan<CoverageCarryCommand> commands,
        CoverageCarryScratch scratch, Span<ModelVertex> output, out CoverageFailure failure)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(scratch);
        if (!ReferenceEquals(topology, scratch.ReferenceTopology.Topology) || output.Length != topology.VertexCount
            || composedPalette.Length != scratch.Skin.BoneCount || output.Overlaps(scratch.PosedSource)
            || output.Overlaps(scratch.FlushedSource) || output.Overlaps(scratch.BeforeVertices)
            || output.Overlaps(scratch.After) || output.Overlaps(scratch.Published))
            throw new ArgumentException("Carry requires its topology, complete palette and disjoint exact output storage.");
        ArgumentOutOfRangeException.ThrowIfLessThan(entryIndex, -1);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(entryIndex, table.EntryCount);
        if (!Matches(topology, table)) return Refuse(topology, CoverageFailureCode.TableIdentityMismatch, out failure);
        if (commands.Length > CoverageCarryUniforms.MaxCommands)
            return CoverageCarryUniforms.Refuse(topology, CoverageFailureCode.CapacityExceeded, 0, 0,
                commands.Length, CoverageCarryUniforms.MaxCommands, out failure);
        var f = scratch.Arithmetic;
        Array.Fill(scratch.RegionCommands, -1);
        for (int slot = 0; slot < commands.Length; slot++)
        {
            var command = commands[slot];
            if ((uint)command.RegionIndex >= (uint)scratch.RegionCommands.Length)
                return Refuse(topology, CoverageFailureCode.UnknownRegion, out failure);
            if (scratch.RegionCommands[command.RegionIndex] >= 0)
                return Refuse(topology, CoverageFailureCode.OverlappingConformers, out failure);
            if (!CoverageReferenceMath.IsFinite3(f, command.RecessionDirection) || !f.IsFinite(command.TransitionWeight)
                || command.TransitionWeight is < 0 or > 1)
                return Refuse(topology, CoverageFailureCode.UnsupportedTransform, out failure);
            scratch.RegionCommands[command.RegionIndex] = slot;
        }
        if (!CoverageReferenceSkinning.TrySkin(f, scratch.Skin, topology.Source, composedPalette, scratch.PosedSource, out var code))
            return Refuse(topology, code, out failure);
        if (!CoverageCarryInterpolation.TryInterpolate(topology, scratch.PosedSource, scratch, out failure)) return false;
        if (entryIndex == -1)
        {
            scratch.Before.CopyTo(output);
            return true;
        }
        ReadOnlySpan<float> scalars = table.Entry(entryIndex);
        scratch.Before.CopyTo(scratch.After);
        for (int g = 0; g < topology.PositionGroupCount; g++)
        {
            int representative = table.Representatives[g];
            if (representative < 0) continue;
            int slot = scratch.RegionCommands[topology.Vertices[representative].RegionIndex];
            if (slot < 0 || f.Equal(scalars[g], 0) || f.Equal(commands[slot].TransitionWeight, 0)) continue;
            Vector3 before = scratch.Before[representative].Position;
            var command = commands[slot];
            float full = f.Read(scalars[g]);
            float applied = f.Equal(command.TransitionWeight, 1) ? full : f.Mul(command.TransitionWeight, full);
            var direction = command.RecessionDirection;
            scratch.GroupPositions[g] = new(f.Sub(before.X, f.Mul(applied, direction.X)),
                f.Sub(before.Y, f.Mul(applied, direction.Y)), f.Sub(before.Z, f.Mul(applied, direction.Z)));
        }
        for (int v = 0; v < topology.VertexCount; v++)
        {
            var record = topology.Vertices[v];
            int group = record.PositionGroup, representative = table.Representatives[group];
            if (record.RegionIndex < 0 || record.Strength == 0 || representative < 0 || f.Equal(scalars[group], 0)) continue;
            int slot = scratch.RegionCommands[topology.Vertices[representative].RegionIndex];
            if (slot >= 0 && f.NotEqual(commands[slot].TransitionWeight, 0))
                scratch.After[v].Position = scratch.GroupPositions[group];
        }
        if (!CoverageReferenceNormals.TryUpdate(topology, scratch, out failure)) return false;
        scratch.Published.AsSpan().CopyTo(output);
        return true;
    }

    static bool Matches(CoverageRefinement topology, CoverageRecessionTable table)
    {
        if (table.PositionGroupCount != topology.PositionGroupCount) return false;
        for (int group = 0; group < table.PositionGroupCount; group++)
        {
            int representative = table.Representatives[group];
            if (representative == -1) continue;
            if ((uint)representative >= (uint)topology.VertexCount) return false;
            var record = topology.Vertices[representative];
            if (record.PositionGroup != group || record.RegionIndex < 0 || record.Strength == 0) return false;
        }
        return true;
    }

    static bool Refuse(CoverageRefinement topology, CoverageFailureCode code, out CoverageFailure failure) =>
        CoverageCarryUniforms.Refuse(topology, code, 0, 0, 0, 0, out failure);
}
