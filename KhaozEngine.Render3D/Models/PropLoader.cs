using System;
using System.Collections.Generic;
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
        /// <summary>Load + normalize a manifest entry's glTF to its declared height as ONE flat mesh. When a source
        /// material carries a <c>baseColorTexture</c>, that texture's alpha-weighted average albedo
        /// (<see cref="GltfLoader.AverageAlbedo"/>) is folded into the material's flattened per-vertex colour, so a
        /// textures-ON kit still renders a sensible flat colour here (the same glb renders textured via
        /// <see cref="LoadPropParts"/>). A material WITHOUT a texture is untouched, so an existing untextured prop is
        /// byte-identical to before (the goldens-hold guarantee). Throws <see cref="InvalidOperationException"/>
        /// (with the entry id in the message) if the file cannot be loaded or the size is implausible.</summary>
        public static GltfMesh LoadProp(AssetEntry entry, PropValidation? validation = null)
            => Normalize(LoadRaw(entry), entry.HeightMeters, validation, entry.Id);

        static GltfMesh LoadRaw(AssetEntry entry) => LoadRaw(entry.File, entry.Id);

        static GltfMesh LoadRaw(string file, string id)
        {
            try { return GltfLoader.LoadFlattenedAlbedo(file); }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"PropLoader could not load prop '{id}' from '{file}': {ex.Message}", ex);
            }
        }

        /// <summary>Load + normalize a manifest entry like <see cref="LoadProp"/>, AND auto-read the glTF's first
        /// textured material's baseColor/normal/metallicRoughness textures (opt-in, via
        /// <see cref="GltfLoader.LoadWithMaterial"/>). Upload the mesh + maps with
        /// <see cref="Scene3D.LoadMesh(GltfMesh,GltfMaterialMaps)"/>. A prop whose glTF has no textures yields an
        /// all-absent <see cref="GltfMaterialMaps"/> (<see cref="GltfMaterialMaps.IsEmpty"/>), never a throw, so it
        /// renders exactly as <see cref="LoadProp"/>. The mesh is identical to <see cref="LoadProp"/>'s.</summary>
        public static (GltfMesh Mesh, GltfMaterialMaps Maps) LoadPropWithMaterial(AssetEntry entry, PropValidation? validation = null)
        {
            (GltfMesh raw, GltfMaterialMaps maps) = LoadRawWithMaterial(entry);
            GltfMesh mesh = Normalize(raw, entry.HeightMeters, validation, entry.Id);
            return (mesh, maps);
        }

        static (GltfMesh, GltfMaterialMaps) LoadRawWithMaterial(AssetEntry entry)
        {
            try { return GltfLoader.LoadWithMaterial(entry.File); }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"PropLoader could not load prop '{entry.Id}' from '{entry.File}': {ex.Message}", ex);
            }
        }

        /// <summary>Load + normalize a manifest entry as a multi-material prop: one <see cref="GltfMeshPart"/> per
        /// source material (via <see cref="GltfLoader.LoadPartsWithMaterials"/>), each with its own auto-read
        /// baseColor/normal/roughness maps. This is the multi-texture-per-primitive path - a tree with a separate
        /// bark and leaf material returns two textured parts, drawable each with its own texture, instead of the
        /// single flattened mesh <see cref="LoadPropWithMaterial"/> returns. All parts share ONE normalization
        /// transform computed over the whole prop's combined bounds (uniform scale to the declared
        /// <see cref="AssetEntry.HeightMeters"/>, base dropped to y=0, X/Z re-centred), so the parts stay aligned
        /// exactly as authored - never normalized independently. Validation (the 1.8 m human-scale guard) runs once
        /// on the combined size. Upload the result with <see cref="Scene3D.LoadProp"/>. A single-material asset
        /// yields one part whose geometry matches <see cref="LoadProp"/>.</summary>
        public static IReadOnlyList<GltfMeshPart> LoadPropParts(AssetEntry entry, PropValidation? validation = null)
            => NormalizeParts(LoadRawParts(entry), entry.HeightMeters, validation, entry.Id);

        /// <summary>Manifest-driven convenience: load a prop the way its <see cref="AssetEntry.Textured"/> flag asks,
        /// always returning a uniform <see cref="GltfMeshPart"/> list a caller can upload the same way regardless of
        /// mode (<see cref="Scene3D.LoadPropMeshes"/> / <see cref="Scene3D.LoadProp(IReadOnlyList{GltfMeshPart})"/>).
        /// When <see cref="AssetEntry.Textured"/> is true this is <see cref="LoadPropParts"/> - one textured sub-mesh
        /// per source material (bark + leaves, ...), each with its own decoded maps. When false it is the flat
        /// <see cref="LoadProp"/> wrapped as a SINGLE part with all-absent <see cref="GltfMaterialMaps"/>, so it
        /// renders untextured exactly as before (a textured glb still degrades to its flattened average colour). This
        /// keeps prop-loading call sites a one-liner: the manifest flag alone chooses textured vs flat.</summary>
        public static IReadOnlyList<GltfMeshPart> LoadPropAuto(AssetEntry entry, PropValidation? validation = null)
            => entry.Textured
                ? LoadPropParts(entry, validation)
                : new[] { new GltfMeshPart(LoadProp(entry, validation), default) };

        static IReadOnlyList<GltfMeshPart> LoadRawParts(AssetEntry entry) => LoadRawParts(entry.File, entry.Id);

        static IReadOnlyList<GltfMeshPart> LoadRawParts(string file, string id)
        {
            try { return GltfLoader.LoadPartsWithMaterials(file); }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"PropLoader could not load prop '{id}' from '{file}': {ex.Message}", ex);
            }
        }

        /// <summary>Load the AUTHOR-SUPPLIED far LOD variant for a prop the same way <see cref="LoadPropAuto"/> loads
        /// the full mesh (the manifest <see cref="AssetEntry.Textured"/> flag chooses textured parts vs a single flat
        /// part), normalized to the entry's <see cref="AssetEntry.HeightMeters"/> so the far mesh is the SAME
        /// on-screen size as the full one and the runtime swap is seamless. Returns null when the entry declares no
        /// <see cref="AssetEntry.LodFile"/> (the common case): the caller then simply has no LOD variant for that kit
        /// and <c>KhaozEngine.Terrain.PropRenderer</c> keeps drawing its full mesh at every distance. Upload the
        /// returned parts exactly like the full mesh (<see cref="Scene3D.LoadPropMeshes"/> for a per-kit part list, or
        /// wrap the single flat part as one handle) into a parallel LOD mesh set. The engine ships no mesh decimator,
        /// so this is never generated for you - author the low-poly glTF by hand. Throws
        /// <see cref="InvalidOperationException"/> (with the entry id) if the declared LOD file cannot be loaded or is
        /// implausibly sized.</summary>
        public static IReadOnlyList<GltfMeshPart>? LoadPropLodAuto(AssetEntry entry, PropValidation? validation = null)
        {
            if (string.IsNullOrWhiteSpace(entry.LodFile)) return null;
            return entry.Textured
                ? NormalizeParts(LoadRawParts(entry.LodFile!, entry.Id), entry.HeightMeters, validation, entry.Id)
                : new[] { new GltfMeshPart(Normalize(LoadRaw(entry.LodFile!, entry.Id), entry.HeightMeters, validation, entry.Id), default) };
        }

        /// <summary>Normalize a set of raw material parts as ONE prop: scale + recentre every part by a single
        /// transform derived from their combined bounds (so parts stay aligned), validated against
        /// <paramref name="validation"/> like <see cref="Normalize(GltfMesh,float,PropValidation?)"/>. Public for a
        /// consumer that already has parts in hand (e.g. from <see cref="GltfLoader.LoadPartsWithMaterials"/>).</summary>
        public static IReadOnlyList<GltfMeshPart> NormalizeParts(IReadOnlyList<GltfMeshPart> parts, float heightMeters,
                                                                 PropValidation? validation = null)
            => NormalizeParts(parts, heightMeters, validation, null);

        static IReadOnlyList<GltfMeshPart> NormalizeParts(IReadOnlyList<GltfMeshPart> parts, float heightMeters,
                                                          PropValidation? validation, string? id)
        {
            if (parts == null) throw new ArgumentNullException(nameof(parts));
            if (parts.Count == 0) throw new ArgumentException("a prop needs at least one material part.", nameof(parts));
            PropValidation v = validation ?? PropValidation.Default;
            string what = id == null ? "prop" : $"prop '{id}'";
            for (int p = 0; p < parts.Count; p++)
                MeshIndexValidation.All(parts[p].Mesh, $"{what} part {p}");

            if (heightMeters < v.MinHeightMeters || heightMeters > v.MaxHeightMeters)
                throw new InvalidOperationException(
                    $"{what} declares an implausible height {heightMeters} m (outside {v.MinHeightMeters}..{v.MaxHeightMeters} m).");

            // Combined bounds over EVERY part, so a single shared transform keeps the parts aligned.
            var mn = new Vector3(float.MaxValue);
            var mx = new Vector3(float.MinValue);
            foreach (GltfMeshPart part in parts)
            {
                ModelVertex[] verts = part.Mesh.Vertices;
                for (int i = 0; i < verts.Length; i++) { mn = Vector3.Min(mn, verts[i].Position); mx = Vector3.Max(mx, verts[i].Position); }
            }

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

            var outParts = new GltfMeshPart[parts.Count];
            for (int p = 0; p < parts.Count; p++)
                outParts[p] = new GltfMeshPart(Transform(parts[p].Mesh, cx, cz, baseY, scale), parts[p].Maps);
            return outParts;
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
            MeshIndexValidation.All(raw, what);

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
            return Transform(raw, cx, cz, baseY, scale);
        }

        // Apply the normalization transform (X/Z re-centred on cx/cz, base dropped to y=0, uniform scale) to a
        // mesh's vertices, preserving indices. Uniform scale preserves normal + tangent directions, so those are
        // left unchanged. Shared by the single-mesh and multi-part paths.
        static GltfMesh Transform(GltfMesh mesh, float cx, float cz, float baseY, float scale)
        {
            ModelVertex[] verts = mesh.Vertices;
            var outVerts = new ModelVertex[verts.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                ModelVertex src = verts[i];
                src.Position = new Vector3(
                    (src.Position.X - cx) * scale,
                    (src.Position.Y - baseY) * scale,
                    (src.Position.Z - cz) * scale);
                outVerts[i] = src;
            }
            return new GltfMesh(outVerts, mesh.Indices32);
        }
    }
}
