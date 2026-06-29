using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Render3D
{
    /// <summary>Bakes a 3D collision shape from a <see cref="PropLoader"/>-normalized prop mesh (base y=0, XZ
    /// centred on origin). Solid props (rocks, logs) get a <see cref="ConvexHullShape"/>; buildings (non-convex:
    /// walls, doorways, interiors) get a <see cref="TriangleMeshShape"/>. The Bepu backend then builds its
    /// internal representation from the shape seam. Offline/tooling only; the runtime reads the baked binary via
    /// <see cref="PropCollisionLoader"/>.</summary>
    public static class PropCollisionBake
    {
        /// <summary>Binary magic: "KECL" (KhaozEngine Collision).</summary>
        public const uint Magic = 0x4B45434C;
        /// <summary>Format version written by this implementation.</summary>
        public const byte Version = 1;

        /// <summary>Triangle count above which a non-walkable-solid mesh is treated as a building and gets a
        /// <see cref="TriangleMeshShape"/> rather than a <see cref="ConvexHullShape"/>. Open tuning item: a
        /// manifest hint ("collisionHint": "building") would be more reliable for complex assets.</summary>
        public const int BuildingTriangleThreshold = 60;

        /// <summary>Maximum number of points passed to <see cref="ConvexHullShape"/> before spatial
        /// downsampling kicks in. The Bepu backend runs its own hull computation; this just limits the input.</summary>
        public const int MaxHullPoints = 64;

        /// <summary>True when the mesh should be treated as a building (non-convex interior, walls, doorways).
        /// First cut: triangle count over the threshold AND the mesh is tall (height > SolidHeightMeters).
        /// Short props (rocks, crates) stay convex-hull even if they happen to have many triangles.
        /// Open tuning item: a manifest hint ("collisionHint": "building") would be more reliable for ambiguous
        /// assets that are tall and complex but not actually buildings.</summary>
        public static bool IsBuilding(GltfMesh normalizedMesh)
        {
            if (normalizedMesh == null) throw new ArgumentNullException(nameof(normalizedMesh));
            int triCount = normalizedMesh.Indices32.Length / 3;
            if (triCount < BuildingTriangleThreshold) return false;
            // Short props (rocks/crates) stay convex-hulls regardless of triangle count.
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (ModelVertex v in normalizedMesh.Vertices)
            {
                if (v.Position.Y < minY) minY = v.Position.Y;
                if (v.Position.Y > maxY) maxY = v.Position.Y;
            }
            return (maxY - minY) > PropSurfaceBakeOptions.Default.SolidHeightMeters;
        }

        /// <summary>True when the mesh is a tree (or any non-walkable-solid prop): a thin trunk near the
        /// base with a canopy spreading out above. Reuses the canopy-spread classification already in
        /// <see cref="PropSurfaceBake.IsWalkableSolid"/> (walkable-solid = rock/log/building; everything
        /// else = tree). Trees get a trunk-only <see cref="CylinderShape"/> so the player can walk under
        /// the canopy instead of into a full-mesh convex hull.</summary>
        public static bool IsTree(GltfMesh normalizedMesh)
        {
            if (normalizedMesh == null) throw new ArgumentNullException(nameof(normalizedMesh));
            return !PropSurfaceBake.IsWalkableSolid(normalizedMesh);
        }

        /// <summary>Bake a collision shape from a normalized prop mesh. Classification priority:
        /// tree -> trunk <see cref="CylinderShape"/>; building -> <see cref="TriangleMeshShape"/>;
        /// rock/solid -> <see cref="ConvexHullShape"/>.</summary>
        public static PhysicsShape Bake(GltfMesh normalizedMesh)
        {
            if (normalizedMesh == null) throw new ArgumentNullException(nameof(normalizedMesh));
            if (IsTree(normalizedMesh))     return BakeTrunkCylinder(normalizedMesh);
            if (IsBuilding(normalizedMesh)) return BakeTriangleMesh(normalizedMesh);
            return BakeConvexHull(normalizedMesh);
        }

        /// <summary>Bake a trunk-only cylinder from a tree mesh: radius = the largest XZ half-extent of
        /// the geometry in the bottom <c>trunkHeightMeters</c> (the trunk slice, not the canopy), length =
        /// the full prop height. Placed base-aligned at runtime (the Bepu backend lifts the cylinder
        /// +Length/2 so it spans base -> top; see ShapeFactory).</summary>
        static CylinderShape BakeTrunkCylinder(GltfMesh mesh)
        {
            const float trunkHeightMeters = 1.0f;
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (ModelVertex v in mesh.Vertices)
            {
                if (v.Position.Y < minY) minY = v.Position.Y;
                if (v.Position.Y > maxY) maxY = v.Position.Y;
            }
            float captureTop = minY + trunkHeightMeters;
            float hx = 0f, hz = 0f;
            foreach (ModelVertex v in mesh.Vertices)
            {
                if (v.Position.Y > captureTop) continue;
                float ax = MathF.Abs(v.Position.X), az = MathF.Abs(v.Position.Z);
                if (ax > hx) hx = ax;
                if (az > hz) hz = az;
            }
            float radius = MathF.Max(hx, hz);   // largest trunk half-extent
            float length = maxY - minY;          // full prop height
            return new CylinderShape(radius, length);
        }

        static ConvexHullShape BakeConvexHull(GltfMesh mesh)
        {
            // Deduplicate vertices by spatial bucketing (deterministic, grid-based).
            var unique = new Dictionary<(int, int, int), Vector3>();
            foreach (ModelVertex v in mesh.Vertices)
            {
                // Bucket at 5 mm resolution to collapse near-duplicate verts.
                int bx = (int)MathF.Round(v.Position.X * 200f);
                int by = (int)MathF.Round(v.Position.Y * 200f);
                int bz = (int)MathF.Round(v.Position.Z * 200f);
                unique.TryAdd((bx, by, bz), v.Position);
            }

            // Sort by bucket key (ascending, lexicographic) before copying so the ordering is
            // a contract, not a Dictionary implementation detail. Required for streaming consistency:
            // server and client must bake the identical shape from the same mesh.
            var sortedKeys = new List<(int, int, int)>(unique.Keys);
            sortedKeys.Sort((a, b) =>
            {
                int c = a.Item1.CompareTo(b.Item1); if (c != 0) return c;
                    c = a.Item2.CompareTo(b.Item2); if (c != 0) return c;
                return a.Item3.CompareTo(b.Item3);
            });

            Vector3[] points = new Vector3[sortedKeys.Count];
            for (int i = 0; i < sortedKeys.Count; i++) points[i] = unique[sortedKeys[i]];

            // If over budget, downsample by striding deterministically.
            if (points.Length > MaxHullPoints)
                points = StrideDownsample(points, MaxHullPoints);

            return new ConvexHullShape(points);
        }

        static TriangleMeshShape BakeTriangleMesh(GltfMesh mesh)
        {
            // Collect unique vertex positions (deduplicated).
            var posMap = new Dictionary<(int, int, int), int>();
            var positions = new List<Vector3>();
            var remapIdx = new int[mesh.Vertices.Length];
            for (int i = 0; i < mesh.Vertices.Length; i++)
            {
                Vector3 p = mesh.Vertices[i].Position;
                int bx = (int)MathF.Round(p.X * 200f);
                int by = (int)MathF.Round(p.Y * 200f);
                int bz = (int)MathF.Round(p.Z * 200f);
                var key = (bx, by, bz);
                if (!posMap.TryGetValue(key, out int mapped))
                {
                    mapped = positions.Count;
                    posMap[key] = mapped;
                    positions.Add(p);
                }
                remapIdx[i] = mapped;
            }

            uint[] src = mesh.Indices32;
            var indices = new int[src.Length];
            for (int i = 0; i < src.Length; i++)
                indices[i] = remapIdx[(int)src[i]];

            return new TriangleMeshShape(positions.ToArray(), indices);
        }

        static Vector3[] StrideDownsample(Vector3[] points, int target)
        {
            int stride = (int)MathF.Ceiling((float)points.Length / target);
            var result = new List<Vector3>(target);
            for (int i = 0; i < points.Length && result.Count < target; i += stride)
                result.Add(points[i]);
            return result.ToArray();
        }

        // Shape kind byte written to the binary.
        const byte KindConvexHull = 1;
        const byte KindTriangleMesh = 2;
        const byte KindCylinder = 3;

        /// <summary>Serialize <paramref name="shape"/> to <paramref name="stream"/> in the KECL binary format.
        /// Magic (uint32 LE) + version (byte) + kind (byte) + payload.</summary>
        public static void Write(PhysicsShape shape, Stream stream)
        {
            if (shape == null) throw new ArgumentNullException(nameof(shape));
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            w.Write(Magic);
            w.Write(Version);
            switch (shape)
            {
                case ConvexHullShape hull:
                    w.Write(KindConvexHull);
                    w.Write(hull.Points.Length);
                    foreach (Vector3 p in hull.Points)
                    { w.Write(p.X); w.Write(p.Y); w.Write(p.Z); }
                    break;
                case CylinderShape cyl:
                    w.Write(KindCylinder);
                    w.Write(cyl.Radius);
                    w.Write(cyl.Length);
                    break;
                case TriangleMeshShape mesh:
                    w.Write(KindTriangleMesh);
                    w.Write(mesh.Vertices.Length);
                    foreach (Vector3 v in mesh.Vertices)
                    { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
                    w.Write(mesh.Indices.Length);
                    foreach (int idx in mesh.Indices)
                        w.Write(idx);
                    break;
                default:
                    throw new NotSupportedException($"PropCollisionBake.Write: unsupported shape type {shape.GetType().Name}");
            }
        }
    }
}
