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

        /// <summary>Return a new shape with all geometric dimensions scaled uniformly by
        /// <paramref name="scale"/> (delegates to the public <see cref="PhysicsShapeScale.Uniform"/> helper in
        /// KhaozEngine.Physics, the single home for per-placement shape scaling). A scale of 1 returns the
        /// original instance unchanged.</summary>
        /// <remarks>Limitation: non-uniform (per-axis) scale is not modelled (the scatter emits a single uniform
        /// <c>Scale</c> float per placement).</remarks>
        internal static PhysicsShape ScaleShape(PhysicsShape shape, float scale)
            => PhysicsShapeScale.Uniform(shape, scale);
    }
}
