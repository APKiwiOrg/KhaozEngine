using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapEditor;

/// <summary>Vertex-colored <see cref="GltfMesh"/> builders for the transform gizmo, built once and uploaded by
/// the viewport (the <c>CollisionShapeOverlay</c> pattern: <see cref="Scene3D.DrawOverlayMesh"/> takes color from
/// the vertex color only, with no draw-time tint, which is why every builder bakes its color into the vertices).
/// All meshes are authored in the gizmo's local space around the origin at unit scale, so the viewport draws them
/// with a single world matrix that translates to the gizmo position and multiplies by the screen-constant scale.
/// The handle dimensions are shared with <see cref="GizmoDrag.HitTest"/> through the constants here, so the visible
/// mesh and the pickable region can never drift apart.</summary>
public static class GizmoGeometry
{
    /// <summary>Length each translate arrow extends from the origin along its axis (at gizmo scale 1).</summary>
    public const float ArrowLength = 1.2f;

    /// <summary>Half-width of an arrow's pick box on the two axes perpendicular to its shaft.</summary>
    public const float ArrowHalfWidth = 0.15f;

    /// <summary>Radius of the flat yaw ring (at gizmo scale 1).</summary>
    public const float RingRadius = 1.0f;

    /// <summary>Half-width of the yaw ring's radial pick band (a ring hit is a flat annulus test, not a box).</summary>
    public const float RingBandHalfWidth = 0.15f;

    /// <summary>The scale cube's corner offset along +X and +Z from the origin (at gizmo scale 1).</summary>
    public const float ScaleCubeOffset = 0.85f;

    /// <summary>Half-extent of the scale cube on every axis (at gizmo scale 1).</summary>
    public const float ScaleCubeHalfExtent = 0.12f;

    /// <summary>Baked vertex color of the +X (right) translate arrow: red.</summary>
    public static readonly Vector4 AxisXColor = new(0.85f, 0.18f, 0.18f, 1f);

    /// <summary>Baked vertex color of the +Y (up) translate arrow: green.</summary>
    public static readonly Vector4 AxisYColor = new(0.30f, 0.80f, 0.30f, 1f);

    /// <summary>Baked vertex color of the +Z (forward) translate arrow: blue.</summary>
    public static readonly Vector4 AxisZColor = new(0.25f, 0.45f, 0.95f, 1f);

    /// <summary>Baked vertex color of the yaw ring: yellow.</summary>
    public static readonly Vector4 YawColor = new(0.95f, 0.80f, 0.20f, 1f);

    /// <summary>Baked vertex color of the corner scale cube: near-white.</summary>
    public static readonly Vector4 ScaleColor = new(0.90f, 0.90f, 0.96f, 1f);

    /// <summary>Baked vertex color of the selection marker pyramid: orange.</summary>
    public static readonly Vector4 MarkerColor = new(0.95f, 0.55f, 0.15f, 1f);

    // Arrow shaft/head proportions (local space, gizmo scale 1). The shaft box runs from the origin to
    // ShaftTop; the pyramid head runs from ShaftTop up to ArrowLength.
    const float ShaftHalfWidth = 0.03f;
    const float HeadLength = 0.3f;
    const float HeadHalfWidth = 0.09f;

    // Selection marker pyramid.
    const float MarkerHalfBase = 0.3f;
    const float MarkerHeight = 0.8f;

    // Yaw ring tessellation.
    const int RingSegments = 48;

    /// <summary>The three axis arrows on one mesh: +X red and +Z blue on the ground plane, +Y green up. Each
    /// arrow is a thin shaft box capped by a pyramid head, oriented by rotating a canonical +Y arrow onto its
    /// axis, with its axis color baked into every vertex.</summary>
    public static GltfMesh TranslateArrows()
    {
        var v = new List<ModelVertex>();
        var i = new List<uint>();
        EmitArrow(v, i, Matrix4x4.CreateRotationZ(-MathF.PI / 2f), AxisXColor); // canonical +Y arrow rotated onto +X
        EmitArrow(v, i, Matrix4x4.Identity, AxisYColor);                        // +Y
        EmitArrow(v, i, Matrix4x4.CreateRotationX(MathF.PI / 2f), AxisZColor);  // rotated onto +Z
        return new GltfMesh(v.ToArray(), i.ToArray());
    }

    /// <summary>The two ground-plane translate arrows only: +X red and +Z blue, no +Y arrow. Every affordance
    /// that draws this mesh (a spawn's marker-plus-drag, or a feature / disc / rect shape's move + scale) has its
    /// vertical handle blocked by <c>EditorToolController.RestrictHandle</c>, so the mesh never offers a handle
    /// the drag policy refuses.</summary>
    public static GltfMesh TranslateArrowsXZ()
    {
        var v = new List<ModelVertex>();
        var i = new List<uint>();
        EmitArrow(v, i, Matrix4x4.CreateRotationZ(-MathF.PI / 2f), AxisXColor); // canonical +Y arrow rotated onto +X
        EmitArrow(v, i, Matrix4x4.CreateRotationX(MathF.PI / 2f), AxisZColor);  // rotated onto +Z
        return new GltfMesh(v.ToArray(), i.ToArray());
    }

    /// <summary>A flat, double-sided annulus in the ground plane (y = 0) at <see cref="RingRadius"/>, spanning the
    /// pick band width, with <see cref="YawColor"/> baked into every vertex.</summary>
    public static GltfMesh YawRing()
    {
        float inner = RingRadius - RingBandHalfWidth;
        float outer = RingRadius + RingBandHalfWidth;
        var v = new List<ModelVertex>();
        var idx = new List<uint>();
        for (int s = 0; s < RingSegments; s++)
        {
            float a0 = MathF.Tau * s / RingSegments;
            float a1 = MathF.Tau * (s + 1) / RingSegments;
            Vector3 i0 = new(MathF.Cos(a0) * inner, 0f, MathF.Sin(a0) * inner);
            Vector3 o0 = new(MathF.Cos(a0) * outer, 0f, MathF.Sin(a0) * outer);
            Vector3 i1 = new(MathF.Cos(a1) * inner, 0f, MathF.Sin(a1) * inner);
            Vector3 o1 = new(MathF.Cos(a1) * outer, 0f, MathF.Sin(a1) * outer);
            AddQuad(v, idx, i0, o0, o1, i1, Vector3.UnitY, YawColor);   // top face (+Y)
            AddQuad(v, idx, i0, i1, o1, o0, -Vector3.UnitY, YawColor);  // bottom face (-Y), reversed so it faces down
        }
        return new GltfMesh(v.ToArray(), idx.ToArray());
    }

    /// <summary>The corner scale cube: an axis-aligned box centred at (<see cref="ScaleCubeOffset"/>, 0,
    /// <see cref="ScaleCubeOffset"/>) with <see cref="ScaleCubeHalfExtent"/> half-extent, <see cref="ScaleColor"/>
    /// baked in.</summary>
    public static GltfMesh ScaleHandle()
    {
        var v = new List<ModelVertex>();
        var i = new List<uint>();
        var center = new Vector3(ScaleCubeOffset, 0f, ScaleCubeOffset);
        var half = new Vector3(ScaleCubeHalfExtent);
        EmitBox(v, i, Matrix4x4.Identity, center - half, center + half, ScaleColor);
        return new GltfMesh(v.ToArray(), i.ToArray());
    }

    /// <summary>A small upright pyramid to mark the selected spawn/feature, based at y = 0 with its apex up,
    /// <see cref="MarkerColor"/> baked in.</summary>
    public static GltfMesh SelectionMarker()
    {
        var v = new List<ModelVertex>();
        var i = new List<uint>();
        EmitPyramid(v, i, Matrix4x4.Identity, MarkerHalfBase, 0f, MarkerHeight, MarkerColor);
        return new GltfMesh(v.ToArray(), i.ToArray());
    }

    /// <summary>Emits a canonical +Y arrow (shaft box + pyramid head) transformed by <paramref name="m"/> and
    /// colored <paramref name="color"/>.</summary>
    static void EmitArrow(List<ModelVertex> v, List<uint> i, Matrix4x4 m, Vector4 color)
    {
        float shaftTop = ArrowLength - HeadLength;
        EmitBox(v, i, m, new Vector3(-ShaftHalfWidth, 0f, -ShaftHalfWidth),
                         new Vector3(ShaftHalfWidth, shaftTop, ShaftHalfWidth), color);
        EmitPyramid(v, i, m, HeadHalfWidth, shaftTop, ArrowLength, color);
    }

    /// <summary>Emits an axis-aligned box spanning [<paramref name="min"/>, <paramref name="max"/>] as six
    /// outward-facing quads, transformed by <paramref name="m"/> and colored <paramref name="color"/>.</summary>
    static void EmitBox(List<ModelVertex> v, List<uint> i, Matrix4x4 m, Vector3 min, Vector3 max, Vector4 color)
    {
        void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n)
        {
            uint b0 = (uint)v.Count;
            Vector3 tn = Vector3.Normalize(Vector3.TransformNormal(n, m));
            v.Add(new ModelVertex(Vector3.Transform(a, m), tn, color));
            v.Add(new ModelVertex(Vector3.Transform(b, m), tn, color));
            v.Add(new ModelVertex(Vector3.Transform(c, m), tn, color));
            v.Add(new ModelVertex(Vector3.Transform(d, m), tn, color));
            i.Add(b0); i.Add(b0 + 1); i.Add(b0 + 2);
            i.Add(b0); i.Add(b0 + 2); i.Add(b0 + 3);
        }

        Face(new Vector3(max.X, min.Y, max.Z), new Vector3(max.X, min.Y, min.Z),
             new Vector3(max.X, max.Y, min.Z), new Vector3(max.X, max.Y, max.Z), Vector3.UnitX);
        Face(new Vector3(min.X, min.Y, min.Z), new Vector3(min.X, min.Y, max.Z),
             new Vector3(min.X, max.Y, max.Z), new Vector3(min.X, max.Y, min.Z), -Vector3.UnitX);
        Face(new Vector3(min.X, max.Y, max.Z), new Vector3(max.X, max.Y, max.Z),
             new Vector3(max.X, max.Y, min.Z), new Vector3(min.X, max.Y, min.Z), Vector3.UnitY);
        Face(new Vector3(min.X, min.Y, min.Z), new Vector3(max.X, min.Y, min.Z),
             new Vector3(max.X, min.Y, max.Z), new Vector3(min.X, min.Y, max.Z), -Vector3.UnitY);
        Face(new Vector3(min.X, min.Y, max.Z), new Vector3(max.X, min.Y, max.Z),
             new Vector3(max.X, max.Y, max.Z), new Vector3(min.X, max.Y, max.Z), Vector3.UnitZ);
        Face(new Vector3(max.X, min.Y, min.Z), new Vector3(min.X, min.Y, min.Z),
             new Vector3(min.X, max.Y, min.Z), new Vector3(max.X, max.Y, min.Z), -Vector3.UnitZ);
    }

    /// <summary>Emits a square-based pyramid: base of half-extent <paramref name="half"/> at y =
    /// <paramref name="baseY"/>, apex at (0, <paramref name="apexY"/>, 0), transformed by <paramref name="m"/>
    /// and colored <paramref name="color"/>. Four outward side triangles plus a downward base quad.</summary>
    static void EmitPyramid(List<ModelVertex> v, List<uint> i, Matrix4x4 m, float half, float baseY, float apexY, Vector4 color)
    {
        var apex = new Vector3(0f, apexY, 0f);
        var c0 = new Vector3(-half, baseY, -half);
        var c1 = new Vector3(half, baseY, -half);
        var c2 = new Vector3(half, baseY, half);
        var c3 = new Vector3(-half, baseY, half);

        void Tri(Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 n = Vector3.Normalize(Vector3.TransformNormal(Vector3.Normalize(Vector3.Cross(b - a, c - a)), m));
            uint b0 = (uint)v.Count;
            v.Add(new ModelVertex(Vector3.Transform(a, m), n, color));
            v.Add(new ModelVertex(Vector3.Transform(b, m), n, color));
            v.Add(new ModelVertex(Vector3.Transform(c, m), n, color));
            i.Add(b0); i.Add(b0 + 1); i.Add(b0 + 2);
        }

        // Sides wound so the face normal points outward away from the axis.
        Tri(c1, c0, apex); // -Z
        Tri(c2, c1, apex); // +X
        Tri(c3, c2, apex); // +Z
        Tri(c0, c3, apex); // -X

        // Base quad, facing -Y.
        Vector3 nd = Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitY, m));
        uint bi = (uint)v.Count;
        v.Add(new ModelVertex(Vector3.Transform(c0, m), nd, color));
        v.Add(new ModelVertex(Vector3.Transform(c3, m), nd, color));
        v.Add(new ModelVertex(Vector3.Transform(c2, m), nd, color));
        v.Add(new ModelVertex(Vector3.Transform(c1, m), nd, color));
        i.Add(bi); i.Add(bi + 2); i.Add(bi + 1);
        i.Add(bi); i.Add(bi + 3); i.Add(bi + 2);
    }

    /// <summary>Emits one colored quad (a, b, c, d) with a shared normal <paramref name="n"/> as two triangles.</summary>
    static void AddQuad(List<ModelVertex> v, List<uint> i, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n, Vector4 color)
    {
        uint b0 = (uint)v.Count;
        v.Add(new ModelVertex(a, n, color));
        v.Add(new ModelVertex(b, n, color));
        v.Add(new ModelVertex(c, n, color));
        v.Add(new ModelVertex(d, n, color));
        i.Add(b0); i.Add(b0 + 1); i.Add(b0 + 2);
        i.Add(b0); i.Add(b0 + 2); i.Add(b0 + 3);
    }
}
