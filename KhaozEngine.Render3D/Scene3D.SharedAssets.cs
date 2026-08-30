using System;
using System.Collections.Generic;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Keyed, idempotent asset loads for a scene that outlives the things it draws
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/250">#250</see>). The plain
    /// <c>LoadTexture</c> / <c>LoadMesh</c> / <c>LoadSkinnedMesh</c> upload every time they are called, which is
    /// right for a scene built once and torn down once, and wrong for an app-lifetime scene whose renderer
    /// rebuilds its asset set on every run restart. Every consumer with that lifecycle was hand-rolling the same
    /// cache-once state (static handle fields keyed by asset path), so the keying lives here instead.
    /// <para>
    /// <b>Ownership does not change.</b> A cached handle is owned by the scene exactly as the underlying
    /// <c>Load*</c> left it: the cache adds a NAME for a handle, not a second owner. <see cref="Scene3D.Dispose"/>
    /// frees the cached handles along with every other handle the scene owns, because they are the same handles,
    /// and the key tables die with the scene.
    /// </para>
    /// <para>
    /// <b>Eviction is explicit, by key, with no refcount.</b> <c>UnloadShared*</c> is the only eviction: it
    /// unloads through the matching <c>Unload*</c> and drops the entry, so the next <c>GetOrLoad*</c> uploads
    /// again. Nothing evicts on its own, and nothing counts references. A refcount would need a matching release
    /// at every use site, which is precisely the bookkeeping a consumer reaches for this API to stop doing, and
    /// the lifecycle that asked for it (cache once, keep for the app's life, drop the whole set at a level change)
    /// does not need one. Calling the plain <c>Unload*</c> on a cached handle DIRECTLY leaves the key pointing at
    /// a freed handle, so pair the two: cache with <c>GetOrLoad*</c>, free with <c>UnloadShared*</c>.
    /// </para>
    /// </summary>
    public sealed partial class Scene3D
    {
        // Ordinal, case-sensitive: the keys are asset paths and identifiers, not display text, and a
        // case-insensitive match would silently fuse two files that differ only in case on a case-sensitive
        // filesystem. Each family has its OWN key space, so "hull" as a mesh and "hull" as a texture are two
        // entries, not a collision.
        readonly Dictionary<string, MeshHandle> _sharedMeshes = new(StringComparer.Ordinal);
        readonly Dictionary<string, SkinnedMeshHandle> _sharedSkinnedMeshes = new(StringComparer.Ordinal);
        readonly Dictionary<string, TextureHandle> _sharedTextures = new(StringComparer.Ordinal);

        /// <summary>How many keyed assets this scene is holding, summed across meshes, skinned meshes and
        /// textures. Diagnostics: a number that climbs across run restarts is a consumer still re-keying its
        /// assets rather than reusing the key.</summary>
        public int SharedAssetCount => _sharedMeshes.Count + _sharedSkinnedMeshes.Count + _sharedTextures.Count;

        /// <summary>
        /// Return the mesh already cached under <paramref name="key"/>, or run <paramref name="load"/> once and
        /// cache what it returns. The repeat call never reaches the GPU. If <paramref name="load"/> throws,
        /// nothing is cached and the next call retries. Not re-entrant: a loader that calls back in on the same
        /// key uploads twice and the outer result is the one that ends up cached.
        /// </summary>
        public MeshHandle GetOrLoadMesh(string key, Func<MeshHandle> load)
        {
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(load);
            if (_sharedMeshes.TryGetValue(key, out MeshHandle existing)) return existing;
            MeshHandle loaded = load();
            _sharedMeshes[key] = loaded;
            return loaded;
        }

        /// <summary>
        /// Return the skinned mesh already cached under <paramref name="key"/>, or run <paramref name="load"/>
        /// once and cache what it returns. Same contract as <see cref="GetOrLoadMesh"/>.
        /// </summary>
        public SkinnedMeshHandle GetOrLoadSkinnedMesh(string key, Func<SkinnedMeshHandle> load)
        {
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(load);
            if (_sharedSkinnedMeshes.TryGetValue(key, out SkinnedMeshHandle existing)) return existing;
            SkinnedMeshHandle loaded = load();
            _sharedSkinnedMeshes[key] = loaded;
            return loaded;
        }

        /// <summary>
        /// Return the texture already cached under <paramref name="key"/>, or run <paramref name="load"/> once and
        /// cache what it returns. Same contract as <see cref="GetOrLoadMesh"/>. This is the one that mattered in
        /// the field: an albedo set re-uploaded per run restart is a native texture leaked per restart.
        /// </summary>
        public TextureHandle GetOrLoadTexture(string key, Func<TextureHandle> load)
        {
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(load);
            if (_sharedTextures.TryGetValue(key, out TextureHandle existing)) return existing;
            TextureHandle loaded = load();
            _sharedTextures[key] = loaded;
            return loaded;
        }

        /// <summary>Unload the mesh cached under <paramref name="key"/> (via <see cref="UnloadMesh"/>) and forget
        /// the key, so a later <see cref="GetOrLoadMesh"/> uploads again. <c>false</c> when nothing was cached
        /// under that key, which is a harmless no-op.</summary>
        public bool UnloadSharedMesh(string key)
        {
            ArgumentNullException.ThrowIfNull(key);
            if (!_sharedMeshes.Remove(key, out MeshHandle h)) return false;
            UnloadMesh(h);
            return true;
        }

        /// <summary>Unload the skinned mesh cached under <paramref name="key"/> (via
        /// <see cref="UnloadSkinnedMesh"/>) and forget the key. <c>false</c> when nothing was cached under
        /// it.</summary>
        public bool UnloadSharedSkinnedMesh(string key)
        {
            ArgumentNullException.ThrowIfNull(key);
            if (!_sharedSkinnedMeshes.Remove(key, out SkinnedMeshHandle h)) return false;
            UnloadSkinnedMesh(h);
            return true;
        }

        /// <summary>Unload the texture cached under <paramref name="key"/> (via <see cref="UnloadTexture"/>) and
        /// forget the key. <c>false</c> when nothing was cached under it. A texture can still be bound by a mesh
        /// loaded against it, exactly as with the plain <see cref="UnloadTexture"/>: unload those meshes first or
        /// stop drawing them.</summary>
        public bool UnloadSharedTexture(string key)
        {
            ArgumentNullException.ThrowIfNull(key);
            if (!_sharedTextures.Remove(key, out TextureHandle h)) return false;
            UnloadTexture(h);
            return true;
        }
    }
}
