using System;
using System.Numerics;
using static KhaozEngine.Render3D.CoverageIntervals;

namespace KhaozEngine.Render3D;

/// <summary>Cold raw-input influence envelopes for the two ordinary skinning routes. No vertex data is retained.</summary>
internal sealed class CoverageOrdinaryBounds
{
    const double U = 1d / 16777216, N = 1.1754943508222875e-38;
    readonly CoveragePositionBounds source;
    readonly CoveragePositionBounds[] boxes;
    readonly CoverageScalarBounds[] weights;
    readonly CoverageScalarBounds totals;
    readonly bool nonnegative = true, gpuIdentity, gpuBlend, cpuIdentity, cpuBlend;

    internal CoverageOrdinaryBounds(ReadOnlySpan<SkinnedVertex> vertices)
    {
        source = EmptyPosition;
        totals = Empty;
        int count = 0;
        foreach (ref readonly var vertex in vertices)
            for (int slot = 0; slot < 4; slot++) count = Math.Max(count, checked((int)vertex.BoneIndices[slot] + 1));
        boxes = new CoveragePositionBounds[count];
        weights = new CoverageScalarBounds[count * 4];
        Array.Fill(boxes, EmptyPosition); Array.Fill(weights, Empty);
        foreach (ref readonly var vertex in vertices)
        {
            var position = CoveragePositionBounds.From(vertex.Position);
            source = Union(source, position);
            Vector4 raw = vertex.BoneWeights;
            var total = WeightSum(raw);
            totals = Union(totals, total);
            gpuIdentity |= !total.IsFinite || total.Min < 1e-8f;
            gpuBlend |= !total.IsFinite || total.Max >= 1e-8f;
            float cpuTotal = raw.X + raw.Y + raw.Z + raw.W;
            cpuIdentity |= cpuTotal < 1e-8f; cpuBlend |= !(cpuTotal < 1e-8f);
            for (int slot = 0; slot < 4; slot++)
            {
                int bone = (int)vertex.BoneIndices[slot];
                nonnegative &= raw[slot] >= 0;
                weights[bone * 4 + slot] = Union(weights[bone * 4 + slot], Point(raw[slot]));
                if (raw[slot] != 0) boxes[bone] = Union(boxes[bone], position);
            }
        }
    }

    internal CoveragePositionBounds Transform(ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 model, bool vertexSkinning)
    {
        bool identity = vertexSkinning ? gpuIdentity : cpuIdentity;
        bool blend = vertexSkinning ? gpuBlend : cpuBlend;
        var local = source;
        if (blend)
        {
            if (palette.Length < boxes.Length || !totals.IsFinite) return CoveragePositionBounds.Invalid;
            local = Signed(palette, vertexSkinning);
            // Joined bounds retain total-weight correlation. Slot bounds retain small weights amplified by large coefficients.
            if (nonnegative) local = Tighter(local, Joined(palette, vertexSkinning));
            if (vertexSkinning && AffinePalette(palette)) local = local with { W = totals };
            if (identity) local = Union(local, source);
        }
        // SkinVertex returns XYZ. The ordinary model shader supplies W=1 after CPU skinning.
        if (!vertexSkinning) local = local with { W = One };
        return CoverageShaderPosition.Transform(model, local);
    }

    CoveragePositionBounds Joined(ReadOnlySpan<Matrix4x4> palette, bool homogeneous)
    {
        Span<CoverageScalarBounds> axes = stackalloc CoverageScalarBounds[4];
        Span<double> coefficients = stackalloc double[4];
        for (int axis = 0; axis < (homogeneous ? 4 : 3); axis++)
        {
            CoverageScalarBounds joined = Empty;
            double products = 0;
            coefficients.Clear();
            for (int bone = 0; bone < boxes.Length; bone++)
            {
                for (int slot = 0; slot < 4; slot++)
                    if (weights[bone * 4 + slot] != Empty)
                        for (int row = 0; row < 4; row++)
                        {
                            float coefficient = palette[bone][row, axis];
                            if (!float.IsFinite(coefficient)) return CoveragePositionBounds.Invalid;
                            coefficients[row] = Math.Max(coefficients[row], Math.Abs((double)coefficient));
                        }
                var box = boxes[bone];
                if (!box.IsFinite) continue;
                CoverageScalarBounds value = Zero, magnitude = Zero;
                for (int row = 0; row < 4; row++)
                {
                    var product = Multiply(Input(Axis(box, row)), Input(Point(palette[bone][row, axis])));
                    value = Add(value, product); magnitude = Add(magnitude, Point(Abs(product)));
                }
                joined = Union(joined, value); products = Math.Max(products, magnitude.Max);
            }
            if (!joined.IsFinite) return CoveragePositionBounds.Invalid;
            foreach (double coefficient in coefficients)
                if (!Safe(Multiply(Point(coefficient), new(0, totals.Max)), 4)) return CoveragePositionBounds.Invalid;
            var positionSum = Add(Add(Point(Abs(source.X)), Point(Abs(source.Y))), Point(Abs(source.Z)));
            var flush = Divide(Add(Multiply(Point(7 * N), Add(positionSum, One)), Point(6 * N)), Point(1 - 8 * U));
            var magnitudeBound = Multiply(Point(products), new(0, totals.Max));
            var error = Add(Multiply(Gamma(8), magnitudeBound), flush);
            if (!Add(magnitudeBound, error).IsFinite || Add(magnitudeBound, error).Max > float.MaxValue)
                return CoveragePositionBounds.Invalid;
            axes[axis] = Add(Multiply(joined, totals), new(-error.Max, error.Max));
        }
        axes[3] = homogeneous ? axes[3] : One;
        return new(axes[0], axes[1], axes[2], axes[3]);
    }

    CoveragePositionBounds Signed(ReadOnlySpan<Matrix4x4> palette, bool homogeneous)
    {
        Span<CoverageScalarBounds> matrix = stackalloc CoverageScalarBounds[16];
        Span<CoverageScalarBounds> terms = stackalloc CoverageScalarBounds[4];
        for (int row = 0; row < 4; row++)
            for (int column = 0; column < (homogeneous ? 4 : 3); column++)
            {
                for (int slot = 0; slot < 4; slot++)
                {
                    terms[slot] = Empty;
                    for (int bone = 0; bone < boxes.Length; bone++)
                    {
                        var weight = weights[bone * 4 + slot];
                        if (weight == Empty) continue;
                        var product = CoverageShaderPosition.FloatDot4(Point(palette[bone][row, column]), weight,
                            Zero, Zero, Zero, Zero, Zero, Zero);
                        terms[slot] = Union(terms[slot], product);
                    }
                }
                matrix[row * 4 + column] = CoverageShaderPosition.FloatDot4(terms[0], One, terms[1], One, terms[2], One, terms[3], One);
            }
        Span<CoverageScalarBounds> result = stackalloc CoverageScalarBounds[4];
        for (int column = 0; column < (homogeneous ? 4 : 3); column++)
            result[column] = CoverageShaderPosition.FloatDot4(source.X, matrix[column], source.Y, matrix[4 + column],
                source.Z, matrix[8 + column], One, matrix[12 + column]);
        result[3] = homogeneous ? result[3] : One;
        return new(result[0], result[1], result[2], result[3]);
    }

    static bool Safe(CoverageScalarBounds magnitude, int operations) => magnitude.IsFinite
        && Add(magnitude, Add(Multiply(Gamma(operations), magnitude), Point(7 * N))).Max <= float.MaxValue;
    static CoverageScalarBounds Gamma(int count) => Divide(Point(count * U), Point(1 - count * U));
    static CoverageScalarBounds Input(CoverageScalarBounds value) => value.Min < N && value.Max > -N
        ? new(Math.Min(0, value.Min), Math.Max(0, value.Max)) : value;
    static double Abs(CoverageScalarBounds value) => Math.Max(Math.Abs(value.Min), Math.Abs(value.Max));
    static CoverageScalarBounds Point(double value) => new(value, value);
    static CoverageScalarBounds Empty => new(double.PositiveInfinity, double.NegativeInfinity);
    static CoveragePositionBounds EmptyPosition => new(Empty, Empty, Empty, Empty);
    static CoverageScalarBounds Axis(CoveragePositionBounds box, int axis) => axis switch { 0 => box.X, 1 => box.Y, 2 => box.Z, _ => box.W };
    static CoverageScalarBounds Union(CoverageScalarBounds a, CoverageScalarBounds b) => a == Empty ? b : b == Empty ? a
        : !a.IsFinite || !b.IsFinite ? Invalid : new(Math.Min(a.Min, b.Min), Math.Max(a.Max, b.Max));
    static CoveragePositionBounds Union(CoveragePositionBounds a, CoveragePositionBounds b) =>
        new(Union(a.X, b.X), Union(a.Y, b.Y), Union(a.Z, b.Z), Union(a.W, b.W));
    static CoverageScalarBounds Tighter(CoverageScalarBounds a, CoverageScalarBounds b) => !a.IsFinite ? b : !b.IsFinite ? a
        : new(Math.Max(a.Min, b.Min), Math.Min(a.Max, b.Max));
    static CoveragePositionBounds Tighter(CoveragePositionBounds a, CoveragePositionBounds b) =>
        new(Tighter(a.X, b.X), Tighter(a.Y, b.Y), Tighter(a.Z, b.Z), Tighter(a.W, b.W));
    bool AffinePalette(ReadOnlySpan<Matrix4x4> palette)
    {
        for (int bone = 0; bone < boxes.Length; bone++)
            for (int slot = 0; slot < 4; slot++)
                if (weights[bone * 4 + slot] != Empty
                    && (palette[bone].M14 != 0 || palette[bone].M24 != 0 || palette[bone].M34 != 0 || palette[bone].M44 != 1)) return false;
        return true;
    }

    // Enclose every association and permutation of four additions, including preserved or flushed raw inputs/results.
    // This retains exact cancellation for the identity branch without applying the covered kernel's input flush.
    static CoverageScalarBounds WeightSum(Vector4 weights)
    {
        Span<CoverageScalarBounds> sums = stackalloc CoverageScalarBounds[16];
        sums.Fill(Empty);
        for (int slot = 0; slot < 4; slot++) sums[1 << slot] = Input(Point(weights[slot]));
        for (int mask = 1; mask < 16; mask++)
        {
            if ((mask & (mask - 1)) == 0) continue;
            for (int left = (mask - 1) & mask; left > 0; left = (left - 1) & mask)
            {
                int right = mask ^ left;
                if (left > right) continue;
                sums[mask] = Union(sums[mask], Sum(sums[left], sums[right]));
            }
        }
        return sums[15];
    }

    static CoverageScalarBounds Sum(CoverageScalarBounds a, CoverageScalarBounds b)
    {
        var exact = Add(a, b);
        return Union(new((float)exact.Min, (float)exact.Max), CoverageActorBounds.Round32Envelope(exact));
    }
}
