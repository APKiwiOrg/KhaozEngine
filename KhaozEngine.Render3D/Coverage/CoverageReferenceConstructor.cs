using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Scalar binary32 construction phases for the headless reference. Only scratch is written here.</summary>
internal static class CoverageReferenceConstructor
{
    const float SurvivingFractionSquared = 1f / (1L << 50);

    internal static bool TryInterpolate(CoverageRefinement topology, ReadOnlySpan<ModelVertex> posedSource,
        CoverageReferenceScratch scratch, out CoverageFailure failure)
    {
        Validate(topology, scratch);
        if (posedSource.Length != topology.SourceVertexCount || posedSource.Overlaps(scratch.Before)
            || posedSource.Overlaps(scratch.After) || posedSource.Overlaps(scratch.Published))
            throw new ArgumentException("Reference interpolation requires an exact source length and disjoint buffers.", nameof(posedSource));
        for (int i = 0; i < posedSource.Length; i++)
            if (!CoverageRefinement.Finite(posedSource[i]))
                return Refuse(topology, CoverageFailureCode.UnsupportedTransform, out failure);
        for (int i = 0; i < topology.SourceAliases.Count; i++)
        {
            var alias = topology.SourceAliases[i];
            if (posedSource[alias.SourceVertex].Position != posedSource[alias.Representative].Position)
                return Refuse(topology, CoverageFailureCode.FoldedPatch, out failure);
        }
        for (int g = 0; g < scratch.GroupPositions.Length; g++)
        {
            Vector3 position = Position(scratch.ReferenceTopology.Recipes[g], posedSource);
            if (!CoverageRefinement.Finite(position))
                return Refuse(topology, CoverageFailureCode.UnsupportedTransform, out failure);
            scratch.GroupPositions[g] = position;
        }
        // Validate all generated attributes before touching Before, including attributes on protected splits.
        for (int v = 0; v < topology.VertexCount; v++)
            if (topology.Vertices[v].SourceVertex < 0 && !TryVertex(topology, v, posedSource, scratch, out _))
                return Refuse(topology, CoverageFailureCode.UnsupportedTransform, out failure);
        for (int v = 0; v < topology.VertexCount; v++)
        {
            int source = topology.Vertices[v].SourceVertex;
            if (source >= 0) scratch.Before[v] = posedSource[source];
            else
            {
                _ = TryVertex(topology, v, posedSource, scratch, out var vertex);
                scratch.Before[v] = vertex;
            }
        }
        failure = default;
        return true;
    }

    internal static bool TryConformGroups(CoverageRefinement topology, ReadOnlySpan<CoverageCommandPose> commands,
        CoverageReferenceScratch scratch, out CoverageFailure failure) =>
        TryConformGroups(topology, commands, scratch, out failure, default);

    /// <summary>Overrides are exact capture target depths in sorted active-slot order. Frame preparation precedes each run.</summary>
    internal static bool TryConformGroups(CoverageRefinement topology, ReadOnlySpan<CoverageCommandPose> commands,
        CoverageReferenceScratch scratch, out CoverageFailure failure, ReadOnlySpan<double> clearanceCoverageOverride)
    {
        Validate(topology, scratch);
        if (commands.Length != scratch.ActiveCommands || commands.Length > scratch.CommandCapacity)
            throw new ArgumentException("Reference conformation requires prepared active command frames.", nameof(commands));
        if (!clearanceCoverageOverride.IsEmpty && clearanceCoverageOverride.Length != scratch.ActiveCommands)
            throw new ArgumentException("Reference clearance overrides require one target per active slot.", nameof(clearanceCoverageOverride));
        for (int slot = 0; slot < clearanceCoverageOverride.Length; slot++)
        {
            float clearance = (float)clearanceCoverageOverride[slot];
            if (!float.IsFinite(clearance) || clearance < 0)
                return CoverageFrames.Refuse(topology, CoverageFailureCode.UnsupportedTransform, commands[scratch.Frames[slot].Command], out failure);
            scratch.Frames[slot] = scratch.Frames[slot] with { ClearanceCoverage = clearance };
        }
        Array.Clear(scratch.FullRecessions);
        Array.Fill(scratch.ActiveGroupRepresentatives, -1);
        for (int g = 0; g < scratch.GroupPositions.Length; g++)
            scratch.GroupPositions[g] = scratch.Before[scratch.ReferenceTopology.GroupRepresentatives[g]].Position;
        for (int v = 0; v < topology.VertexCount; v++)
        {
            var record = topology.Vertices[v];
            if (record.Strength > 0 && record.RegionIndex >= 0 && scratch.RegionCommands[record.RegionIndex] >= 0
                && scratch.ActiveGroupRepresentatives[record.PositionGroup] < 0)
                scratch.ActiveGroupRepresentatives[record.PositionGroup] = v;
        }
        var tally = new CoverageTally();
        // Representatives are visited by vertex index, independently of position-group or command-slot order.
        for (int v = 0; v < topology.VertexCount; v++)
        {
            var record = topology.Vertices[v];
            int g = record.PositionGroup;
            if (scratch.ActiveGroupRepresentatives[g] != v) continue;
            int slot = scratch.RegionCommands[record.RegionIndex];
            bool core = scratch.ReferenceTopology.CoreFaceOffsets[g] != scratch.ReferenceTopology.CoreFaceOffsets[g + 1];
            scratch.GroupPositions[g] = Conform(scratch.Before[v].Position, record.Strength, core, g, slot, commands, scratch, ref tally);
        }
        for (int v = 0; v < topology.VertexCount; v++)
        {
            scratch.After[v] = scratch.Before[v];
            if (scratch.ActiveGroupRepresentatives[topology.Vertices[v].PositionGroup] >= 0)
                scratch.After[v].Position = scratch.GroupPositions[topology.Vertices[v].PositionGroup];
        }
        return tally.Succeeded(topology, commands, scratch.Order, out failure);
    }

    static Vector3 Conform(Vector3 position, float strength, bool core, int group, int slot,
        ReadOnlySpan<CoverageCommandPose> commands, CoverageReferenceScratch scratch, ref CoverageTally tally)
    {
        var frame = scratch.Frames[slot];
        var command = commands[frame.Command];
        var support = command.Support.ReferenceData;
        Vector3 q = CoverageReferenceMath.Point(position, frame.HostToCoverage);
        float lower = command.MinZ + frame.CapMargin, upper = command.MaxZ - frame.CapMargin;
        if (!CoverageRefinement.Finite(q) || !float.IsFinite(lower) || !float.IsFinite(upper))
        { tally.Note(CoverageFailureCode.UnsupportedTransform, slot); return position; }
        if (q.Z < lower || q.Z > upper)
        { tally.Note(CoverageFailureCode.CapContact, slot); return position; }
        float spatial = 1;
        Vector2 xy = new(q.X, q.Y);
        if (!support.TryHeight(xy, out float height))
        {
            if (core) { tally.Note(CoverageFailureCode.CoreEscaped, slot); return position; }
            if (!support.TryBoundary(xy, out height, out float distance))
            { tally.Note(CoverageFailureCode.UnsupportedSupport, slot); return position; }
            if (distance >= command.CollarWidth) return position;
            float u = Math.Clamp(1 - (distance / command.CollarWidth), 0, 1);
            spatial = (u * u) * (3 - (2 * u));
        }
        float target = height - frame.ClearanceCoverage, difference = q.Z - target;
        float request = MathF.Max(0, difference);
        float full = ((core ? 1 : strength) * spatial) * request;
        float world = full * frame.ZScale;
        if (!float.IsFinite(height) || !float.IsFinite(spatial) || !float.IsFinite(target) || !float.IsFinite(difference)
            || !float.IsFinite(full) || !float.IsFinite(world))
        { tally.Note(CoverageFailureCode.UnsupportedTransform, slot); return position; }
        if (world > command.MaxWorldRecession)
        { tally.Note(CoverageFailureCode.RecessionExceeded, slot); return position; }
        if (full == 0) return position;
        Vector3 moved = new(position.X - (full * frame.Recess.X), position.Y - (full * frame.Recess.Y), position.Z - (full * frame.Recess.Z));
        Vector3 movedQ = CoverageReferenceMath.Point(moved, frame.HostToCoverage);
        if (!CoverageRefinement.Finite(moved) || !CoverageRefinement.Finite(movedQ))
        { tally.Note(CoverageFailureCode.UnsupportedTransform, slot); return position; }
        if (movedQ.Z < lower || movedQ.Z > upper)
        { tally.Note(CoverageFailureCode.CapContact, slot); return position; }
        scratch.FullRecessions[group] = full;
        return moved == position ? position : moved;
    }

    static Vector3 Position(in CoveragePositionRecipe recipe, ReadOnlySpan<ModelVertex> posed)
    {
        if (recipe.WeightA == 12) return posed[recipe.A].Position;
        if (recipe.WeightB == 12) return posed[recipe.B].Position;
        if (recipe.WeightC == 12) return posed[recipe.C].Position;
        if (recipe.WeightC == 0) return Edge(recipe.A, recipe.B, recipe.WeightB, posed);
        if (recipe.WeightB == 0) return Edge(recipe.A, recipe.C, recipe.WeightC, posed);
        if (recipe.WeightA == 0) return Edge(recipe.B, recipe.C, recipe.WeightC, posed);
        Vector3 a = posed[recipe.A].Position, b = posed[recipe.B].Position, c = posed[recipe.C].Position;
        return new(Sum(a.X, b.X, c.X, recipe.WeightA, recipe.WeightB, recipe.WeightC) / 12f,
            Sum(a.Y, b.Y, c.Y, recipe.WeightA, recipe.WeightB, recipe.WeightC) / 12f,
            Sum(a.Z, b.Z, c.Z, recipe.WeightA, recipe.WeightB, recipe.WeightC) / 12f);
    }

    static Vector3 Edge(int a, int b, int numerator, ReadOnlySpan<ModelVertex> posed)
    {
        if (a > b) { (a, b) = (b, a); numerator = 12 - numerator; }
        float t = numerator / 12f;
        Vector3 first = posed[a].Position, second = posed[b].Position;
        return new((first.X * (1 - t)) + (second.X * t), (first.Y * (1 - t)) + (second.Y * t), (first.Z * (1 - t)) + (second.Z * t));
    }

    static bool TryVertex(CoverageRefinement topology, int vertex, ReadOnlySpan<ModelVertex> posed,
        CoverageReferenceScratch scratch, out ModelVertex result)
    {
        var record = topology.Vertices[vertex];
        var triangle = topology.Source.Triangles[record.SourceTriangle];
        ModelVertex a = posed[triangle.A], b = posed[triangle.B], c = posed[triangle.C];
        result = default;
        if (!Direction(a.Normal, b.Normal, c.Normal, record, out var normal)
            || !Direction(new(a.Tangent.X, a.Tangent.Y, a.Tangent.Z), new(b.Tangent.X, b.Tangent.Y, b.Tangent.Z),
                new(c.Tangent.X, c.Tangent.Y, c.Tangent.Z), record, out var tangentDirection)) return false;
        Vector4 tangent = Vector4.Zero;
        if (tangentDirection != Vector3.Zero)
        {
            float handedness = Sum(a.Tangent.W, b.Tangent.W, c.Tangent.W, record.A, record.B, record.C);
            if (!float.IsFinite(handedness)) return false;
            tangent = new(tangentDirection, handedness < 0 ? -1 : 1);
        }
        result = new(scratch.GroupPositions[record.PositionGroup], normal,
            new(Value(a.Color.X, b.Color.X, c.Color.X, record), Value(a.Color.Y, b.Color.Y, c.Color.Y, record),
                Value(a.Color.Z, b.Color.Z, c.Color.Z, record), Value(a.Color.W, b.Color.W, c.Color.W, record)),
            new(Value(a.Uv.X, b.Uv.X, c.Uv.X, record), Value(a.Uv.Y, b.Uv.Y, c.Uv.Y, record)), tangent);
        return CoverageRefinement.Finite(result);
    }

    static bool Direction(Vector3 a, Vector3 b, Vector3 c, in CoverageRefinedVertex record, out Vector3 unit)
    {
        unit = default;
        Vector3 sum = new(Sum(a.X, b.X, c.X, record.A, record.B, record.C), Sum(a.Y, b.Y, c.Y, record.A, record.B, record.C),
            Sum(a.Z, b.Z, c.Z, record.A, record.B, record.C));
        Vector3 magnitude = new(Magnitude(a.X, b.X, c.X, record), Magnitude(a.Y, b.Y, c.Y, record), Magnitude(a.Z, b.Z, c.Z, record));
        if (!CoverageRefinement.Finite(sum) || !CoverageRefinement.Finite(magnitude)) return false;
        if (magnitude == Vector3.Zero) { unit = sum; return true; }
        float lengthSquared = CoverageReferenceMath.Dot3(sum, sum), magnitudeSquared = CoverageReferenceMath.Dot3(magnitude, magnitude);
        if (!float.IsFinite(lengthSquared) || !float.IsFinite(magnitudeSquared)
            || !(lengthSquared > magnitudeSquared * SurvivingFractionSquared)) return false;
        float length = MathF.Sqrt(lengthSquared);
        unit = new(sum.X / length, sum.Y / length, sum.Z / length);
        return CoverageRefinement.Finite(unit);
    }

    static float Sum(float a, float b, float c, int wa, int wb, int wc) => ((a * wa) + (b * wb)) + (c * wc);
    static float Value(float a, float b, float c, in CoverageRefinedVertex record) => Sum(a, b, c, record.A, record.B, record.C) / 12f;
    static float Magnitude(float a, float b, float c, in CoverageRefinedVertex record) =>
        (MathF.Abs(a * record.A) + MathF.Abs(b * record.B)) + MathF.Abs(c * record.C);

    static void Validate(CoverageRefinement topology, CoverageReferenceScratch scratch)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(scratch);
        if (!ReferenceEquals(scratch.ReferenceTopology.Topology, topology))
            throw new ArgumentException("Reference scratch must be built for this topology instance.", nameof(scratch));
    }

    static bool Refuse(CoverageRefinement topology, CoverageFailureCode code, out CoverageFailure failure) =>
        CoverageFrames.Refuse(topology, code, 0, 0, 0, 0, out failure);
}
