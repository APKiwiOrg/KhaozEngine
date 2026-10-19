using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Cold validated integer palette indices. Source float indices never enter a device kernel.</summary>
internal sealed class CoverageReferenceSkin
{
    internal CoverageReferenceSkin(CoverageSourceTopology source, int boneCount)
    {
        ArgumentNullException.ThrowIfNull(source);
        Source = source;
        BoneCount = boneCount;
        Indices = new int[source.SkinVertices.Length * 4];
        for (int v = 0; v < source.SkinVertices.Length; v++)
        {
            Vector4 indices = source.SkinVertices[v].BoneIndices;
            if (!SkinningMath.AreBoneIndicesValid(indices, boneCount))
                throw new ArgumentException("Reference skin indices must be inside the palette.", nameof(source));
            Indices[v * 4] = (int)indices.X; Indices[v * 4 + 1] = (int)indices.Y;
            Indices[v * 4 + 2] = (int)indices.Z; Indices[v * 4 + 3] = (int)indices.W;
        }
    }
    internal CoverageSourceTopology Source { get; }
    internal int BoneCount { get; }
    internal int[] Indices { get; }
}

/// <summary>Pinned scalar CPU palette composition and independently gathered source-vertex skinning.</summary>
internal static class CoverageReferenceSkinning
{
    internal static void Compose(ReadOnlySpan<Matrix4x4> jointWorld, ReadOnlySpan<Matrix4x4> inverseBind, Span<Matrix4x4> composed)
    {
        if (jointWorld.Length != inverseBind.Length || composed.Length != jointWorld.Length)
            throw new ArgumentException("Palette composition requires identical input and output lengths.");
        for (int bone = 0; bone < composed.Length; bone++) composed[bone] = Compose(jointWorld[bone], inverseBind[bone]);
    }

    internal static Matrix4x4 Compose(in Matrix4x4 jointWorld, in Matrix4x4 inverseBind)
    {
        var j = jointWorld; var b = inverseBind;
        return new(
            (((b.M11 * j.M11) + (b.M12 * j.M21)) + (b.M13 * j.M31)) + (b.M14 * j.M41),
            (((b.M11 * j.M12) + (b.M12 * j.M22)) + (b.M13 * j.M32)) + (b.M14 * j.M42),
            (((b.M11 * j.M13) + (b.M12 * j.M23)) + (b.M13 * j.M33)) + (b.M14 * j.M43),
            (((b.M11 * j.M14) + (b.M12 * j.M24)) + (b.M13 * j.M34)) + (b.M14 * j.M44),
            (((b.M21 * j.M11) + (b.M22 * j.M21)) + (b.M23 * j.M31)) + (b.M24 * j.M41),
            (((b.M21 * j.M12) + (b.M22 * j.M22)) + (b.M23 * j.M32)) + (b.M24 * j.M42),
            (((b.M21 * j.M13) + (b.M22 * j.M23)) + (b.M23 * j.M33)) + (b.M24 * j.M43),
            (((b.M21 * j.M14) + (b.M22 * j.M24)) + (b.M23 * j.M34)) + (b.M24 * j.M44),
            (((b.M31 * j.M11) + (b.M32 * j.M21)) + (b.M33 * j.M31)) + (b.M34 * j.M41),
            (((b.M31 * j.M12) + (b.M32 * j.M22)) + (b.M33 * j.M32)) + (b.M34 * j.M42),
            (((b.M31 * j.M13) + (b.M32 * j.M23)) + (b.M33 * j.M33)) + (b.M34 * j.M43),
            (((b.M31 * j.M14) + (b.M32 * j.M24)) + (b.M33 * j.M34)) + (b.M34 * j.M44),
            (((b.M41 * j.M11) + (b.M42 * j.M21)) + (b.M43 * j.M31)) + (b.M44 * j.M41),
            (((b.M41 * j.M12) + (b.M42 * j.M22)) + (b.M43 * j.M32)) + (b.M44 * j.M42),
            (((b.M41 * j.M13) + (b.M42 * j.M23)) + (b.M43 * j.M33)) + (b.M44 * j.M43),
            (((b.M41 * j.M14) + (b.M42 * j.M24)) + (b.M43 * j.M34)) + (b.M44 * j.M44));
    }

    internal static bool TrySkin(CoverageReferenceArithmetic f, CoverageReferenceSkin skin, CoverageSourceTopology source,
        ReadOnlySpan<Matrix4x4> composed, Span<ModelVertex> posed, out CoverageFailureCode code)
    {
        ArgumentNullException.ThrowIfNull(f);
        ArgumentNullException.ThrowIfNull(skin);
        ArgumentNullException.ThrowIfNull(source);
        if (!ReferenceEquals(skin.Source, source) || source.SkinVertices.Length != source.Positions.Length
            || posed.Length != source.SkinVertices.Length || composed.Length != skin.BoneCount)
            throw new ArgumentException("Reference skinning requires its source, palette and exact output length.");
        for (int v = 0; v < source.SkinVertices.Length; v++)
            if (!TrySkinVertex(f, source.SkinVertices[v], v, skin.Indices, composed, out posed[v]))
            { code = CoverageFailureCode.UnsupportedTransform; return false; }
        code = default;
        return true;
    }

    [Binary32Kernel]
    static bool TrySkinVertex(CoverageReferenceArithmetic f, in SkinnedVertex v, int vertex, int[] indices,
        ReadOnlySpan<Matrix4x4> composed, out ModelVertex posed)
    {
        f.At(CoverageExactnessPhase.Skinning, vertex);
        Vector4 w = v.BoneWeights;
        float total = f.Add(f.Add(f.Add(w.X, w.Y), w.Z), w.W);
        Matrix4x4 m = new Matrix4x4(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
        if (!f.Less(total, 1e-8f))
        {
            Matrix4x4 a = composed[indices[vertex * 4]], b = composed[indices[vertex * 4 + 1]],
                c = composed[indices[vertex * 4 + 2]], d = composed[indices[vertex * 4 + 3]];
            m = new Matrix4x4(
                f.Add(f.Add(f.Add(f.Mul(a.M11, w.X), f.Mul(b.M11, w.Y)), f.Mul(c.M11, w.Z)), f.Mul(d.M11, w.W)),
                f.Add(f.Add(f.Add(f.Mul(a.M12, w.X), f.Mul(b.M12, w.Y)), f.Mul(c.M12, w.Z)), f.Mul(d.M12, w.W)),
                f.Add(f.Add(f.Add(f.Mul(a.M13, w.X), f.Mul(b.M13, w.Y)), f.Mul(c.M13, w.Z)), f.Mul(d.M13, w.W)),
                0,
                f.Add(f.Add(f.Add(f.Mul(a.M21, w.X), f.Mul(b.M21, w.Y)), f.Mul(c.M21, w.Z)), f.Mul(d.M21, w.W)),
                f.Add(f.Add(f.Add(f.Mul(a.M22, w.X), f.Mul(b.M22, w.Y)), f.Mul(c.M22, w.Z)), f.Mul(d.M22, w.W)),
                f.Add(f.Add(f.Add(f.Mul(a.M23, w.X), f.Mul(b.M23, w.Y)), f.Mul(c.M23, w.Z)), f.Mul(d.M23, w.W)),
                0,
                f.Add(f.Add(f.Add(f.Mul(a.M31, w.X), f.Mul(b.M31, w.Y)), f.Mul(c.M31, w.Z)), f.Mul(d.M31, w.W)),
                f.Add(f.Add(f.Add(f.Mul(a.M32, w.X), f.Mul(b.M32, w.Y)), f.Mul(c.M32, w.Z)), f.Mul(d.M32, w.W)),
                f.Add(f.Add(f.Add(f.Mul(a.M33, w.X), f.Mul(b.M33, w.Y)), f.Mul(c.M33, w.Z)), f.Mul(d.M33, w.W)),
                0,
                f.Add(f.Add(f.Add(f.Mul(a.M41, w.X), f.Mul(b.M41, w.Y)), f.Mul(c.M41, w.Z)), f.Mul(d.M41, w.W)),
                f.Add(f.Add(f.Add(f.Mul(a.M42, w.X), f.Mul(b.M42, w.Y)), f.Mul(c.M42, w.Z)), f.Mul(d.M42, w.W)),
                f.Add(f.Add(f.Add(f.Mul(a.M43, w.X), f.Mul(b.M43, w.Y)), f.Mul(c.M43, w.Z)), f.Mul(d.M43, w.W)),
                1);
        }
        Vector3 p = CoverageReferenceMath.Point(f, v.Position, m), n = CoverageReferenceMath.Linear(f, v.Normal, m);
        float length = f.Sqrt(CoverageReferenceMath.Dot3(f, n, n));
        n = f.Greater(length, 1e-8f) ? new Vector3(f.Div(n.X, length), f.Div(n.Y, length), f.Div(n.Z, length)) : v.Normal;
        Vector3 t = new Vector3(v.Tangent.X, v.Tangent.Y, v.Tangent.Z);
        Vector4 tangent = default;
        if (f.Greater(CoverageReferenceMath.Dot3(f, t, t), 1e-12f))
        {
            Vector3 transformed = CoverageReferenceMath.Linear(f, t, m);
            float tl = f.Sqrt(CoverageReferenceMath.Dot3(f, transformed, transformed));
            transformed = f.Greater(tl, 1e-8f) ? new Vector3(f.Div(transformed.X, tl), f.Div(transformed.Y, tl), f.Div(transformed.Z, tl)) : t;
            tangent = new Vector4(transformed, v.Tangent.W);
        }
        posed = new ModelVertex(p, n, v.Color, v.Uv, tangent);
        return CoverageReferenceMath.ReadVertex(f, ref posed);
    }
}
