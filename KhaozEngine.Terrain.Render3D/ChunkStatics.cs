using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Terrain;

namespace KhaozEngine.Terrain
{
    /// <summary>Render-free helper: adds/removes per-prop <see cref="StaticHandle"/>s in an
    /// <see cref="IPhysicsWorld"/> as a chunk is loaded or unloaded by
    /// <see cref="KhaozEngine.Terrain.Scene3DChunkSink"/>. Extracted so the logic is headless-testable
    /// without a GPU context. Scale is applied to the shape geometry (uniform-scale pre-bake); see
    /// <see cref="ScaleShape"/> for per-type details.</summary>
    internal static class ChunkStatics
    {
        /// <summary>For each placement in <paramref name="placements"/> that has an entry in
        /// <paramref name="collisionShapes"/>, add a static body to <paramref name="physics"/> at the
        /// world pose and append the handle to <paramref name="handles"/>. The Y position of each static
        /// is the placement's baked <c>Y</c> field (terrain height at scatter time).</summary>
        internal static void AddAll(
            IPhysicsWorld physics,
            IReadOnlyDictionary<string, PhysicsShape> collisionShapes,
            IReadOnlyList<PropPlacement> placements,
            List<StaticHandle> handles)
        {
            if (physics is null) throw new ArgumentNullException(nameof(physics));
            if (collisionShapes is null) throw new ArgumentNullException(nameof(collisionShapes));
            if (placements is null) throw new ArgumentNullException(nameof(placements));
            if (handles is null) throw new ArgumentNullException(nameof(handles));

            for (int i = 0; i < placements.Count; i++)
            {
                PropPlacement p = placements[i];
                if (!collisionShapes.TryGetValue(p.Id, out PhysicsShape? shape)) continue;

                PhysicsShape scaled = ScaleShape(shape, p.Scale);
                var pose = new Pose(
                    new Vector3(p.X, p.Y, p.Z),
                    Quaternion.CreateFromAxisAngle(Vector3.UnitY, p.Yaw));

                handles.Add(physics.AddStatic(scaled, pose));
            }
        }

        /// <summary>Remove every handle in <paramref name="handles"/> from <paramref name="physics"/> and
        /// clear the list.</summary>
        internal static void RemoveAll(IPhysicsWorld physics, List<StaticHandle> handles)
        {
            if (physics is null) throw new ArgumentNullException(nameof(physics));
            if (handles is null) throw new ArgumentNullException(nameof(handles));

            for (int i = 0; i < handles.Count; i++)
                physics.RemoveStatic(handles[i]);
            handles.Clear();
        }

        /// <summary>Return a new shape with all geometric dimensions scaled by <paramref name="scale"/>.
        /// For convex hulls and triangle meshes, vertex positions are pre-multiplied. For primitives
        /// (sphere, capsule, cylinder, box) all length fields are scaled uniformly. Compound children
        /// are recursed. A scale of 1 returns the original instance unchanged.</summary>
        /// <remarks>Limitation: non-uniform scale is not supported by this seam (the scatter always emits
        /// a single uniform <c>Scale</c> float, so this is correct for all current prop placements).
        /// A future per-axis scale would need the backend's per-axis support.</remarks>
        internal static PhysicsShape ScaleShape(PhysicsShape shape, float scale)
        {
            if (MathF.Abs(scale - 1f) < 1e-6f) return shape;

            return shape switch
            {
                SphereShape s => new SphereShape(s.Radius * scale),
                CapsuleShape c => new CapsuleShape(c.Radius * scale, c.Length * scale),
                CylinderShape cy => new CylinderShape(cy.Radius * scale, cy.Length * scale),
                BoxShape b => new BoxShape(b.HalfExtents * scale),
                ConvexHullShape h => ScaleConvexHull(h, scale),
                TriangleMeshShape m => ScaleTriangleMesh(m, scale),
                CompoundShape co => ScaleCompound(co, scale),
                _ => throw new NotSupportedException($"ChunkStatics.ScaleShape: unsupported shape type {shape.GetType().Name}."),
            };
        }

        static ConvexHullShape ScaleConvexHull(ConvexHullShape h, float scale)
        {
            Vector3[] src = h.Points;
            var dst = new Vector3[src.Length];
            for (int i = 0; i < src.Length; i++) dst[i] = src[i] * scale;
            return new ConvexHullShape(dst);
        }

        static TriangleMeshShape ScaleTriangleMesh(TriangleMeshShape m, float scale)
        {
            Vector3[] src = m.Vertices;
            var dst = new Vector3[src.Length];
            for (int i = 0; i < src.Length; i++) dst[i] = src[i] * scale;
            return new TriangleMeshShape(dst, m.Indices);
        }

        static CompoundShape ScaleCompound(CompoundShape co, float scale)
        {
            CompoundChild[] src = co.Children;
            var dst = new CompoundChild[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                CompoundChild child = src[i];
                dst[i] = new CompoundChild(
                    ScaleShape(child.Shape, scale),
                    new Pose(child.Local.Position * scale, child.Local.Orientation));
            }
            return new CompoundShape(dst);
        }
    }
}
