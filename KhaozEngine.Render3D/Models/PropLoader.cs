using System;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>The human-scale plausibility band for <see cref="PropLoader"/>. A prop is authored at roughly
    /// 1 unit = 1 metre (the Blender 1.8 m human reference rule), so the loader rejects two failure modes loudly:
    /// a declared <see cref="AssetEntry.HeightMeters"/> outside <see cref="MinHeightMeters"/>..<see
    /// cref="MaxHeightMeters"/> (a typo'd manifest), and an implied uniform scale outside <see cref="MinScale"/>..
    /// <see cref="MaxScale"/> (the raw asset is in the wrong units - e.g. exported in millimetres or kilometres).
    /// </summary>
    public sealed class PropValidation
    {
        /// <summary>Smallest plausible declared height (m). Default 0.1 m (a pebble).</summary>
        public float MinHeightMeters = 0.1f;
        /// <summary>Largest plausible declared height (m). Default 120 m (a very tall tree). Raise per category.</summary>
        public float MaxHeightMeters = 120f;
        /// <summary>Smallest plausible raw-to-declared uniform scale before the asset is judged mis-authored.</summary>
        public float MinScale = 1e-3f;
        /// <summary>Largest plausible raw-to-declared uniform scale before the asset is judged mis-authored.</summary>
        public float MaxScale = 1e3f;

        /// <summary>The default human-scale band.</summary>
        public static readonly PropValidation Default = new();
    }

    /// <summary>Loads a prop kit asset (decompressed glTF) and normalizes it for placement: scale uniformly to the
    /// manifest's real-world height, drop the origin to the base (feet on the ground), and re-centre X/Z on the
    /// origin so the placement (x,z) is the trunk. Validation throws loudly on an implausible declared-vs-actual
    /// size (the 1.8 m guard). Reuses <see cref="GltfLoader"/> - the engine has no meshopt decoder, so kit assets
    /// must be decompressed offline first (see <see cref="AssetManifest"/> / docs/USING-KHAOZENGINE.md).</summary>
    public static class PropLoader
    {
        /// <summary>Load + normalize a manifest entry's glTF to its declared height (geometry only). Throws
        /// <see cref="InvalidOperationException"/> (with the entry id in the message) if the file cannot be loaded
        /// or the size is implausible.</summary>
        public static GltfMesh LoadProp(AssetEntry entry, PropValidation? validation = null)
            => Normalize(LoadRaw(entry), entry.HeightMeters, validation, entry.Id);

        /// <summary>Like <see cref="LoadProp"/> but also auto-reads the first textured material's decoded maps (for
        /// callers that upload textures); the mesh is normalized identically. A kit baked to flat per-material
        /// base colours (no textures) yields an all-absent <see cref="GltfMaterialMaps"/>, which is the expected
        /// case for the committed CC0 props.</summary>
        public static (GltfMesh Mesh, GltfMaterialMaps Maps) LoadPropWithMaterial(AssetEntry entry, PropValidation? validation = null)
        {
            (GltfMesh raw, GltfMaterialMaps maps) = LoadRawWithMaterial(entry);
            return (Normalize(raw, entry.HeightMeters, validation, entry.Id), maps);
        }

        static GltfMesh LoadRaw(AssetEntry entry)
        {
            try { return GltfLoader.Load(entry.File); }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"PropLoader could not load prop '{entry.Id}' from '{entry.File}': {ex.Message}", ex);
            }
        }

        static (GltfMesh, GltfMaterialMaps) LoadRawWithMaterial(AssetEntry entry)
        {
            try { return GltfLoader.LoadWithMaterial(entry.File); }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"PropLoader could not load prop '{entry.Id}' from '{entry.File}': {ex.Message}", ex);
            }
        }

        /// <summary>Scale a raw mesh uniformly so its vertical (Y) extent equals <paramref name="heightMeters"/>,
        /// drop the base to y=0, and re-centre X/Z on the origin. Validates the declared height and the implied
        /// scale against <paramref name="validation"/> (default <see cref="PropValidation.Default"/>); throws
        /// <see cref="InvalidOperationException"/> on a degenerate mesh or an implausible size.</summary>
        public static GltfMesh Normalize(GltfMesh raw, float heightMeters, PropValidation? validation = null)
            => Normalize(raw, heightMeters, validation, null);

        static GltfMesh Normalize(GltfMesh raw, float heightMeters, PropValidation? validation, string? id)
        {
            if (raw == null) throw new ArgumentNullException(nameof(raw));
            PropValidation v = validation ?? PropValidation.Default;
            string what = id == null ? "prop" : $"prop '{id}'";

            if (heightMeters < v.MinHeightMeters || heightMeters > v.MaxHeightMeters)
                throw new InvalidOperationException(
                    $"{what} declares an implausible height {heightMeters} m (outside {v.MinHeightMeters}..{v.MaxHeightMeters} m).");

            // Measure the loaded mesh.
            var mn = new Vector3(float.MaxValue);
            var mx = new Vector3(float.MinValue);
            ModelVertex[] verts = raw.Vertices;
            for (int i = 0; i < verts.Length; i++) { mn = Vector3.Min(mn, verts[i].Position); mx = Vector3.Max(mx, verts[i].Position); }

            float rawHeight = mx.Y - mn.Y;
            if (rawHeight <= 1e-6f)
                throw new InvalidOperationException($"{what} mesh has no measurable height (degenerate or non-Y-up).");

            float scale = heightMeters / rawHeight;
            if (scale < v.MinScale || scale > v.MaxScale)
                throw new InvalidOperationException(
                    $"{what} needs a {scale:G4} scale to reach {heightMeters} m from a {rawHeight:G4}-unit mesh " +
                    $"(outside {v.MinScale:G4}..{v.MaxScale:G4}); the asset is likely in the wrong units (authored ~1u=1m).");

            float cx = (mn.X + mx.X) * 0.5f;
            float cz = (mn.Z + mx.Z) * 0.5f;
            float baseY = mn.Y;

            var outVerts = new ModelVertex[verts.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                ModelVertex src = verts[i];
                src.Position = new Vector3(
                    (src.Position.X - cx) * scale,
                    (src.Position.Y - baseY) * scale,
                    (src.Position.Z - cz) * scale);
                // Uniform scale preserves normal + tangent directions; leave them unchanged.
                outVerts[i] = src;
            }
            return new GltfMesh(outVerts, raw.Indices32);
        }
    }
}
