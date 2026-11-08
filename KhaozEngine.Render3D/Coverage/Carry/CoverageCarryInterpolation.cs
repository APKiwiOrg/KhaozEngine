using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Frozen binary32 recipes and attributes over source vertices skinned in the current pose.</summary>
internal static class CoverageCarryInterpolation
{
    const float SurvivingFractionSquared = 1f / (1L << 50);

    internal static bool TryInterpolate(CoverageRefinement topology, ReadOnlySpan<ModelVertex> posedSource,
        CoverageCarryScratch scratch, out CoverageFailure failure)
    {
        Validate(topology, scratch);
        if (posedSource.Length != topology.SourceVertexCount || posedSource.Overlaps(scratch.BeforeVertices)
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
        // Computed groups share one canonical position.
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
            if (source >= 0) scratch.BeforeVertices[v] = posedSource[source];
            else
            {
                ModelVertex vertex;
                TryVertex(f, v, posedSource, scratch, out vertex);
                scratch.BeforeVertices[v] = vertex;
            }
        }
        failure = default;
        return true;
    }

    static bool ValidateSource(CoverageReferenceArithmetic f, ModelVertex input, int vertex, out ModelVertex result)
    {
        result = input;
        return CoverageReferenceMath.ReadVertex(f, ref result);
    }

    static bool AliasesEqual(CoverageReferenceArithmetic f, Vector3 position, Vector3 representative, int vertex)
    {
        return CoverageReferenceMath.Equal3(f, position, representative);
    }

    static bool Interpolate(CoverageReferenceArithmetic f, in CoveragePositionRecipe recipe, in CoverageReferenceWeights weights,
        float edgeParameter, ReadOnlySpan<ModelVertex> posed, int group, out Vector3 position)
    {
        position = Position(f, recipe, weights, edgeParameter, posed);
        return CoverageReferenceMath.IsFinite3(f, position);
    }

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

    static Vector3 Edge(CoverageReferenceArithmetic f, int a, int b, float t, ReadOnlySpan<ModelVertex> posed)
    {
        if (a > b) { int temporary = a; a = b; b = temporary; }
        Vector3 first = posed[a].Position, second = posed[b].Position;
        return new Vector3(f.Add((f.Mul(first.X, (f.Sub(1, t)))), (f.Mul(second.X, t))), f.Add((f.Mul(first.Y, (f.Sub(1, t)))), (f.Mul(second.Y, t))), f.Add((f.Mul(first.Z, (f.Sub(1, t)))), (f.Mul(second.Z, t))));
    }

    static bool TryVertex(CoverageReferenceArithmetic f, int vertex, ReadOnlySpan<ModelVertex> posed,
        CoverageCarryScratch scratch, out ModelVertex result)
    {
        Vector3 normal;
        Vector3 tangentDirection;
        var data = scratch.ReferenceTopology;
        var record = data.Vertices[vertex];
        var weights = data.VertexWeights[vertex];
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

    static float Sum(CoverageReferenceArithmetic f, float a, float b, float c, float wa, float wb, float wc) => f.Add((f.Add((f.Mul(a, wa)), (f.Mul(b, wb)))), (f.Mul(c, wc)));

    static float Value(CoverageReferenceArithmetic f, float a, float b, float c, in CoverageReferenceWeights weights) => f.Div(Sum(f, a, b, c, weights.A, weights.B, weights.C), 12f);

    static float Magnitude(CoverageReferenceArithmetic f, float a, float b, float c, in CoverageReferenceWeights weights) =>
        f.Add((f.Add(f.Abs(f.Mul(a, weights.A)), f.Abs(f.Mul(b, weights.B)))), f.Abs(f.Mul(c, weights.C)));

    static void Validate(CoverageRefinement topology, CoverageCarryScratch scratch)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(scratch);
        if (!ReferenceEquals(scratch.ReferenceTopology.Topology, topology))
            throw new ArgumentException("Reference scratch must be built for this topology instance.", nameof(scratch));
    }

    static bool Refuse(CoverageRefinement topology, CoverageFailureCode code, out CoverageFailure failure) =>
        CoverageCarryUniforms.Refuse(topology, code, 0, 0, 0, 0, out failure);
}
