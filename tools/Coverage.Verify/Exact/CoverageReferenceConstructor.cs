using System;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tools.Coverage;

internal static class CoverageReferenceConstructor
{
    internal static bool TryConformPrepared(CoverageRefinement topology, ReadOnlySpan<CoverageCommandPose> commands,
        CoverageReferenceScratch scratch, out CoverageFailure failure)
    {
        if (commands.Length != scratch.ActiveCommands || commands.Length > scratch.CommandCapacity)
            throw new ArgumentException("Reference conformation requires prepared active command frames.", nameof(commands));
        var f = scratch.Arithmetic;
        var data = scratch.ReferenceTopology;
        Array.Clear(scratch.FullRecessions);
        for (int g = 0; g < data.PositionGroupCount; g++)
            scratch.GroupPositions[g] = scratch.Before[scratch.ReferenceTopology.GroupRepresentatives[g]].Position;
        CoverageReferenceTopology.FillActiveRepresentatives(topology, scratch.RegionCommands, scratch.ActiveGroupRepresentatives, f);
        var tally = new CoverageTally();
        // Representatives are visited by vertex index, independently of position-group or command-slot order.
        for (int v = 0; v < data.VertexCount; v++)
        {
            var record = data.Vertices[v];
            int g = record.PositionGroup;
            if (scratch.ActiveGroupRepresentatives[g] != v) continue;
            int slot = scratch.RegionCommands[record.RegionIndex];
            bool core = scratch.Core.Offsets[g] != scratch.Core.Offsets[g + 1];
            scratch.GroupPositions[g] = Conform(f, scratch.Before[v].Position, record.Strength, core, g, slot, commands, scratch, ref tally);
        }
        for (int v = 0; v < data.VertexCount; v++)
        {
            scratch.After[v] = scratch.Before[v];
            if (scratch.ActiveGroupRepresentatives[data.Vertices[v].PositionGroup] >= 0)
                scratch.After[v].Position = scratch.GroupPositions[data.Vertices[v].PositionGroup];
        }
        return tally.Succeeded(topology, commands, scratch.Order, out failure);
    }

    static Vector3 Conform(CoverageReferenceArithmetic f, Vector3 position, float strength, bool core, int group, int slot,
        ReadOnlySpan<CoverageCommandPose> commands, CoverageReferenceScratch scratch, ref CoverageTally tally)
    {
        float height;

        var frame = scratch.Frames[slot];
        var command = commands[frame.Command];
        var support = scratch.Supports[slot]!;
        Vector3 q = CoverageReferenceMath.Point(f, position, frame.HostToCoverage);
        float lower = f.Add(command.MinZ, frame.CapMargin), upper = f.Sub(command.MaxZ, frame.CapMargin);
        if (!CoverageReferenceMath.IsFinite3(f, q) || !f.IsFinite(lower) || !f.IsFinite(upper))
        { tally.Note(CoverageFailureCode.UnsupportedTransform, slot); return position; }
        if (f.Less(q.Z, lower) || f.Greater(q.Z, upper))
        { tally.Note(CoverageFailureCode.CapContact, slot); return position; }
        float spatial = 1;
        Vector2 xy = new Vector2(q.X, q.Y);
        if (!support.TryHeight(f, xy, out height))
        {
            float distance;
            if (core) { tally.Note(CoverageFailureCode.CoreEscaped, slot); return position; }
            if (!support.TryBoundary(f, xy, out height, out distance))
            { tally.Note(CoverageFailureCode.UnsupportedSupport, slot); return position; }
            if (f.GreaterOrEqual(distance, command.CollarWidth)) return position;
            float u = f.Clamp(f.Sub(1, (f.Div(distance, command.CollarWidth))), 0, 1);
            spatial = f.Mul((f.Mul(u, u)), (f.Sub(3, (f.Mul(2, u)))));
        }
        float target = f.Sub(height, frame.ClearanceCoverage), difference = f.Sub(q.Z, target);
        float request = f.Max(0, difference);
        float full = f.Mul((f.Mul((core ? 1 : strength), spatial)), request);
        float world = f.Mul(full, frame.ZScale);
        if (!f.IsFinite(height) || !f.IsFinite(spatial) || !f.IsFinite(target) || !f.IsFinite(difference)
            || !f.IsFinite(full) || !f.IsFinite(world))
        { tally.Note(CoverageFailureCode.UnsupportedTransform, slot); return position; }
        if (f.Greater(world, command.MaxWorldRecession))
        { tally.Note(CoverageFailureCode.RecessionExceeded, slot); return position; }
        if (f.Equal(full, 0)) return position;
        Vector3 moved = new Vector3(f.Sub(position.X, (f.Mul(full, frame.Recess.X))), f.Sub(position.Y, (f.Mul(full, frame.Recess.Y))), f.Sub(position.Z, (f.Mul(full, frame.Recess.Z))));
        Vector3 movedQ = CoverageReferenceMath.Point(f, moved, frame.HostToCoverage);
        if (!CoverageReferenceMath.IsFinite3(f, moved) || !CoverageReferenceMath.IsFinite3(f, movedQ))
        { tally.Note(CoverageFailureCode.UnsupportedTransform, slot); return position; }
        if (f.Less(movedQ.Z, lower) || f.Greater(movedQ.Z, upper))
        { tally.Note(CoverageFailureCode.CapContact, slot); return position; }
        scratch.FullRecessions[group] = full;
        return CoverageReferenceMath.Equal3(f, moved, position) ? position : moved;
    }

}
