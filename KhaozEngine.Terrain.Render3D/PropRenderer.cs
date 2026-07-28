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
    /// mesh).
    /// <para>Two optional presentation knobs layer on top of the plain cull, both defaulting to today's exact
    /// behaviour. <c>fadeBandWidth</c> (issue #44) turns the hard cut at <c>drawRadius</c> into a dissolve FADE BAND:
    /// over the ring <c>drawRadius - fadeBandWidth</c> .. <c>drawRadius</c> each prop's rigid dissolve (the 14.5.0
    /// per-instance primitive, opaque noise discard so overlapping fades never sort-fight) ramps deterministically 0
    /// (solid) to 1 (fully discarded) by horizontal distance, so props thin out instead of popping. A band of 0 keeps
    /// the byte-identical hard cut. <c>lodMeshes</c>/<c>lodDistance</c> swap a kit to an author-supplied far LOD mesh
    /// (from <see cref="AssetEntry.LodFile"/>) beyond <c>lodDistance</c>: a per-kit opt-in, an id with no variant just
    /// keeps its full mesh. Both are deterministic per distance, no per-frame randomness. A third knob,
    /// <c>castsShadows</c> (issue #287), is policy rather than presentation: false keeps the emitted props out of the
    /// shadow depth pass entirely (they still draw and still receive shadows), which is what a dense short-radius
    /// layer wants when its hundreds of small cast shadows cost more than they read.</para>
    /// The headless <see cref="Queue(SceneInstances, IReadOnlyList{PropPlacement}, IReadOnlyDictionary{string, MeshHandle}, Vector3, float, Color?, float, IReadOnlyDictionary{string, MeshHandle}, float, float, bool)"/> overload is testable without a GPU; <see cref="DrawProps(Scene3D, IReadOnlyList{PropPlacement}, IReadOnlyDictionary{string, MeshHandle}, Vector3, float, Color?, float, IReadOnlyDictionary{string, MeshHandle}, float, float, bool)"/> is the Scene3D
    /// convenience a consumer with <c>using KhaozEngine.Terrain;</c> gets in scope.</summary>
    public static class PropRenderer
    {
        /// <summary>Queue every placement within <paramref name="drawRadius"/> (horizontal/XZ) of
        /// <paramref name="focus"/> whose <see cref="PropPlacement.Id"/> has a mesh in <paramref name="meshes"/>.
        /// Out-of-range and unknown-id placements are skipped. <paramref name="fadeBandWidth"/> (default 0 = hard cut)
        /// dissolves props across the band just inside the radius; <paramref name="lodMeshes"/> plus a positive
        /// <paramref name="lodDistance"/> swap a kit to its far LOD variant beyond that distance.
        /// <paramref name="dissolveFloor"/> (default 0) raises the MINIMUM dissolve applied to every emitted prop
        /// (combined with the per-placement fade via max), the seam the HLOD crossfade uses to dissolve a whole
        /// chunk's props out uniformly. <paramref name="castsShadows"/> (default true, unchanged) queues these props
        /// as non-casters when false, so a dense decorative layer draws and receives shadows without writing
        /// hundreds of small ones into the cascade atlas (issue #287). Returns the number queued.</summary>
        public static int Queue(SceneInstances instances, IReadOnlyList<PropPlacement> placements,
                                IReadOnlyDictionary<string, MeshHandle> meshes, Vector3 focus, float drawRadius,
                                Color? tint = null, float fadeBandWidth = 0f,
                                IReadOnlyDictionary<string, MeshHandle>? lodMeshes = null, float lodDistance = 0f,
                                float dissolveFloor = 0f, bool castsShadows = true)
        {
            if (instances == null) throw new ArgumentNullException(nameof(instances));
            Color t = tint ?? Color.White;
            return Emit(placements, meshes, lodMeshes, lodDistance, focus, drawRadius, fadeBandWidth, dissolveFloor,
                (handle, world, dissolve) =>
                {
                    if (dissolve > 0f || !castsShadows) instances.Add(handle, world, t, Material.None, dissolve, 0f, default, castsShadows);
                    else instances.Add(handle, world, t);
                });
        }

        /// <summary>Scene3D convenience: queue the in-range props into the scene's instance buffer for this frame
        /// (same cull + matrix + fade band + LOD selection as <see cref="Queue(SceneInstances, IReadOnlyList{PropPlacement}, IReadOnlyDictionary{string, MeshHandle}, Vector3, float, Color?, float, IReadOnlyDictionary{string, MeshHandle}, float, float, bool)"/>). <paramref name="dissolveFloor"/>
        /// (default 0) raises every prop's minimum dissolve, the HLOD crossfade seam. <paramref name="castsShadows"/>
        /// (default true) draws these props as non-casters when false (issue #287). Returns the number drawn.</summary>
        public static int DrawProps(this Scene3D scene, IReadOnlyList<PropPlacement> placements,
                                    IReadOnlyDictionary<string, MeshHandle> meshes, Vector3 focus, float drawRadius,
                                    Color? tint = null, float fadeBandWidth = 0f,
                                    IReadOnlyDictionary<string, MeshHandle>? lodMeshes = null, float lodDistance = 0f,
                                    float dissolveFloor = 0f, bool castsShadows = true)
        {
            if (scene == null) throw new ArgumentNullException(nameof(scene));
            Color t = tint ?? Color.White;
            return Emit(placements, meshes, lodMeshes, lodDistance, focus, drawRadius, fadeBandWidth, dissolveFloor,
                (handle, world, dissolve) =>
                {
                    if (dissolve > 0f || !castsShadows) scene.Draw(handle, world, t, Material.None, dissolve, 0f, default, castsShadows);
                    else scene.Draw(handle, world, t);
                });
        }

        /// <summary>Multi-part variant of <see cref="Queue(SceneInstances, IReadOnlyList{PropPlacement}, IReadOnlyDictionary{string, MeshHandle}, Vector3, float, Color?, float, IReadOnlyDictionary{string, MeshHandle}, float, float, bool)"/>: each kit id maps to ONE-OR-MANY
        /// <see cref="MeshHandle"/>s (a prop split into one textured sub-mesh per source material, from
        /// <see cref="Scene3D.LoadPropMeshes"/>). For every in-range placement, all of that id's parts are queued at
        /// the placement's shared scale/yaw/translation transform, so the whole prop instances as a unit and each
        /// (id, part) batches through the same <see cref="SceneInstances"/> path as the single-mesh form (no new
        /// per-instance shader indexing). The fade band and (<paramref name="lodParts"/>, <paramref name="lodDistance"/>)
        /// LOD swap work exactly as on the single-mesh form, applied to the whole prop (every part shares the one
        /// dissolve value and switches to the LOD variant together). A single-part list queues exactly one instance per
        /// placement, byte-identical to <see cref="Queue(SceneInstances, IReadOnlyList{PropPlacement}, IReadOnlyDictionary{string, MeshHandle}, Vector3, float, Color?, float, IReadOnlyDictionary{string, MeshHandle}, float, float, bool)"/>. Returns the number of PLACEMENTS drawn (not part submissions).</summary>
        public static int Queue(SceneInstances instances, IReadOnlyList<PropPlacement> placements,
                                IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> parts, Vector3 focus,
                                float drawRadius, Color? tint = null, float fadeBandWidth = 0f,
                                IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>>? lodParts = null, float lodDistance = 0f,
                                float dissolveFloor = 0f, bool castsShadows = true)
        {
            if (instances == null) throw new ArgumentNullException(nameof(instances));
            Color t = tint ?? Color.White;
            return EmitParts(placements, parts, lodParts, lodDistance, focus, drawRadius, fadeBandWidth, dissolveFloor,
                (handle, world, dissolve) =>
                {
                    if (dissolve > 0f || !castsShadows) instances.Add(handle, world, t, Material.None, dissolve, 0f, default, castsShadows);
                    else instances.Add(handle, world, t);
                });
        }

        /// <summary>Scene3D convenience: multi-part variant of <see cref="DrawProps(Scene3D,IReadOnlyList{PropPlacement},IReadOnlyDictionary{string,MeshHandle},Vector3,float,Color?,float,IReadOnlyDictionary{string,MeshHandle},float,float,bool)"/>.
        /// Queues every part of each in-range prop at the placement transform (same cull + matrix + fade band + LOD as
        /// the headless <see cref="Queue(SceneInstances,IReadOnlyList{PropPlacement},IReadOnlyDictionary{string,IReadOnlyList{MeshHandle}},Vector3,float,Color?,float,IReadOnlyDictionary{string,IReadOnlyList{MeshHandle}},float,float,bool)"/>).
        /// Returns the number of placements drawn.</summary>
        public static int DrawProps(this Scene3D scene, IReadOnlyList<PropPlacement> placements,
                                    IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> parts, Vector3 focus,
                                    float drawRadius, Color? tint = null, float fadeBandWidth = 0f,
                                    IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>>? lodParts = null, float lodDistance = 0f,
                                    float dissolveFloor = 0f, bool castsShadows = true)
        {
            if (scene == null) throw new ArgumentNullException(nameof(scene));
            Color t = tint ?? Color.White;
            return EmitParts(placements, parts, lodParts, lodDistance, focus, drawRadius, fadeBandWidth, dissolveFloor,
                (handle, world, dissolve) =>
                {
                    if (dissolve > 0f || !castsShadows) scene.Draw(handle, world, t, Material.None, dissolve, 0f, default, castsShadows);
                    else scene.Draw(handle, world, t);
                });
        }

        // Deterministic dissolve for one placement at squared horizontal distance d2 from the focus. Outside the fade
        // band (d2 <= fadeInner^2, or a zero band) the prop is solid (0). Across the band it ramps linearly to 1 at
        // drawRadius, where the noise discard leaves nothing just as the cull removes it, so the handoff never pops.
        // fadeBand is the clamped band width (an over-wide band is capped to the radius, so the fade starts at focus
        // but still reaches exactly 1 at the radius).
        static float DissolveAt(float d2, float fadeInner, float fadeBand)
        {
            if (fadeBand <= 0f) return 0f;
            float dist = MathF.Sqrt(d2);
            return Math.Clamp((dist - fadeInner) / fadeBand, 0f, 1f);
        }

        static int Emit(IReadOnlyList<PropPlacement> placements, IReadOnlyDictionary<string, MeshHandle> meshes,
                        IReadOnlyDictionary<string, MeshHandle>? lodMeshes, float lodDistance,
                        Vector3 focus, float drawRadius, float fadeBandWidth, float dissolveFloor,
                        Action<MeshHandle, Matrix4x4, float> sink)
        {
            if (placements == null) throw new ArgumentNullException(nameof(placements));
            if (meshes == null) throw new ArgumentNullException(nameof(meshes));

            float r2 = drawRadius * drawRadius;
            float fadeInner = MathF.Max(0f, drawRadius - fadeBandWidth);   // fade starts here (band clamped to radius)
            float fadeBand = drawRadius - fadeInner;                        // = min(fadeBandWidth, drawRadius); 0 = hard cut
            bool useLod = lodMeshes != null && lodDistance > 0f;
            float lod2 = lodDistance * lodDistance;
            int count = 0;
            for (int i = 0; i < placements.Count; i++)
            {
                PropPlacement p = placements[i];
                float dx = p.X - focus.X, dz = p.Z - focus.Z;
                float d2 = dx * dx + dz * dz;
                if (d2 > r2) continue;                                      // horizontal distance cull
                if (!meshes.TryGetValue(p.Id, out MeshHandle handle)) continue;
                if (useLod && d2 > lod2 && lodMeshes!.TryGetValue(p.Id, out MeshHandle lodHandle))
                    handle = lodHandle;                                     // far LOD variant (per-kit opt-in)

                Matrix4x4 world = Matrix4x4.CreateScale(p.Scale)
                                  * Matrix4x4.CreateRotationY(p.Yaw)
                                  * Matrix4x4.CreateTranslation(p.X, p.Y, p.Z);
                // Per-placement fade band OR the uniform HLOD crossfade floor, whichever discards more.
                sink(handle, world, MathF.Max(DissolveAt(d2, fadeInner, fadeBand), dissolveFloor));
                count++;
            }
            return count;
        }

        // As Emit, but each in-range placement queues EVERY part of its kit id at the shared world transform (the LOD
        // swap picks the variant's whole part list). The cull + matrix + dissolve are identical to the single-mesh
        // path, so a single-part list with no fade/LOD produces byte-identical submissions. Returns placements drawn.
        static int EmitParts(IReadOnlyList<PropPlacement> placements,
                             IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> parts,
                             IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>>? lodParts, float lodDistance,
                             Vector3 focus, float drawRadius, float fadeBandWidth, float dissolveFloor,
                             Action<MeshHandle, Matrix4x4, float> sink)
        {
            if (placements == null) throw new ArgumentNullException(nameof(placements));
            if (parts == null) throw new ArgumentNullException(nameof(parts));

            float r2 = drawRadius * drawRadius;
            float fadeInner = MathF.Max(0f, drawRadius - fadeBandWidth);
            float fadeBand = drawRadius - fadeInner;
            bool useLod = lodParts != null && lodDistance > 0f;
            float lod2 = lodDistance * lodDistance;
            int count = 0;
            for (int i = 0; i < placements.Count; i++)
            {
                PropPlacement p = placements[i];
                float dx = p.X - focus.X, dz = p.Z - focus.Z;
                float d2 = dx * dx + dz * dz;
                if (d2 > r2) continue;                                      // horizontal distance cull
                if (!parts.TryGetValue(p.Id, out IReadOnlyList<MeshHandle>? handles) || handles == null) continue;
                if (useLod && d2 > lod2 && lodParts!.TryGetValue(p.Id, out IReadOnlyList<MeshHandle>? lodHandles) && lodHandles != null)
                    handles = lodHandles;                                   // far LOD variant (per-kit opt-in)

                Matrix4x4 world = Matrix4x4.CreateScale(p.Scale)
                                  * Matrix4x4.CreateRotationY(p.Yaw)
                                  * Matrix4x4.CreateTranslation(p.X, p.Y, p.Z);
                // Whole prop shares one dissolve: per-placement fade band OR the uniform HLOD crossfade floor, max.
                float dissolve = MathF.Max(DissolveAt(d2, fadeInner, fadeBand), dissolveFloor);
                for (int j = 0; j < handles.Count; j++) sink(handles[j], world, dissolve);
                count++;
            }
            return count;
        }
    }
}
