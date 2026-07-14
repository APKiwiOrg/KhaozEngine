using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain
{
    /// <summary>Instanced render helper for scattered props. Given <see cref="PropPlacement"/>s, a map of kit id to
    /// uploaded <see cref="MeshHandle"/>, a focus point and a draw radius, it queues one instance per in-range
    /// placement (scale + yaw + translation) and distance-culls the rest. The cull is horizontal (XZ), so a draw
    /// radius is a cylinder around the player - props are not culled by terrain height. Pure use of the existing
    /// <see cref="SceneInstances"/> path, so a forest of N props batches into a handful of draws (one per kit
    /// mesh). The <see cref="Queue(SceneInstances, IReadOnlyList{PropPlacement}, IReadOnlyDictionary{string, MeshHandle}, Vector3, float, Color?)"/> overload is headless-testable. <see cref="DrawProps(Scene3D, IReadOnlyList{PropPlacement}, IReadOnlyDictionary{string, MeshHandle}, Vector3, float, Color?)"/> is the Scene3D
    /// convenience a consumer with <c>using KhaozEngine.Terrain;</c> gets in scope.</summary>
    public static class PropRenderer
    {
        /// <summary>Queue every placement within <paramref name="drawRadius"/> (horizontal/XZ) of
        /// <paramref name="focus"/> whose <see cref="PropPlacement.Id"/> has a mesh in <paramref name="meshes"/>.
        /// Out-of-range and unknown-id placements are skipped. Returns the number queued.</summary>
        public static int Queue(SceneInstances instances, IReadOnlyList<PropPlacement> placements,
                                IReadOnlyDictionary<string, MeshHandle> meshes, Vector3 focus, float drawRadius,
                                Color? tint = null)
        {
            if (instances == null) throw new ArgumentNullException(nameof(instances));
            Color t = tint ?? Color.White;
            return Emit(placements, meshes, focus, drawRadius, (handle, world) => instances.Add(handle, world, t));
        }

        /// <summary>Scene3D convenience: queue the in-range props into the scene's instance buffer for this frame
        /// (same cull + matrix as <see cref="Queue(SceneInstances, IReadOnlyList{PropPlacement}, IReadOnlyDictionary{string, MeshHandle}, Vector3, float, Color?)"/>). Returns the number drawn.</summary>
        public static int DrawProps(this Scene3D scene, IReadOnlyList<PropPlacement> placements,
                                    IReadOnlyDictionary<string, MeshHandle> meshes, Vector3 focus, float drawRadius,
                                    Color? tint = null)
        {
            if (scene == null) throw new ArgumentNullException(nameof(scene));
            Color t = tint ?? Color.White;
            return Emit(placements, meshes, focus, drawRadius, (handle, world) => scene.Draw(handle, world, t));
        }

        /// <summary>Multi-part variant of <see cref="Queue(SceneInstances, IReadOnlyList{PropPlacement}, IReadOnlyDictionary{string, MeshHandle}, Vector3, float, Color?)"/>: each kit id maps to ONE-OR-MANY
        /// <see cref="MeshHandle"/>s (a prop split into one textured sub-mesh per source material, from
        /// <see cref="Scene3D.LoadPropMeshes"/>). For every in-range placement, all of that id's parts are queued at
        /// the placement's shared scale/yaw/translation transform, so the whole prop instances as a unit and each
        /// (id, part) batches through the same <see cref="SceneInstances"/> path as the single-mesh form (no new
        /// per-instance shader indexing). A single-part list queues exactly one instance per placement, byte-identical
        /// to <see cref="Queue(SceneInstances, IReadOnlyList{PropPlacement}, IReadOnlyDictionary{string, MeshHandle}, Vector3, float, Color?)"/>. Returns the number of PLACEMENTS drawn (not part submissions).</summary>
        public static int Queue(SceneInstances instances, IReadOnlyList<PropPlacement> placements,
                                IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> parts, Vector3 focus,
                                float drawRadius, Color? tint = null)
        {
            if (instances == null) throw new ArgumentNullException(nameof(instances));
            Color t = tint ?? Color.White;
            return EmitParts(placements, parts, focus, drawRadius, (handle, world) => instances.Add(handle, world, t));
        }

        /// <summary>Scene3D convenience: multi-part variant of <see cref="DrawProps(Scene3D,IReadOnlyList{PropPlacement},IReadOnlyDictionary{string,MeshHandle},Vector3,float,Color?)"/>.
        /// Queues every part of each in-range prop at the placement transform (same cull + matrix + batching as the
        /// headless <see cref="Queue(SceneInstances,IReadOnlyList{PropPlacement},IReadOnlyDictionary{string,IReadOnlyList{MeshHandle}},Vector3,float,Color?)"/>).
        /// Returns the number of placements drawn.</summary>
        public static int DrawProps(this Scene3D scene, IReadOnlyList<PropPlacement> placements,
                                    IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> parts, Vector3 focus,
                                    float drawRadius, Color? tint = null)
        {
            if (scene == null) throw new ArgumentNullException(nameof(scene));
            Color t = tint ?? Color.White;
            return EmitParts(placements, parts, focus, drawRadius, (handle, world) => scene.Draw(handle, world, t));
        }

        static int Emit(IReadOnlyList<PropPlacement> placements, IReadOnlyDictionary<string, MeshHandle> meshes,
                        Vector3 focus, float drawRadius, Action<MeshHandle, Matrix4x4> sink)
        {
            if (placements == null) throw new ArgumentNullException(nameof(placements));
            if (meshes == null) throw new ArgumentNullException(nameof(meshes));

            float r2 = drawRadius * drawRadius;
            int count = 0;
            for (int i = 0; i < placements.Count; i++)
            {
                PropPlacement p = placements[i];
                float dx = p.X - focus.X, dz = p.Z - focus.Z;
                if (dx * dx + dz * dz > r2) continue;                       // horizontal distance cull
                if (!meshes.TryGetValue(p.Id, out MeshHandle handle)) continue;

                Matrix4x4 world = Matrix4x4.CreateScale(p.Scale)
                                  * Matrix4x4.CreateRotationY(p.Yaw)
                                  * Matrix4x4.CreateTranslation(p.X, p.Y, p.Z);
                sink(handle, world);
                count++;
            }
            return count;
        }

        // As Emit, but each in-range placement queues EVERY part of its kit id at the shared world transform. The
        // cull + matrix are identical to the single-mesh path. Only the inner loop over the id's parts differs, so a
        // single-part list produces byte-identical submissions. Returns the number of placements drawn.
        static int EmitParts(IReadOnlyList<PropPlacement> placements,
                             IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> parts,
                             Vector3 focus, float drawRadius, Action<MeshHandle, Matrix4x4> sink)
        {
            if (placements == null) throw new ArgumentNullException(nameof(placements));
            if (parts == null) throw new ArgumentNullException(nameof(parts));

            float r2 = drawRadius * drawRadius;
            int count = 0;
            for (int i = 0; i < placements.Count; i++)
            {
                PropPlacement p = placements[i];
                float dx = p.X - focus.X, dz = p.Z - focus.Z;
                if (dx * dx + dz * dz > r2) continue;                       // horizontal distance cull
                if (!parts.TryGetValue(p.Id, out IReadOnlyList<MeshHandle>? handles) || handles == null) continue;

                Matrix4x4 world = Matrix4x4.CreateScale(p.Scale)
                                  * Matrix4x4.CreateRotationY(p.Yaw)
                                  * Matrix4x4.CreateTranslation(p.X, p.Y, p.Z);
                for (int j = 0; j < handles.Count; j++) sink(handles[j], world);
                count++;
            }
            return count;
        }
    }
}
