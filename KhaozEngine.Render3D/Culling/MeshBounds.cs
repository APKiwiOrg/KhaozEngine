using System;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Mesh-local bounds (an axis-aligned box plus a bounding sphere) computed once when a mesh is uploaded, so the
    /// per-frame frustum cull never rescans vertices. The sphere (<see cref="Center"/> + <see cref="Radius"/>) is
    /// the box's mid-point and half-diagonal, a conservative superset of the geometry. The renderer transforms these
    /// local bounds by the per-instance world matrix at cull time (sphere: transform the centre, scale the radius by
    /// the matrix's largest axis scale).
    /// </summary>
    public readonly struct MeshBounds
    {
        /// <summary>Local-space AABB minimum corner.</summary>
        public Vector3 Min { get; }
        /// <summary>Local-space AABB maximum corner.</summary>
        public Vector3 Max { get; }
        /// <summary>Local-space bounding-sphere centre (the AABB mid-point).</summary>
        public Vector3 Center { get; }
        /// <summary>Local-space bounding-sphere radius (half the AABB diagonal).</summary>
        public float Radius { get; }

        public MeshBounds(Vector3 min, Vector3 max)
        {
            Min = min; Max = max;
            Center = (min + max) * 0.5f;
            Radius = (max - min).Length() * 0.5f;
        }

        /// <summary>An empty/degenerate bounds (a point at the origin). Used for a mesh with no vertices; a
        /// zero-radius sphere is conservatively kept by the frustum test unless it is provably outside.</summary>
        public static MeshBounds Empty => new(Vector3.Zero, Vector3.Zero);

        /// <summary>Compute local bounds from a mesh's vertex positions. One pass, no allocation. An empty span
        /// yields <see cref="Empty"/>.</summary>
        public static MeshBounds FromVertices(ReadOnlySpan<ModelVertex> verts)
        {
            if (verts.Length == 0) return Empty;
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (ref readonly var v in verts)
            {
                min = Vector3.Min(min, v.Position);
                max = Vector3.Max(max, v.Position);
            }
            return new MeshBounds(min, max);
        }

        /// <summary>The world-space bounding sphere for this mesh drawn at <paramref name="world"/>: the local
        /// centre transformed by the matrix, and the local radius scaled by the matrix's largest axis scale (a
        /// conservative bound under non-uniform scale). Rotation/translation do not change a sphere's radius.</summary>
        public void WorldSphere(in Matrix4x4 world, out Vector3 center, out float radius)
        {
            center = Vector3.Transform(Center, world);
            // Largest of the three basis-row lengths = the max axis scale (also correct under rotation, since a
            // pure rotation row has unit length). Guards non-uniform scale by taking the largest.
            float sx = new Vector3(world.M11, world.M12, world.M13).Length();
            float sy = new Vector3(world.M21, world.M22, world.M23).Length();
            float sz = new Vector3(world.M31, world.M32, world.M33).Length();
            radius = Radius * MathF.Max(sx, MathF.Max(sy, sz));
        }
    }
}
