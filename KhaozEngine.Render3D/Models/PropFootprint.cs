using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Collision;

namespace KhaozEngine.Render3D
{
    /// <summary>Tunables for <see cref="PropFootprint"/> collider derivation.</summary>
    public sealed class PropFootprintOptions
    {
        /// <summary>A prop no taller than this (metres) is treated as a small solid object (rock, crate, barrel,
        /// low wall): its collider uses the <b>full</b> XZ footprint. Default 2.5.</summary>
        public float SolidHeightMeters = 2.5f;
        /// <summary>For a prop taller than <see cref="SolidHeightMeters"/> (a tree, a tall building), the footprint
        /// is measured from only the vertices in the bottom this-many metres (the trunk / wall base), so a tree's
        /// wide canopy does not become solid. A building's vertical walls make this equal the full footprint
        /// anyway. Default 1.0.</summary>
        public float TrunkHeightMeters = 1.0f;
        /// <summary>Footprint long-axis / short-axis above this becomes an oriented <see cref="ColliderKind.Box"/>;
        /// at or below it becomes a <see cref="ColliderKind.Cylinder"/>. Default 1.5.</summary>
        public float BoxAspectThreshold = 1.5f;

        /// <summary>The default derivation tuning.</summary>
        public static readonly PropFootprintOptions Default = new();
    }

    /// <summary>
    /// Derives a static-collision <see cref="ColliderShape"/> from a prop mesh so a scattered prop gets a
    /// right-sized collider automatically (the spec's "default a cylinder from the prop footprint"). Expects a
    /// <see cref="PropLoader"/>-normalized mesh (base at y=0, XZ re-centred on the origin = the placement point).
    /// A short prop uses its full XZ footprint; a tall prop uses only the bottom <see
    /// cref="PropFootprintOptions.TrunkHeightMeters"/> slice (so a tree's canopy is excluded, but a building's
    /// vertical walls still measure their full footprint). A near-round footprint becomes a cylinder (radius =
    /// the larger half-extent, so it never under-covers); an oblong one becomes an oriented box (rotated by the
    /// scatter yaw when placed). Render-free of the GPU (uses only the loaded geometry).
    /// </summary>
    public static class PropFootprint
    {
        /// <summary>Derive a collider footprint from one normalized prop mesh.</summary>
        public static ColliderShape Derive(GltfMesh normalizedMesh, PropFootprintOptions? options = null)
        {
            if (normalizedMesh == null) throw new ArgumentNullException(nameof(normalizedMesh));
            PropFootprintOptions o = options ?? PropFootprintOptions.Default;
            ModelVertex[] verts = normalizedMesh.Vertices;
            if (verts.Length == 0) return ColliderShape.Cylinder(0f);

            // Base + height of the (normalized) mesh.
            float minY = float.MaxValue, maxY = float.MinValue;
            for (int i = 0; i < verts.Length; i++)
            {
                float y = verts[i].Position.Y;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
            float height = maxY - minY;

            // Tall props measure only the bottom trunk slice; short props use the full footprint.
            float captureTop = height <= o.SolidHeightMeters ? maxY : minY + o.TrunkHeightMeters;

            // Footprint half-extents about the origin (the placement point), over the captured slice.
            float hx = 0f, hz = 0f;
            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 p = verts[i].Position;
                if (p.Y > captureTop) continue;
                float ax = MathF.Abs(p.X), az = MathF.Abs(p.Z);
                if (ax > hx) hx = ax;
                if (az > hz) hz = az;
            }

            float maxH = MathF.Max(hx, hz);
            float minH = MathF.Min(hx, hz);
            if (minH < 1e-4f || maxH / minH <= o.BoxAspectThreshold)
                return ColliderShape.Cylinder(maxH); // round-ish: cylinder that never under-covers
            return ColliderShape.Box(hx, hz);         // oblong: oriented box (rotated by the scatter yaw on Place)
        }

        /// <summary>Derive a collider footprint for every prop in <paramref name="manifest"/>, keyed by id: an
        /// entry's explicit <see cref="AssetEntry.Collider"/> wins; otherwise the prop's glTF is loaded
        /// (<see cref="PropLoader.LoadProp"/>, no GPU) and its footprint derived. Convenience for building the
        /// <c>id -&gt; ColliderShape</c> lookup that <c>KhaozEngine.Terrain.PropColliders.FromScatter</c> takes.</summary>
        public static IReadOnlyDictionary<string, ColliderShape> DeriveAll(
            AssetManifest manifest, PropFootprintOptions? options = null, PropValidation? validation = null)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            var result = new Dictionary<string, ColliderShape>(manifest.Props.Count);
            foreach (AssetEntry entry in manifest.Props)
                result[entry.Id] = entry.Collider ?? Derive(PropLoader.LoadProp(entry, validation), options);
            return result;
        }
    }
}
