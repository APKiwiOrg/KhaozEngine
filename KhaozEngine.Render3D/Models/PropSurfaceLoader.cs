using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Collision;

namespace KhaozEngine.Render3D
{
    /// <summary>Loads baked prop surfaces (render-free <see cref="PropSurface.Read"/>) referenced from a manifest,
    /// and (tooling) bakes + writes the binary for a prop. The runtime path is read-only and GPU-free, so the
    /// headless authoritative server and the client share identical surface data.</summary>
    public static class PropSurfaceLoader
    {
        /// <summary>Read every entry's referenced <c>.surf</c> into an id -> <see cref="PropSurface"/> map (entries
        /// with no <c>Heightmap</c> are skipped).</summary>
        public static IReadOnlyDictionary<string, PropSurface> LoadAll(AssetManifest manifest)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            var result = new Dictionary<string, PropSurface>();
            foreach (AssetEntry e in manifest.Props)
            {
                if (string.IsNullOrEmpty(e.Heightmap)) continue;
                using FileStream fs = File.OpenRead(e.Heightmap);
                result[e.Id] = PropSurface.Read(fs);
            }
            return result;
        }

        /// <summary>Load + normalize the prop mesh, bake its top-surface grid, and write the binary to
        /// <paramref name="outPath"/>. Tooling only (loads the glTF; no GPU).</summary>
        public static void BakeAndWrite(AssetEntry entry, string outPath, PropSurfaceBakeOptions? options = null)
        {
            GltfMesh mesh = PropLoader.LoadProp(entry);
            PropSurface surface = PropSurfaceBake.Bake(mesh, options);
            using FileStream fs = File.Create(outPath);
            surface.Write(fs);
        }
    }
}
