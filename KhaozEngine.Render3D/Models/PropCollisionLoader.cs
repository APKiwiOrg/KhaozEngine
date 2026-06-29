using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Physics;

namespace KhaozEngine.Render3D
{
    /// <summary>The manifest-driven render-side loader for baked 3D collision shapes (<c>.coll</c>) produced by
    /// <see cref="PropCollisionBake"/>. Render-free already, but it lives in KhaozEngine.Render3D because
    /// <see cref="AssetManifest"/> does. The actual KECL decode is now <see cref="PropCollisionFormat"/> in the
    /// dependency-free KhaozEngine.Physics package, so a headless authoritative server (no GPU/windowing) can
    /// load the same shapes via <see cref="PropCollisionFormat.LoadDirectory"/> /
    /// <see cref="PropCollisionFormat.Load"/> without referencing Render3D.</summary>
    public static class PropCollisionLoader
    {
        /// <summary>Read a single baked shape from <paramref name="stream"/>. Throws
        /// <see cref="InvalidOperationException"/> on a bad magic, unsupported version, or unknown kind.
        /// Delegates to <see cref="PropCollisionFormat.Read(Stream)"/>.</summary>
        public static PhysicsShape Read(Stream stream) => PropCollisionFormat.Read(stream);

        /// <summary>Read every entry's referenced <c>.coll</c> into an id -> <see cref="PhysicsShape"/> map.
        /// Entries with no <see cref="AssetEntry.CollisionShape"/> path are skipped. The manifest-driven path
        /// (a headless server with no manifest uses <see cref="PropCollisionFormat.LoadDirectory"/> instead).</summary>
        public static IReadOnlyDictionary<string, PhysicsShape> LoadAll(AssetManifest manifest)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            var result = new Dictionary<string, PhysicsShape>();
            foreach (AssetEntry e in manifest.Props)
            {
                if (string.IsNullOrEmpty(e.CollisionShape)) continue;
                result[e.Id] = PropCollisionFormat.Read(e.CollisionShape);
            }
            return result;
        }
    }
}
