using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Scalar binary32 construction for the headless reference, with one atomic caller-output copy.</summary>
internal static class CoverageReferenceConstructor
{
    const float SurvivingFractionSquared = 1f / (1L << 50);

    /// <summary>Constructs a finite reference candidate atomically. Success supplies no certification or certificate.</summary>
    internal static bool TryConstruct(CoverageRefinement topology, ReadOnlySpan<ModelVertex> posedSource, in Matrix4x4 model,
        ReadOnlySpan<CoverageCommandPose> commands, CoverageReferenceScratch scratch, Span<ModelVertex> finalVertices,
        out CoverageFailure failure) => TryConstruct(topology, posedSource, model, commands, scratch, finalVertices,
            out failure, default);

    /// <summary>Internal like-for-like seam. Capture target and correction terms replace only the corresponding depths.</summary>
    internal static bool TryConstruct(CoverageRefinement topology, ReadOnlySpan<ModelVertex> posedSource, in Matrix4x4 model,
        ReadOnlySpan<CoverageCommandPose> commands, CoverageReferenceScratch scratch, Span<ModelVertex> finalVertices,
        out CoverageFailure failure, ReadOnlySpan<double> clearanceCoverageOverride,
        ReadOnlySpan<double> correctionClearanceOverride = default, ReadOnlySpan<double> correctionConversionOverride = default)
    {
        Validate(topology, scratch);
        if (posedSource.Length != topology.SourceVertexCount || finalVertices.Length != topology.VertexCount
            || posedSource.Overlaps(finalVertices) || posedSource.Overlaps(scratch.Before) || posedSource.Overlaps(scratch.After)
            || posedSource.Overlaps(scratch.Published) || finalVertices.Overlaps(scratch.Before)
            || finalVertices.Overlaps(scratch.After) || finalVertices.Overlaps(scratch.Published))
            throw new ArgumentException("Reference construction requires exact source and final lengths and disjoint buffers.");
        scratch.Arithmetic.Reset();
        var clearance = RoundOverrides(clearanceCoverageOverride, scratch.ClearanceOverrides);
        var correctionClearance = RoundOverrides(correctionClearanceOverride, scratch.CorrectionClearanceOverrides);
        var correctionConversion = RoundOverrides(correctionConversionOverride, scratch.CorrectionConversionOverrides);
        if (!CoverageReferenceFrames.TryPrepare(topology, model, commands, scratch, out failure)
            || !TryInterpolate(topology, posedSource, scratch, out failure)
            || !TryConformPrepared(topology, commands, scratch, out failure, clearance)
            || !CoverageReferenceCorrection.TryApplyPrepared(topology, commands, scratch, out failure,
                correctionClearance, correctionConversion)) return false;
        CoverageReferenceCorrection.ApplyTransition(topology, commands, scratch);
        if (!CoverageReferenceNormals.TryUpdate(topology, scratch, out failure)) return false;
        scratch.Published.AsSpan().CopyTo(finalVertices);
        return true;
    }

    internal static bool TryInterpolate(CoverageRefinement topology, ReadOnlySpan<ModelVertex> posedSource,
        CoverageReferenceScratch scratch, out CoverageFailure failure)
    {
        Validate(topology, scratch);
        if (posedSource.Length != topology.SourceVertexCount || posedSource.Overlaps(scratch.Before)
            || posedSource.Overlaps(scratch.After) || posedSource.Overlaps(scratch.Published))
            throw new ArgumentException("Reference interpolation requires an exact source length and disjoint buffers.", nameof(posedSource));
        var f = scratch.Arithmetic;
        ModelVertex checkedVertex;
        var data = scratch.ReferenceTopology;
        for (int i = 0; i < data.SourceVertexCount; i++)
        {
            if (!ValidateSource(f, posedSource[i], i, out var input))
                return Refuse(topology, CoverageFailureCode.UnsupportedTransform, out failure);
            scratch.FlushedSource[i] = input;
        }
        posedSource = scratch.FlushedSource;
        // Compare aliases by value. Originals keep their own flushed input bits, including signed zero.
        // Computed groups share one canonical position. Part C kernels must reproduce this policy.
        for (int i = 0; i < data.AliasCount; i++)
        {
            var alias = data.SourceAliases[i];
            if (!AliasesEqual(f, posedSource[alias.SourceVertex].Position, posedSource[alias.Representative].Position, alias.SourceVertex))
                return Refuse(topology, CoverageFailureCode.FoldedPatch, out failure);
        }
        for (int g = 0; g < data.PositionGroupCount; g++)
        {
            if (!Interpolate(f, data.Recipes[g], data.GroupWeights[g], data.EdgeParameters[g], posedSource, g, out var position))
                return Refuse(topology, CoverageFailureCode.UnsupportedTransform, out failure);
            scratch.GroupPositions[g] = position;
        }
        // Validate all generated attributes before touching Before, including attributes on protected splits.
        for (int v = 0; v < data.VertexCount; v++)
            if (data.Vertices[v].SourceVertex < 0 && !TryVertex(f, v, posedSource, scratch, out checkedVertex))
                return Refuse(topology, CoverageFailureCode.UnsupportedTransform, out failure);
        for (int v = 0; v < data.VertexCount; v++)
        {
            int source = data.Vertices[v].SourceVertex;
            if (source >= 0) scratch.Before[v] = posedSource[source];
            else
            {
                ModelVertex vertex;
                TryVertex(f, v, posedSource, scratch, out vertex);
                scratch.Before[v] = vertex;
            }
        }
        failure = default;
        return true;
    }

    [Binary32Kernel]
    static bool ValidateSource(CoverageReferenceArithmetic f, ModelVertex input, int vertex, out ModelVertex result)
    {
        f.At(CoverageExactnessPhase.SourceValidation, vertex);
        result = input;
        return CoverageReferenceMath.ReadVertex(f, ref result);
    }

    [Binary32Kernel]
    static bool AliasesEqual(CoverageReferenceArithmetic f, Vector3 position, Vector3 representative, int vertex)
    {
        f.At(CoverageExactnessPhase.SourceValidation, vertex);
        return CoverageReferenceMath.Equal3(f, position, representative);
    }

    [Binary32Kernel]
    static bool Interpolate(CoverageReferenceArithmetic f, in CoveragePositionRecipe recipe, in CoverageReferenceWeights weights,
        float edgeParameter, ReadOnlySpan<ModelVertex> posed, int group, out Vector3 position)
    {
        f.At(CoverageExactnessPhase.GroupInterpolation, group);
        position = Position(f, recipe, weights, edgeParameter, posed);
        return CoverageReferenceMath.IsFinite3(f, position);
    }

    internal static bool TryConformGroups(CoverageRefinement topology, ReadOnlySpan<CoverageCommandPose> commands,
        CoverageReferenceScratch scratch, out CoverageFailure failure) =>
        TryConformGroups(topology, commands, scratch, out failure, default);

    internal static bool TryCorrectAndTransition(CoverageRefinement topology, ReadOnlySpan<CoverageCommandPose> commands,
        CoverageReferenceScratch scratch, out CoverageFailure failure)
    {
        if (!CoverageReferenceCorrection.TryApply(topology, commands, scratch, out failure)) return false;
        CoverageReferenceCorrection.ApplyTransition(topology, commands, scratch);
        return true;
    }

    /// <summary>Overrides are exact capture target depths in sorted active-slot order. Frame preparation precedes each run.</summary>
    internal static bool TryConformGroups(CoverageRefinement topology, ReadOnlySpan<CoverageCommandPose> commands,
        CoverageReferenceScratch scratch, out CoverageFailure failure, ReadOnlySpan<double> clearanceCoverageOverride)
    {
        Validate(topology, scratch);
        return TryConformPrepared(topology, commands, scratch, out failure, RoundOverrides(clearanceCoverageOverride, scratch.ClearanceOverrides));
    }

    static bool TryConformPrepared(CoverageRefinement topology, ReadOnlySpan<CoverageCommandPose> commands,
        CoverageReferenceScratch scratch, out CoverageFailure failure, ReadOnlySpan<float> clearanceCoverageOverride)
    {
        Validate(topology, scratch);
        if (commands.Length != scratch.ActiveCommands || commands.Length > scratch.CommandCapacity)
            throw new ArgumentException("Reference conformation requires prepared active command frames.", nameof(commands));
        if (!clearanceCoverageOverride.IsEmpty && clearanceCoverageOverride.Length != scratch.ActiveCommands)
            throw new ArgumentException("Reference clearance overrides require one target per active slot.", nameof(clearanceCoverageOverride));
        for (int slot = 0; slot < clearanceCoverageOverride.Length; slot++)
        {
            float clearance = clearanceCoverageOverride[slot];
            if (!ValidClearance(scratch.UniformArithmetic, clearance))
                return CoverageFrames.Refuse(topology, CoverageFailureCode.UnsupportedTransform, commands[scratch.Frames[slot].Command], out failure);
            var frame = scratch.Frames[slot];
            scratch.Frames[slot] = new CoverageReferenceFrame(frame.Command, frame.HostToCoverage, frame.HostToActualSheet,
                frame.ActorLinear, frame.Recess, frame.ZScale, frame.CapMargin, clearance, frame.ActualClearanceCoverage, frame.CorrectionRate);
        }
        var f = scratch.Arithmetic;
        var data = scratch.ReferenceTopology;
        Array.Clear(scratch.FullRecessions);
        Array.Fill(scratch.ActiveGroupRepresentatives, -1);
        for (int g = 0; g < data.PositionGroupCount; g++)
            scratch.GroupPositions[g] = scratch.Before[scratch.ReferenceTopology.GroupRepresentatives[g]].Position;
        for (int v = 0; v < data.VertexCount; v++)
        {
            var record = data.Vertices[v];
            if (IsActive(f, record.Strength, v, record.RegionIndex < 0 ? -1 : scratch.RegionCommands[record.RegionIndex]) && record.RegionIndex >= 0 && scratch.RegionCommands[record.RegionIndex] >= 0
                && scratch.ActiveGroupRepresentatives[record.PositionGroup] < 0)
                scratch.ActiveGroupRepresentatives[record.PositionGroup] = v;
        }
        var tally = new CoverageTally();
        // Representatives are visited by vertex index, independently of position-group or command-slot order.
        for (int v = 0; v < data.VertexCount; v++)
        {
            var record = data.Vertices[v];
            int g = record.PositionGroup;
            if (scratch.ActiveGroupRepresentatives[g] != v) continue;
            int slot = scratch.RegionCommands[record.RegionIndex];
            bool core = scratch.ReferenceTopology.CoreFaceOffsets[g] != scratch.ReferenceTopology.CoreFaceOffsets[g + 1];
            scratch.GroupPositions[g] = Conform(f, scratch.Before[v].Position, record.Strength, core, v, g, slot, commands, scratch, ref tally);
        }
        for (int v = 0; v < data.VertexCount; v++)
        {
            scratch.After[v] = scratch.Before[v];
            if (scratch.ActiveGroupRepresentatives[data.Vertices[v].PositionGroup] >= 0)
                scratch.After[v].Position = scratch.GroupPositions[data.Vertices[v].PositionGroup];
        }
        return tally.Succeeded(topology, commands, scratch.Order, out failure);
    }

    [Binary32Kernel]
    static bool ValidClearance(CoverageReferenceArithmetic f, float clearance) => f.IsFinite(clearance) && !f.Less(clearance, 0);

    [Binary32Kernel]
    static bool IsActive(CoverageReferenceArithmetic f, float strength, int vertex, int slot)
    {
        f.At(CoverageExactnessPhase.Conformation, vertex, slot);
        return f.Greater(strength, 0);
    }

    [Binary32Kernel]
    static Vector3 Conform(CoverageReferenceArithmetic f, Vector3 position, float strength, bool core, int vertex, int group, int slot,
        ReadOnlySpan<CoverageCommandPose> commands, CoverageReferenceScratch scratch, ref CoverageTally tally)
    {
        float height;
        f.At(CoverageExactnessPhase.Conformation, vertex, slot);
        var frame = scratch.Frames[slot];
        var command = commands[frame.Command];
        var support = command.Support.ReferenceData;
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

    [Binary32Kernel]
    static Vector3 Position(CoverageReferenceArithmetic f, in CoveragePositionRecipe recipe, in CoverageReferenceWeights weights, float edgeParameter, ReadOnlySpan<ModelVertex> posed)
    {
        if (recipe.WeightA == 12) return posed[recipe.A].Position;
        if (recipe.WeightB == 12) return posed[recipe.B].Position;
        if (recipe.WeightC == 12) return posed[recipe.C].Position;
        if (recipe.WeightC == 0) return Edge(f, recipe.A, recipe.B, edgeParameter, posed);
        if (recipe.WeightB == 0) return Edge(f, recipe.A, recipe.C, edgeParameter, posed);
        if (recipe.WeightA == 0) return Edge(f, recipe.B, recipe.C, edgeParameter, posed);
        Vector3 a = posed[recipe.A].Position, b = posed[recipe.B].Position, c = posed[recipe.C].Position;
        return new Vector3(f.Div(Sum(f, a.X, b.X, c.X, weights.A, weights.B, weights.C), 12f),
            f.Div(Sum(f, a.Y, b.Y, c.Y, weights.A, weights.B, weights.C), 12f),
            f.Div(Sum(f, a.Z, b.Z, c.Z, weights.A, weights.B, weights.C), 12f));
    }

    [Binary32Kernel]
    static Vector3 Edge(CoverageReferenceArithmetic f, int a, int b, float t, ReadOnlySpan<ModelVertex> posed)
    {
        if (a > b) { int temporary = a; a = b; b = temporary; }
        Vector3 first = posed[a].Position, second = posed[b].Position;
        return new Vector3(f.Add((f.Mul(first.X, (f.Sub(1, t)))), (f.Mul(second.X, t))), f.Add((f.Mul(first.Y, (f.Sub(1, t)))), (f.Mul(second.Y, t))), f.Add((f.Mul(first.Z, (f.Sub(1, t)))), (f.Mul(second.Z, t))));
    }

    [Binary32Kernel]
    static bool TryVertex(CoverageReferenceArithmetic f, int vertex, ReadOnlySpan<ModelVertex> posed,
        CoverageReferenceScratch scratch, out ModelVertex result)
    {
        Vector3 normal;
        Vector3 tangentDirection;
        var data = scratch.ReferenceTopology;
        var record = data.Vertices[vertex];
        var weights = data.VertexWeights[vertex];
        f.At(CoverageExactnessPhase.AttributeInterpolation, vertex);
        var triangle = data.SourceTriangles[record.SourceTriangle];
        ModelVertex a = posed[triangle.A], b = posed[triangle.B], c = posed[triangle.C];
        result = default;
        if (!Direction(f, a.Normal, b.Normal, c.Normal, weights, out normal)
            || !Direction(f, new Vector3(a.Tangent.X, a.Tangent.Y, a.Tangent.Z), new Vector3(b.Tangent.X, b.Tangent.Y, b.Tangent.Z),
                new Vector3(c.Tangent.X, c.Tangent.Y, c.Tangent.Z), weights, out tangentDirection)) return false;
        Vector4 tangent = default(Vector4);
        if (!CoverageReferenceMath.Equal3(f, tangentDirection, default(Vector3)))
        {
            float handedness = Sum(f, a.Tangent.W, b.Tangent.W, c.Tangent.W, weights.A, weights.B, weights.C);
            if (!f.IsFinite(handedness)) return false;
            tangent = new Vector4(tangentDirection, f.Less(handedness, 0) ? (float)-1 : 1f);
        }
        result = new ModelVertex(scratch.GroupPositions[record.PositionGroup], normal,
            new Vector4(Value(f, a.Color.X, b.Color.X, c.Color.X, weights), Value(f, a.Color.Y, b.Color.Y, c.Color.Y, weights),
                Value(f, a.Color.Z, b.Color.Z, c.Color.Z, weights), Value(f, a.Color.W, b.Color.W, c.Color.W, weights)),
            new Vector2(Value(f, a.Uv.X, b.Uv.X, c.Uv.X, weights), Value(f, a.Uv.Y, b.Uv.Y, c.Uv.Y, weights)), tangent);
        return CoverageReferenceMath.IsFiniteVertex(f, result);
    }

    [Binary32Kernel]
    static bool Direction(CoverageReferenceArithmetic f, Vector3 a, Vector3 b, Vector3 c, in CoverageReferenceWeights weights, out Vector3 unit)
    {
        unit = default;
        Vector3 sum = new Vector3(Sum(f, a.X, b.X, c.X, weights.A, weights.B, weights.C), Sum(f, a.Y, b.Y, c.Y, weights.A, weights.B, weights.C),
            Sum(f, a.Z, b.Z, c.Z, weights.A, weights.B, weights.C));
        Vector3 magnitude = new Vector3(Magnitude(f, a.X, b.X, c.X, weights), Magnitude(f, a.Y, b.Y, c.Y, weights), Magnitude(f, a.Z, b.Z, c.Z, weights));
        if (!CoverageReferenceMath.IsFinite3(f, sum) || !CoverageReferenceMath.IsFinite3(f, magnitude)) return false;
        if (CoverageReferenceMath.Equal3(f, magnitude, default(Vector3))) { unit = sum; return true; }
        float lengthSquared = CoverageReferenceMath.Dot3(f, sum, sum), magnitudeSquared = CoverageReferenceMath.Dot3(f, magnitude, magnitude);
        if (!f.IsFinite(lengthSquared) || !f.IsFinite(magnitudeSquared)
            || !(f.Greater(lengthSquared, f.Mul(magnitudeSquared, SurvivingFractionSquared)))) return false;
        float length = f.Sqrt(lengthSquared);
        unit = new Vector3(f.Div(sum.X, length), f.Div(sum.Y, length), f.Div(sum.Z, length));
        return CoverageReferenceMath.IsFinite3(f, unit);
    }

    [Binary32Kernel]
    static float Sum(CoverageReferenceArithmetic f, float a, float b, float c, float wa, float wb, float wc) => f.Add((f.Add((f.Mul(a, wa)), (f.Mul(b, wb)))), (f.Mul(c, wc)));
    [Binary32Kernel]
    static float Value(CoverageReferenceArithmetic f, float a, float b, float c, in CoverageReferenceWeights weights) => f.Div(Sum(f, a, b, c, weights.A, weights.B, weights.C), 12f);
    [Binary32Kernel]
    static float Magnitude(CoverageReferenceArithmetic f, float a, float b, float c, in CoverageReferenceWeights weights) =>
        f.Add((f.Add(f.Abs(f.Mul(a, weights.A)), f.Abs(f.Mul(b, weights.B)))), f.Abs(f.Mul(c, weights.C)));

    internal static ReadOnlySpan<float> RoundOverrides(ReadOnlySpan<double> values, float[] rounded)
    {
        if (values.Length > rounded.Length) throw new ArgumentException("Reference overrides exceed command capacity.");
        for (int i = 0; i < values.Length; i++) rounded[i] = (float)values[i];
        return rounded.AsSpan(0, values.Length);
    }

    static void Validate(CoverageRefinement topology, CoverageReferenceScratch scratch)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(scratch);
        if (!ReferenceEquals(scratch.ReferenceTopology.Topology, topology))
            throw new ArgumentException("Reference scratch must be built for this topology instance.", nameof(scratch));
    }

    [Binary32Kernel]
    static bool Refuse(CoverageRefinement topology, CoverageFailureCode code, out CoverageFailure failure) =>
        CoverageFrames.Refuse(topology, code, 0, 0, 0, 0, out failure);
}
