using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace KhaozEngine.Render3D;

internal readonly record struct CoveragePositionBounds(CoverageScalarBounds X, CoverageScalarBounds Y,
    CoverageScalarBounds Z, CoverageScalarBounds W)
{
    internal static CoveragePositionBounds From(Vector3 p) => new(new(p.X, p.X), new(p.Y, p.Y), new(p.Z, p.Z), CoverageIntervals.One);
    internal static CoveragePositionBounds Invalid => new(CoverageIntervals.Invalid, CoverageIntervals.Invalid,
        CoverageIntervals.Invalid, CoverageIntervals.Invalid);
    internal bool IsFinite => X.IsFinite && Y.IsFinite && Z.IsFinite && W.IsFinite;
}

internal static partial class CoverageShaderPosition
{
    // Four products and three sums, independent of association or FMA contraction. Inputs and intermediates may flush.
    internal static CoverageScalarBounds FloatDot4(CoverageScalarBounds a0, CoverageScalarBounds b0,
        CoverageScalarBounds a1, CoverageScalarBounds b1, CoverageScalarBounds a2, CoverageScalarBounds b2,
        CoverageScalarBounds a3, CoverageScalarBounds b3)
    {
        Span<CoverageScalarBounds> a = stackalloc CoverageScalarBounds[4] { a0, a1, a2, a3 };
        Span<CoverageScalarBounds> b = stackalloc CoverageScalarBounds[4] { b0, b1, b2, b3 };
        var exact = CoverageIntervals.Zero;
        double magnitude = 0;
        for (int i = 0; i < 4; i++)
        {
            if (!a[i].IsFinite || !b[i].IsFinite) return CoverageIntervals.Invalid;
            var product = CoverageIntervals.Multiply(Flushed(a[i]), Flushed(b[i]));
            exact = CoverageIntervals.Add(exact, product);
            magnitude = Sum(magnitude, Math.Max(Math.Abs(product.Min), Math.Abs(product.Max)));
        }
        double error = Divide(Sum(Product(7 * U, magnitude), 7 * N), 1 - 7 * U);
        if (!exact.IsFinite || Sum(magnitude, error) > float.MaxValue) return CoverageIntervals.Invalid;
        return new(Math.BitDecrement(exact.Min - error), Math.BitIncrement(exact.Max + error));
    }

    static CoverageScalarBounds Flushed(CoverageScalarBounds value) => value.Min < N && value.Max > -N
        ? new(Math.Min(0, value.Min), Math.Max(0, value.Max)) : value;

    internal static CoveragePositionBounds Transform(in Matrix4x4 model, CoveragePositionBounds position)
    {
        ReadOnlySpan<float> values = MemoryMarshal.Cast<Matrix4x4, float>(MemoryMarshal.CreateReadOnlySpan(in model, 1));
        Span<CoverageScalarBounds> matrix = stackalloc CoverageScalarBounds[16];
        for (int i = 0; i < 16; i++) matrix[i] = new(values[i], values[i]);
        var result = Transform(matrix, position);
        // Affine Model preserves homogeneous W exactly, apart from the supported input flush.
        if (model.M14 == 0 && model.M24 == 0 && model.M34 == 0 && model.M44 == 1 && position.IsFinite)
            result = result with { W = Flushed(position.W) };
        return result;
    }

    static CoveragePositionBounds Transform(ReadOnlySpan<CoverageScalarBounds> matrix, CoveragePositionBounds p) => new(
        FloatDot4(matrix[0], p.X, matrix[4], p.Y, matrix[8], p.Z, matrix[12], p.W),
        FloatDot4(matrix[1], p.X, matrix[5], p.Y, matrix[9], p.Z, matrix[13], p.W),
        FloatDot4(matrix[2], p.X, matrix[6], p.Y, matrix[10], p.Z, matrix[14], p.W),
        FloatDot4(matrix[3], p.X, matrix[7], p.Y, matrix[11], p.Z, matrix[15], p.W));

    // Mirrors SkinnedModelVert: raw weights, conditional identity, blended mat4, local vec4, then reduced Model.
    internal static CoveragePositionBounds Skinned(in SkinnedVertex vertex, ReadOnlySpan<Matrix4x4> bones, in Matrix4x4 model)
    {
        Vector4 weights = vertex.BoneWeights;
        var total = FloatDot4(new(weights.X, weights.X), CoverageIntervals.One, new(weights.Y, weights.Y), CoverageIntervals.One,
            new(weights.Z, weights.Z), CoverageIntervals.One, new(weights.W, weights.W), CoverageIntervals.One);
        if (!total.IsFinite) return CoveragePositionBounds.Invalid;
        var identity = CoveragePositionBounds.From(vertex.Position);
        if (total.Max < 1e-8f) return Transform(model, identity);
        Span<int> indices = stackalloc int[4];
        for (int i = 0; i < 4; i++)
        {
            float index = vertex.BoneIndices[i];
            if (!float.IsFinite(index) || index < 0 || index >= bones.Length) return CoveragePositionBounds.Invalid;
            indices[i] = (int)index;
        }
        ReadOnlySpan<float> matrices = MemoryMarshal.Cast<Matrix4x4, float>(bones);
        Span<CoverageScalarBounds> blended = stackalloc CoverageScalarBounds[16];
        for (int c = 0; c < 16; c++)
        {
            float a = matrices[indices[0] * 16 + c], b = matrices[indices[1] * 16 + c],
                d = matrices[indices[2] * 16 + c], e = matrices[indices[3] * 16 + c];
            blended[c] = FloatDot4(new(a, a), new(weights.X, weights.X), new(b, b), new(weights.Y, weights.Y),
                new(d, d), new(weights.Z, weights.Z), new(e, e), new(weights.W, weights.W));
        }
        var local = Transform(blended, identity);
        if (total.Min < 1e-8f) local = new(Union(local.X, identity.X), Union(local.Y, identity.Y),
            Union(local.Z, identity.Z), Union(local.W, identity.W));
        return Transform(model, local);
    }

    static CoverageScalarBounds Union(CoverageScalarBounds a, CoverageScalarBounds b) =>
        !a.IsFinite || !b.IsFinite ? CoverageIntervals.Invalid : new(Math.Min(a.Min, b.Min), Math.Max(a.Max, b.Max));
}
