using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Render3D.Debug;

/// <summary>Converts a PhysicsShape into a colored GltfMesh in the shape's local space for
/// the debug overlay. Headless, no GPU.</summary>
public static class CollisionShapeMesh
{
    const int CircleSegments = 20;

    public static GltfMesh Build(PhysicsShape shape, CollisionOverlayPalette palette)
    {
        var verts = new List<ModelVertex>();
        var indices = new List<uint>();
        Append(shape, Matrix4x4.Identity, palette, verts, indices);
        return new GltfMesh(verts.ToArray(), indices.ToArray());
    }

    static void Append(PhysicsShape shape, Matrix4x4 xform, CollisionOverlayPalette palette,
        List<ModelVertex> verts, List<uint> indices)
    {
        switch (shape)
        {
            case CompoundShape compound:
                foreach (var c in compound.Children)
                    Append(c.Shape, PoseMatrix(c.Local) * xform, palette, verts, indices);
                return;

            case BoxShape box:
                // Centered box, full extents = 2 * HalfExtents.
                Emit(BoxGeometry(box.HalfExtents), Color(palette, shape), xform, verts, indices);
                return;

            case SphereShape sphere:
                Emit(SphereGeometry(sphere.Radius), Color(palette, shape), xform, verts, indices);
                return;

            case CapsuleShape capsule:
                // Cylinder + 2 hemisphere caps, symmetric about origin, total height 2r+len.
                Emit(CapsuleGeometry(capsule.Radius, capsule.Length), Color(palette, shape), xform, verts, indices);
                return;

            case CylinderShape cyl:
                // Base-aligned: spans local Y 0..Length.
                Emit(CylinderGeometry(cyl.Radius, cyl.Length), Color(palette, shape), xform, verts, indices);
                return;

            case ConvexHullShape hull:
                var (hv, hi) = ConvexHull3D.Triangulate(hull.Points);
                Emit((hv, hi), Color(palette, shape), xform, verts, indices);
                return;

            case TriangleMeshShape mesh:
                Emit((mesh.Vertices, mesh.Indices), Color(palette, shape), xform, verts, indices);
                return;

            default:
                throw new NotSupportedException($"Unsupported shape: {shape.GetType().Name}");
        }
    }

    static Vector4 Color(CollisionOverlayPalette p, PhysicsShape s) =>
        p.For(CollisionOverlayPalette.KindOf(s)).ToVector4();

    static Matrix4x4 PoseMatrix(Pose pose) =>
        Matrix4x4.CreateFromQuaternion(pose.Orientation) * Matrix4x4.CreateTranslation(pose.Position);

    static void Emit((Vector3[] V, int[] I) geo, Vector4 color, Matrix4x4 xform,
        List<ModelVertex> verts, List<uint> indices)
    {
        uint b = (uint)verts.Count;
        foreach (var p in geo.V)
        {
            Vector3 wp = Vector3.Transform(p, xform);
            verts.Add(new ModelVertex(wp, Vector3.UnitY, color, Vector2.Zero));
        }
        foreach (var i in geo.I) indices.Add(b + (uint)i);
    }

    /// <summary>Axis-aligned box centered on the origin, full extents = 2 * halfExtents.
    /// 8 vertices (one per corner, shared across faces since this is a wireframe-friendly
    /// debug mesh, not a shaded render mesh), 12 triangles.</summary>
    static (Vector3[], int[]) BoxGeometry(Vector3 halfExtents)
    {
        float x = halfExtents.X, y = halfExtents.Y, z = halfExtents.Z;
        var v = new[]
        {
            new Vector3(-x, -y, -z), new Vector3(x, -y, -z), new Vector3(x, y, -z), new Vector3(-x, y, -z),
            new Vector3(-x, -y, z), new Vector3(x, -y, z), new Vector3(x, y, z), new Vector3(-x, y, z),
        };
        var i = new[]
        {
            // -Z
            0, 2, 1, 0, 3, 2,
            // +Z
            4, 5, 6, 4, 6, 7,
            // -X
            0, 4, 7, 0, 7, 3,
            // +X
            1, 2, 6, 1, 6, 5,
            // -Y
            0, 1, 5, 0, 5, 4,
            // +Y
            3, 7, 6, 3, 6, 2,
        };
        return (v, i);
    }

    /// <summary>UV sphere centered on the origin, radius <paramref name="radius"/>.</summary>
    static (Vector3[], int[]) SphereGeometry(float radius)
    {
        int rings = CircleSegments / 2;
        int segs = CircleSegments;
        int cols = segs + 1;
        var verts = new Vector3[(rings + 1) * cols];
        var inds = new List<int>();

        for (int r = 0; r <= rings; r++)
        {
            float phi = MathF.PI * r / rings; // 0..PI
            float y = MathF.Cos(phi);
            float rr = MathF.Sin(phi);
            for (int s = 0; s <= segs; s++)
            {
                float theta = MathF.Tau * s / segs;
                var dir = new Vector3(rr * MathF.Cos(theta), y, rr * MathF.Sin(theta));
                verts[r * cols + s] = dir * radius;
            }
        }

        for (int r = 0; r < rings; r++)
        for (int s = 0; s < segs; s++)
        {
            int i0 = r * cols + s;
            int i1 = r * cols + s + 1;
            int i2 = (r + 1) * cols + s;
            int i3 = (r + 1) * cols + s + 1;
            inds.Add(i0); inds.Add(i3); inds.Add(i2);
            inds.Add(i0); inds.Add(i1); inds.Add(i3);
        }

        return (verts, inds.ToArray());
    }

    /// <summary>Cylinder base-aligned along local Y: spans Y 0..length, radius <paramref name="radius"/>.</summary>
    static (Vector3[], int[]) CylinderGeometry(float radius, float length)
    {
        int segs = CircleSegments;
        var verts = new Vector3[segs * 2 + 2]; // ring bottom, ring top, + 2 cap centers
        var inds = new List<int>();

        int bottomCenter = segs * 2;
        int topCenter = segs * 2 + 1;

        for (int s = 0; s < segs; s++)
        {
            float theta = MathF.Tau * s / segs;
            float cx = MathF.Cos(theta) * radius;
            float cz = MathF.Sin(theta) * radius;
            verts[s] = new Vector3(cx, 0f, cz);           // bottom ring
            verts[segs + s] = new Vector3(cx, length, cz); // top ring
        }
        verts[bottomCenter] = new Vector3(0f, 0f, 0f);
        verts[topCenter] = new Vector3(0f, length, 0f);

        // Side walls.
        for (int s = 0; s < segs; s++)
        {
            int s0 = s;
            int s1 = (s + 1) % segs;
            int b0 = s0, b1 = s1;
            int t0 = segs + s0, t1 = segs + s1;
            inds.Add(b0); inds.Add(t0); inds.Add(t1);
            inds.Add(b0); inds.Add(t1); inds.Add(b1);
        }

        // Bottom cap (fan, facing -Y).
        for (int s = 0; s < segs; s++)
        {
            int s0 = s;
            int s1 = (s + 1) % segs;
            inds.Add(bottomCenter); inds.Add(s1); inds.Add(s0);
        }

        // Top cap (fan, facing +Y).
        for (int s = 0; s < segs; s++)
        {
            int s0 = segs + s;
            int s1 = segs + (s + 1) % segs;
            inds.Add(topCenter); inds.Add(s0); inds.Add(s1);
        }

        return (verts, inds.ToArray());
    }

    /// <summary>Capsule: cylindrical body + 2 hemisphere caps along local Y, symmetric about the
    /// origin. Total height = 2*radius + length (bottom hemisphere south pole at -total/2, top
    /// hemisphere north pole at +total/2).</summary>
    static (Vector3[], int[]) CapsuleGeometry(float radius, float length)
    {
        int segs = CircleSegments;
        int hemiRings = Math.Max(1, CircleSegments / 4);

        float half = length * 0.5f;
        float yBottom = -half; // center of the bottom hemisphere
        float yTop = half;     // center of the top hemisphere

        var rowStarts = new List<int>();
        var verts = new List<Vector3>();
        var inds = new List<int>();

        // Bottom hemisphere: phi from PI (south pole) to PI/2 (equator).
        for (int r = 0; r <= hemiRings; r++)
        {
            float phi = MathF.PI - (MathF.PI * 0.5f) * r / hemiRings;
            EmitRing(verts, rowStarts, segs, yBottom, phi, radius);
        }
        // Top hemisphere: phi from PI/2 (equator) to 0 (north pole). Skip the equator (already emitted).
        for (int r = 1; r <= hemiRings; r++)
        {
            float phi = (MathF.PI * 0.5f) - (MathF.PI * 0.5f) * r / hemiRings;
            EmitRing(verts, rowStarts, segs, yTop, phi, radius);
        }

        int totalRows = rowStarts.Count;
        for (int r = 0; r < totalRows - 1; r++)
        {
            int a0 = rowStarts[r];
            int b0 = rowStarts[r + 1];
            for (int s = 0; s < segs; s++)
            {
                int s1 = (s + 1) % segs;
                int i0 = a0 + s, i1 = a0 + s1, i2 = b0 + s, i3 = b0 + s1;
                inds.Add(i0); inds.Add(i2); inds.Add(i3);
                inds.Add(i0); inds.Add(i3); inds.Add(i1);
            }
        }

        return (verts.ToArray(), inds.ToArray());
    }

    static void EmitRing(List<Vector3> verts, List<int> rowStarts, int segs, float yCenter, float phi, float radius)
    {
        rowStarts.Add(verts.Count);
        float yc = MathF.Cos(phi) * radius;
        float rr = MathF.Sin(phi) * radius;
        float y = yCenter + yc;
        for (int s = 0; s < segs; s++)
        {
            float theta = MathF.Tau * s / segs;
            verts.Add(new Vector3(MathF.Cos(theta) * rr, y, MathF.Sin(theta) * rr));
        }
    }
}
