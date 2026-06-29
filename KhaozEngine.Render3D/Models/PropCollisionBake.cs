using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Render3D
{
    /// <summary>Bakes a 3D collision shape from a <see cref="PropLoader"/>-normalized prop mesh (base y=0, XZ
    /// centred on origin). Trees get a trunk-only <see cref="CylinderShape"/> (walk under the canopy); buildings
    /// (tall, non-convex: walls, doorways, interiors) get a <see cref="TriangleMeshShape"/>; every other short
    /// solid prop (rocks, logs) gets a <see cref="ConvexHullShape"/>. A convex hull can never trap the capsule
    /// (there is always a unique shortest exit), unlike a one-sided non-convex triangle mesh whose conflicting
    /// per-triangle contacts can suck the capsule through the near face and pin it inside. The hull is the TRUE
    /// minimal hull of the full deduplicated vertex set (Bepu's hull helper discards interior points), not a
    /// stride-downsampled under-approximation, so it is a smooth convex polytope rather than a lumpy one.
    /// The Bepu backend then builds its internal representation from the shape seam. Offline/tooling only; the
    /// runtime reads the baked binary via <see cref="PropCollisionLoader"/>.</summary>
    public static class PropCollisionBake
    {
        /// <summary>Binary magic: "KECL" (KhaozEngine Collision).</summary>
        public const uint Magic = 0x4B45434C;
        /// <summary>Format version written by this implementation.</summary>
        public const byte Version = 1;

        /// <summary>Triangle count above which a non-walkable-solid mesh is treated as a building and gets a
        /// <see cref="TriangleMeshShape"/> (concave interiors) rather than a <see cref="ConvexHullShape"/>. Open
        /// tuning item: a manifest hint ("collisionHint": "building") would be more reliable for complex assets.</summary>
        public const int BuildingTriangleThreshold = 60;

        /// <summary>Upper bound on the point count handed to <see cref="ConvexHullShape"/>. These rocks are
        /// low-poly (a few hundred verts), so the cap effectively never trips and the full deduplicated set is
        /// passed; the Bepu backend then computes the true minimal hull and discards interior points. The cap is
        /// a guard for pathologically high-poly meshes only, and when it trips it keeps the spatially-EXTREME
        /// points (largest distance from the centroid), NEVER strided points - striding discards the very extreme
        /// points that define the hull and made the old bake lumpy.</summary>
        public const int MaxHullPoints = 256;

        /// <summary>Fraction of a tree's height taken as the trunk slice for the trunk-radius bake, capped at
        /// <see cref="TrunkSliceMaxMeters"/>. The slice is the dense central trunk near the base; conifer foliage
        /// (the lowest branches) sits above it but can still spread out within the cap, which is why the radius is
        /// a low percentile (rejects sparse foliage outliers) rather than the max.</summary>
        public const float TrunkSliceFraction = 0.2f;

        /// <summary>Hard cap (metres) on the trunk slice height regardless of prop height.</summary>
        public const float TrunkSliceMaxMeters = 1.0f;

        /// <summary>Percentile (0..1) of the sliced vertices' euclidean XZ radius taken as the trunk radius. The
        /// trunk is the dense central cluster; foliage points are sparse outliers far from the axis, so a low
        /// percentile lands on the trunk core and rejects the foliage spread that the old max grabbed.</summary>
        public const float TrunkRadiusPercentile = 0.30f;

        /// <summary>Floor (metres) for the baked trunk radius so a degenerate mesh never yields a zero-radius
        /// cylinder.</summary>
        public const float TrunkRadiusFloor = 0.12f;

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

        /// <summary>True when the mesh should be treated as a building (non-convex interior, walls, doorways).
        /// First cut: triangle count over <see cref="BuildingTriangleThreshold"/> AND the mesh is tall (height >
        /// <see cref="PropSurfaceBakeOptions.SolidHeightMeters"/>). Short props (rocks, crates) stay convex-hull
        /// even when they happen to have many triangles. A building needs a <see cref="TriangleMeshShape"/> for its
        /// concave interior; a rock does not, and a mesh on a rock can trap the capsule, so rocks bake a convex
        /// hull. Open tuning item: a manifest hint ("collisionHint": "building") would be more reliable for
        /// ambiguous assets that are tall and complex but not actually buildings.</summary>
        public static bool IsBuilding(GltfMesh normalizedMesh)
        {
            if (normalizedMesh == null) throw new ArgumentNullException(nameof(normalizedMesh));
            int triCount = normalizedMesh.Indices32.Length / 3;
            if (triCount < BuildingTriangleThreshold) return false;
            // Short props (rocks/crates) stay convex hulls regardless of triangle count.
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (ModelVertex v in normalizedMesh.Vertices)
            {
                if (v.Position.Y < minY) minY = v.Position.Y;
                if (v.Position.Y > maxY) maxY = v.Position.Y;
            }
            return (maxY - minY) > PropSurfaceBakeOptions.Default.SolidHeightMeters;
        }

        /// <summary>Bake a collision shape from a normalized prop mesh. Classification priority:
        /// tree -> trunk <see cref="CylinderShape"/> (walk under the canopy); building -> <see cref="TriangleMeshShape"/>
        /// (concave interior); rock/short-solid -> <see cref="ConvexHullShape"/> (a convex shape can never trap
        /// the capsule, unlike a one-sided non-convex mesh).</summary>
        public static PhysicsShape Bake(GltfMesh normalizedMesh)
        {
            if (normalizedMesh == null) throw new ArgumentNullException(nameof(normalizedMesh));
            if (IsTree(normalizedMesh))     return BakeTrunkCylinder(normalizedMesh);
            if (IsBuilding(normalizedMesh)) return BakeTriangleMesh(normalizedMesh);
            return BakeConvexHull(normalizedMesh);
        }

        /// <summary>Bake a trunk-only cylinder from a tree mesh. Radius = the
        /// <see cref="TrunkRadiusPercentile"/> percentile of the euclidean XZ radius over the bottom-slice
        /// vertices (slice = <c>min(<see cref="TrunkSliceMaxMeters"/>, <see cref="TrunkSliceFraction"/> * height)</c>),
        /// floored at <see cref="TrunkRadiusFloor"/>; length = the full prop height. The percentile (not the max)
        /// rejects the sparse low foliage/branch outliers that spread far from the axis and made the old
        /// <c>max(|x|,|z|)</c> trunk far too fat (block radius ~0.9-1.5 m for a ~0.3-0.5 m trunk). Deterministic:
        /// a pure vertex walk plus a value sort. Placed base-aligned at runtime (the Bepu backend lifts the
        /// cylinder +Length/2 so it spans base -> top; see ShapeFactory, unchanged).</summary>
        static CylinderShape BakeTrunkCylinder(GltfMesh mesh)
        {
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (ModelVertex v in mesh.Vertices)
            {
                if (v.Position.Y < minY) minY = v.Position.Y;
                if (v.Position.Y > maxY) maxY = v.Position.Y;
            }
            float height = maxY - minY;
            float sliceTop = minY + MathF.Min(TrunkSliceMaxMeters, TrunkSliceFraction * height);

            // Euclidean XZ radius (distance from the cylinder axis) of every sliced vertex.
            var radii = new List<float>();
            foreach (ModelVertex v in mesh.Vertices)
            {
                if (v.Position.Y > sliceTop) continue;
                radii.Add(MathF.Sqrt(v.Position.X * v.Position.X + v.Position.Z * v.Position.Z));
            }

            float radius = TrunkRadiusFloor;
            if (radii.Count > 0)
            {
                radii.Sort();
                int idx = (int)(TrunkRadiusPercentile * (radii.Count - 1));
                radius = MathF.Max(TrunkRadiusFloor, radii[idx]);
            }

            float length = height;   // full prop height
            return new CylinderShape(radius, length);
        }

        /// <summary>Bake a TRUE convex hull from a short solid prop (a rock). Deduplicate the vertices (a 5 mm
        /// spatial bucket) and sort them deterministically (streaming consistency: server and client must bake the
        /// identical shape from the same mesh), then hand the FULL deduplicated set to <see cref="ConvexHullShape"/>.
        /// Bepu's <c>ConvexHullHelper</c> computes the minimal outer hull from the points, discarding the interior
        /// ones, so passing everything yields the true smooth hull (typically ~20-40 faces for a rock). The old bake
        /// stride-downsampled to 64 points, which threw away the very extreme points that define the hull and left
        /// it lumpy; this passes them all. Only when the count exceeds <see cref="MaxHullPoints"/> (a guard for
        /// pathologically high-poly meshes - these rocks never trip it) does it cap, and then by keeping the
        /// spatially-EXTREME points (largest distance from the centroid), never by striding.</summary>
        static ConvexHullShape BakeConvexHull(GltfMesh mesh)
        {
            // Deduplicate vertices by spatial bucketing (deterministic, grid-based) at 5 mm resolution.
            var unique = new Dictionary<(int, int, int), Vector3>();
            foreach (ModelVertex v in mesh.Vertices)
            {
                int bx = (int)MathF.Round(v.Position.X * 200f);
                int by = (int)MathF.Round(v.Position.Y * 200f);
                int bz = (int)MathF.Round(v.Position.Z * 200f);
                unique.TryAdd((bx, by, bz), v.Position);
            }

            // Sort by bucket key (ascending, lexicographic) before copying so the ordering is a contract, not a
            // Dictionary implementation detail. Required for streaming consistency: server and client must bake the
            // identical shape from the same mesh.
            var sortedKeys = new List<(int, int, int)>(unique.Keys);
            sortedKeys.Sort((a, b) =>
            {
                int c = a.Item1.CompareTo(b.Item1); if (c != 0) return c;
                    c = a.Item2.CompareTo(b.Item2); if (c != 0) return c;
                return a.Item3.CompareTo(b.Item3);
            });

            Vector3[] points = new Vector3[sortedKeys.Count];
            for (int i = 0; i < sortedKeys.Count; i++) points[i] = unique[sortedKeys[i]];

            // Pass the full deduplicated set to the hull (Bepu discards interior points). Only cap pathologically
            // high-poly meshes, and then keep the spatially-extreme points so the hull-defining extremes survive.
            if (points.Length > MaxHullPoints)
                points = KeepExtremePoints(points, MaxHullPoints);

            return new ConvexHullShape(points);
        }

        /// <summary>Cap the input to a convex-hull bake at <paramref name="target"/> points WITHOUT discarding the
        /// hull-defining extremes: keep the points with the largest euclidean distance from the centroid (the
        /// outermost points, which the hull is made of), dropping the interior cluster. Deterministic: a stable
        /// distance sort with a positional tie-break, so the same input always yields the same cap. This only runs
        /// for pathologically high-poly meshes; low-poly rocks pass through untouched.</summary>
        static Vector3[] KeepExtremePoints(Vector3[] points, int target)
        {
            Vector3 centroid = Vector3.Zero;
            foreach (Vector3 p in points) centroid += p;
            centroid /= points.Length;

            // Sort by descending distance from the centroid; positional tie-break keeps it deterministic.
            var sorted = new List<Vector3>(points);
            sorted.Sort((a, b) =>
            {
                float da = (a - centroid).LengthSquared();
                float db = (b - centroid).LengthSquared();
                int c = db.CompareTo(da); if (c != 0) return c;
                    c = a.X.CompareTo(b.X); if (c != 0) return c;
                    c = a.Y.CompareTo(b.Y); if (c != 0) return c;
                return a.Z.CompareTo(b.Z);
            });

            var result = new Vector3[target];
            for (int i = 0; i < target; i++) result[i] = sorted[i];
            return result;
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

            // Preserve the source triangle winding in order: Bepu meshes are one-sided, and the outward-wound
            // faces of the normalized prop mesh are what generate the contacts. Do NOT reverse or reorder.
            uint[] src = mesh.Indices32;
            var indices = new int[src.Length];
            for (int i = 0; i < src.Length; i++)
                indices[i] = remapIdx[(int)src[i]];

            return new TriangleMeshShape(positions.ToArray(), indices);
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
