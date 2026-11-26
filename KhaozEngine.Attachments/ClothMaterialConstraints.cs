using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

// Each material iteration linearizes all triangle and hinge energies together.
internal sealed class ClothMaterialConstraints(ClothRig rig, ClothWorkLimits limits)
{
    private readonly ClothMaterialSystem _system = new(rig, limits);
    private readonly float[] _multipliers = new float[3 * rig.Membranes.Length + rig.Hinges.Length];
    private readonly ClothMaterialRow[] _rows = new ClothMaterialRow[3 * rig.Membranes.Length + rig.Hinges.Length];

    internal long FactorProducts => _system.FactorProducts;

    internal void Begin() => Array.Clear(_multipliers);

    internal bool Project(Vector3[] positions, Vector3[] opening, float seconds)
    {
        float inverseStepSquared = 1 / (seconds * seconds);
        float damping = rig.Material.DampingSeconds / seconds;
        _system.Begin(damping);
        for (int i = 0; i < rig.Membranes.Length; i++)
            rig.Membranes[i].Add(_system, positions, opening, inverseStepSquared, damping, _multipliers.AsSpan(3 * i, 3),
                _rows.AsSpan(3 * i, 3));
        int firstHinge = 3 * rig.Membranes.Length;
        for (int i = 0; i < rig.Hinges.Length; i++)
            _rows[firstHinge + i] = rig.Hinges[i].Add(_system, positions, opening, inverseStepSquared,
                damping, _multipliers[firstHinge + i]);

        if (!_system.Solve()) return false;
        for (int i = 0; i < _rows.Length; i++)
            _multipliers[i] += _rows[i].UpdateMultiplier(_system, damping);
        for (int i = 0; i < positions.Length; i++)
            positions[i] += _system.Delta(i);
        return true;
    }
}

// Two tangent lengths and their cosine are the independent material coordinates.
internal readonly record struct ClothMembrane(ClothTriangle Face, Vector2 Weft, Vector2 Warp, Vector3 RestMetric,
    Vector3 Compliance, float RestArea)
{
    internal static bool TryCreate(ClothTriangle face, ReadOnlySpan<Vector3> rest, ReadOnlySpan<Vector2> materials,
        ClothMaterial material, out ClothMembrane membrane, out float area)
    {
        Vector2 uv = materials[face.B] - materials[face.A], vv = materials[face.C] - materials[face.A];
        Vector3 pq = rest[face.B] - rest[face.A], pr = rest[face.C] - rest[face.A];
        float determinant = uv.X * vv.Y - uv.Y * vv.X;
        area = .5f * Vector3.Cross(pq, pr).Length();
        membrane = default;
        if (MathF.Abs(determinant) < 1e-10f || !float.IsFinite(determinant) || area < 1e-10f || !float.IsFinite(area))
            return false;

        Vector2 weft = new Vector2(vv.Y, -uv.Y) / determinant;
        Vector2 warp = new Vector2(-vv.X, uv.X) / determinant;
        Vector3 across = weft.X * pq + weft.Y * pr, down = warp.X * pq + warp.Y * pr;
        float acrossLength = across.Length(), downLength = down.Length();
        Vector3 metric = new(acrossLength, downLength, Vector3.Dot(across / acrossLength, down / downLength));
        var compliance = Vector3.One / (area * new Vector3(material.WeftStiffness, material.WarpStiffness, material.ShearStiffness));
        if (!ChainMath.IsFinite(metric) || !ChainMath.IsFinite(compliance))
            return false;

        membrane = new ClothMembrane(face, weft, warp, metric, compliance, area);
        return true;
    }

    internal void Add(ClothMaterialSystem system, Vector3[] positions, Vector3[] opening, float inverseStepSquared,
        float damping, ReadOnlySpan<float> lambda, Span<ClothMaterialRow> rows)
    {
        var (a, b, c) = Face;
        Vector3 ab = positions[b] - positions[a], ac = positions[c] - positions[a];
        Vector3 weft = Weft.X * ab + Weft.Y * ac, warp = Warp.X * ab + Warp.Y * ac;
        float weftLength = weft.Length(), warpLength = warp.Length();
        if (weftLength < 1e-7f || warpLength < 1e-7f)
        {
            rows.Clear();
            return;
        }

        Vector3 u = weft / weftLength, v = warp / warpLength;
        float cosine = Vector3.Dot(u, v);
        Vector3 shearWeft = (v - cosine * u) / weftLength;
        Vector3 shearWarp = (u - cosine * v) / warpLength;
        Vector3 alpha = Compliance * inverseStepSquared;
        Span<ClothNodeGradient> gradient = stackalloc ClothNodeGradient[3];
        rows[0] = AddRow(gradient, Weft.X * u, Weft.Y * u, alpha.X, lambda[0], weftLength - RestMetric.X);
        rows[1] = AddRow(gradient, Warp.X * v, Warp.Y * v, alpha.Y, lambda[1], warpLength - RestMetric.Y);
        rows[2] = AddRow(gradient, Weft.X * shearWeft + Warp.X * shearWarp, Weft.Y * shearWeft + Warp.Y * shearWarp,
            alpha.Z, lambda[2], cosine - RestMetric.Z);

        ClothMaterialRow AddRow(Span<ClothNodeGradient> terms, Vector3 second, Vector3 third, float compliance,
            float multiplier, float error)
        {
            terms[0] = new ClothNodeGradient(a, -second - third);
            terms[1] = new ClothNodeGradient(b, second);
            terms[2] = new ClothNodeGradient(c, third);
            return ClothMaterialRow.Add(system, terms, positions, opening, compliance, multiplier, damping, error);
        }
    }

}

internal readonly record struct ClothHinge(int First, int Second, int Left, int Right, float RestAngle, float Compliance)
{
    internal static ClothHinge Create(int first, int second, int left, int right, ReadOnlySpan<Vector3> rest,
        float bendModulus, float? naturalAngle)
    {
        Vector3 edge = rest[second] - rest[first];
        Vector3 up = Vector3.Cross(edge, rest[left] - rest[first]);
        Vector3 down = Vector3.Cross(rest[right] - rest[first], edge);
        // The dual width is the sum of the adjacent triangle centroid distances to the edge.
        float scale = 3 * edge.LengthSquared() / (up.Length() + down.Length());
        return new ClothHinge(first, second, left, right, naturalAngle ?? Angle(Vector3.Normalize(edge), up, down),
            1 / (bendModulus * scale));
    }

    internal ClothMaterialRow Add(ClothMaterialSystem system, Vector3[] positions, Vector3[] opening,
        float inverseStepSquared, float damping, float lambda)
    {
        Vector3 edge = positions[Second] - positions[First];
        Vector3 left = positions[Left] - positions[First], right = positions[Right] - positions[First];
        Vector3 up = Vector3.Cross(edge, left), down = Vector3.Cross(right, edge);
        float edgeSquared = edge.LengthSquared(), upSquared = up.LengthSquared(), downSquared = down.LengthSquared();
        if (edgeSquared < 1e-14f || upSquared < 1e-18f || downSquared < 1e-18f) return default;

        float length = MathF.Sqrt(edgeSquared);
        float error = MathF.IEEERemainder(Angle(edge / length, up, down) - RestAngle, MathF.Tau);
        Vector3 leftGradient = -length / upSquared * up, rightGradient = -length / downSquared * down;
        float tLeft = Vector3.Dot(edge, left) / edgeSquared, tRight = Vector3.Dot(edge, right) / edgeSquared;
        Vector3 secondGradient = -tLeft * leftGradient - tRight * rightGradient;
        Vector3 firstGradient = -leftGradient - rightGradient - secondGradient;
        Span<ClothNodeGradient> gradient = [new(First, firstGradient), new(Second, secondGradient),
            new(Left, leftGradient), new(Right, rightGradient)];
        return ClothMaterialRow.Add(system, gradient, positions, opening, Compliance * inverseStepSquared,
            lambda, damping, error);
    }

    private static float Angle(Vector3 axis, Vector3 left, Vector3 right) =>
        MathF.Atan2(Vector3.Dot(axis, Vector3.Cross(left, right)), Vector3.Dot(left, right));
}
