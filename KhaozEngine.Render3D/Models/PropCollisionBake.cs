using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Render3D
{
    /// <summary>Bakes a 3D collision shape from a <see cref="PropLoader"/>-normalized prop mesh (base y=0, XZ
    /// centred on origin). Trees get a trunk-only <see cref="ConvexHullShape"/> tracking the lean via
    /// <see cref="BakeTrunkHull"/> (percentile-filtered lower-trunk vertices following the leaning centreline;
    /// <see cref="BakeTrunkCylinder"/> is the degenerate fallback); buildings (tall, non-convex: walls, doorways,
    /// interiors) get a <see cref="TriangleMeshShape"/>; every other short solid prop (rocks, logs) gets a
    /// <see cref="ConvexHullShape"/>. A convex hull can never trap the capsule (there is always a unique shortest
    /// exit), unlike a one-sided non-convex triangle mesh whose conflicting per-triangle contacts can suck the
    /// capsule through the near face and pin it inside. The hull is the TRUE minimal hull of the full deduplicated
    /// vertex set (Bepu's hull helper discards interior points), not a stride-downsampled under-approximation, so
    /// it is a smooth convex polytope rather than a lumpy one. The Bepu backend then builds its internal
    /// representation from the shape seam. <see cref="PropBakePlan.For"/> single-sources the per-prop bake
    /// decision. Offline/tooling only; the runtime reads the baked binary via
    /// <see cref="PropCollisionLoader"/>.</summary>
    public static class PropCollisionBake
    {
        /// <summary>Binary magic: "KECL" (KhaozEngine Collision). Single-sourced from
        /// <see cref="PropCollisionFormat.Magic"/> (the render-free format now lives in KhaozEngine.Physics).</summary>
        public const uint Magic = PropCollisionFormat.Magic;
        /// <summary>Format version written by this implementation. Single-sourced from
        /// <see cref="PropCollisionFormat.Version"/>.</summary>
        public const byte Version = PropCollisionFormat.Version;

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

        /// <summary>Hard cap (metres) on the trunk-hull band height. The hull uses the player-reachable lower trunk;
        /// the canopy is excluded above min(this, <see cref="FoliageBaseFraction"/> * height).</summary>
        public const float TrunkHullMaxMeters = 3.0f;

        /// <summary>Fraction of the tree height treated as the trunk band (foliage starts above it). The trunk-hull
        /// cap is min(<see cref="TrunkHullMaxMeters"/>, this * height).</summary>
        public const float FoliageBaseFraction = 0.5f;

        /// <summary>Keep trunk-band verts within this multiple of the percentile core radius of the running centreline;
        /// drops spreading low branches while keeping the trunk core.</summary>
        public const float TrunkCoreRadiusFactor = 1.6f;

        /// <summary>Height (metres) of each centreline bin used to track a leaning trunk's drift with height.</summary>
        public const float TrunkCentrelineBinMeters = 0.25f;

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
        /// tree -> trunk-only <see cref="CylinderShape"/> via <see cref="BakeTrunkCylinder"/>; building ->
        /// <see cref="TriangleMeshShape"/> (concave interior); rock/short-solid -> <see cref="ConvexHullShape"/>
        /// (a convex shape can never trap the capsule, unlike a one-sided non-convex mesh).
        /// <para>Trees bake a thin trunk CYLINDER (radius = a low percentile of the bottom-slice XZ radius, i.e. the
        /// pure trunk core), NOT a convex hull of the lower trunk. A hull tracks the lean but is corrupted by real
        /// conifer geometry: the lowest branch ring sits inside the player-reachable band, its off-axis points pull
        /// the per-bin centreline off the trunk, and the convex hull then balloons into an invisible chest-height
        /// wall (the tester report "running into branches" - the real pines hulled to a ~2 m radius at chest height,
        /// while a vertical trunk cylinder is ~0.3-0.5 m and cannot balloon by construction). The lean is small
        /// (~0.1 m over the lower trunk) so a base-aligned cylinder still covers the visible trunk; the canopy and
        /// branches generate no collision, so the player walks under them. <see cref="BakeTrunkHull"/> is retained
        /// (it is the right shape for a clean trunk and is unit-tested) but is no longer the default for trees.</para>
        /// <see cref="PropBakePlan.For"/> single-sources this decision.</summary>
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

        /// <summary>Bake a convex hull of the player-reachable lower trunk that FOLLOWS a leaning trunk (real
        /// Quaternius trees lean 0.3-0.9 m over their height, so a base-pinned vertical cylinder is not where the
        /// trunk is by mid-height). Keep verts below the trunk-band cap (min(<see cref="TrunkHullMaxMeters"/>,
        /// <see cref="FoliageBaseFraction"/> * height)), build a per-height-bin centreline so the kept set tracks the
        /// lean, and drop verts beyond <see cref="TrunkCoreRadiusFactor"/> * the percentile core radius of that
        /// centreline (rejects spreading low branches). Degenerate trunk (&lt; 4 surviving verts or coplanar) falls
        /// back to <see cref="BakeTrunkCylinder"/>. A trunk is roughly convex, so the hull loses no useful detail and
        /// can never trap the capsule.</summary>
        public static PhysicsShape BakeTrunkHull(GltfMesh mesh)
        {
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (ModelVertex v in mesh.Vertices)
            {
                if (v.Position.Y < minY) minY = v.Position.Y;
                if (v.Position.Y > maxY) maxY = v.Position.Y;
            }
            float height = maxY - minY;
            float cap = minY + MathF.Min(TrunkHullMaxMeters, FoliageBaseFraction * height);

            // Trunk-band verts (drop the canopy).
            var band = new List<Vector3>();
            foreach (ModelVertex v in mesh.Vertices)
                if (v.Position.Y <= cap) band.Add(v.Position);
            if (band.Count < 4) return BakeTrunkCylinder(mesh);

            // Running centreline: bin by height, centroid XZ per bin (tracks the lean). Vertical bin index off minY.
            var binSum = new Dictionary<int, (Vector3 sum, int n)>();
            foreach (Vector3 p in band)
            {
                int bin = (int)MathF.Floor((p.Y - minY) / TrunkCentrelineBinMeters);
                if (binSum.TryGetValue(bin, out var acc)) binSum[bin] = (acc.sum + p, acc.n + 1);
                else binSum[bin] = (p, 1);
            }
            Vector3 Centreline(Vector3 p)
            {
                int bin = (int)MathF.Floor((p.Y - minY) / TrunkCentrelineBinMeters);
                var acc = binSum[bin];
                Vector3 c = acc.sum / acc.n;
                return new Vector3(c.X, p.Y, c.Z);   // XZ centroid at this height
            }

            // Percentile core radius (XZ distance from each vert to its bin centreline).
            var radii = new List<float>(band.Count);
            foreach (Vector3 p in band)
            {
                Vector3 c = Centreline(p);
                radii.Add(MathF.Sqrt((p.X - c.X) * (p.X - c.X) + (p.Z - c.Z) * (p.Z - c.Z)));
            }
            radii.Sort();
            float coreRadius = MathF.Max(TrunkRadiusFloor, radii[(int)(TrunkRadiusPercentile * (radii.Count - 1))]);
            float keepRadius = TrunkCoreRadiusFactor * coreRadius;

            var kept = new List<Vector3>(band.Count);
            foreach (Vector3 p in band)
            {
                Vector3 c = Centreline(p);
                float dx = p.X - c.X, dz = p.Z - c.Z;
                if (dx * dx + dz * dz <= keepRadius * keepRadius) kept.Add(p);
            }
            if (kept.Count < 4 || IsCoplanar(kept)) return BakeTrunkCylinder(mesh);
            return HullFromPoints(kept);
        }

        /// <summary>True when all points lie (within a tolerance) on a single plane, so a convex hull would be
        /// degenerate. Picks the plane from the first non-collinear triple and checks every point's distance to it.</summary>
        static bool IsCoplanar(IReadOnlyList<Vector3> pts)
        {
            const float Eps = 1e-4f;
            Vector3 p0 = pts[0];
            // Find an edge a = p_i - p0 with length > eps.
            Vector3 a = Vector3.Zero;
            foreach (Vector3 p in pts) { Vector3 d = p - p0; if (d.LengthSquared() > Eps) { a = d; break; } }
            if (a.LengthSquared() <= Eps) return true; // all coincident
            // Find a normal n = a x (p_j - p0) that is non-degenerate (a non-collinear point).
            Vector3 n = Vector3.Zero;
            foreach (Vector3 p in pts) { Vector3 c = Vector3.Cross(a, p - p0); if (c.LengthSquared() > Eps) { n = Vector3.Normalize(c); break; } }
            if (n.LengthSquared() <= Eps) return true; // all collinear
            foreach (Vector3 p in pts) if (MathF.Abs(Vector3.Dot(p - p0, n)) > 1e-3f) return false;
            return true;
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
            var positions = new List<Vector3>(mesh.Vertices.Length);
            foreach (ModelVertex v in mesh.Vertices) positions.Add(v.Position);
            return HullFromPoints(positions);
        }

        /// <summary>Build a TRUE convex hull from arbitrary local-space points: deduplicate (5 mm spatial bucket),
        /// sort deterministically (streaming consistency: server and client must bake the identical shape), then hand
        /// the FULL deduplicated set to <see cref="ConvexHullShape"/> (Bepu's hull helper discards interior points).
        /// Only when the count exceeds <see cref="MaxHullPoints"/> (a guard for pathologically high-poly meshes) does it
        /// cap, and then by keeping the spatially-EXTREME points (never striding). Shared by <see cref="BakeConvexHull"/>
        /// (whole mesh) and <see cref="BakeTrunkHull"/> (filtered trunk verts).</summary>
        static ConvexHullShape HullFromPoints(IReadOnlyList<Vector3> pts)
        {
            var unique = new Dictionary<(int, int, int), Vector3>();
            foreach (Vector3 p in pts)
            {
                int bx = (int)MathF.Round(p.X * 200f);
                int by = (int)MathF.Round(p.Y * 200f);
                int bz = (int)MathF.Round(p.Z * 200f);
                unique.TryAdd((bx, by, bz), p);
            }

            var sortedKeys = new List<(int, int, int)>(unique.Keys);
            sortedKeys.Sort((a, b) =>
            {
                int c = a.Item1.CompareTo(b.Item1); if (c != 0) return c;
                    c = a.Item2.CompareTo(b.Item2); if (c != 0) return c;
                return a.Item3.CompareTo(b.Item3);
            });

            Vector3[] points = new Vector3[sortedKeys.Count];
            for (int i = 0; i < sortedKeys.Count; i++) points[i] = unique[sortedKeys[i]];

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

        /// <summary>Serialize <paramref name="shape"/> to <paramref name="stream"/> in the KECL binary format.
        /// Magic (uint32 LE) + version (byte) + kind (byte) + payload. Delegates to the render-free
        /// <see cref="PropCollisionFormat.Write"/> so the bake tool and the headless server share one
        /// byte-identical encoder.</summary>
        public static void Write(PhysicsShape shape, Stream stream)
            => PropCollisionFormat.Write(shape, stream);
    }
}
