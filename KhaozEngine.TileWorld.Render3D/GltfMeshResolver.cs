using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using KhaozEngine.Render3D;

namespace KhaozEngine.TileWorld;

/// <summary>A resolver backed by real content: it maps an archetype's <see cref="TileObjectArchetype.MeshRef"/>
/// and optional <see cref="TileObjectArchetype.LodMeshRef"/> to glbs under a kit root. Drawable parts load
/// through <see cref="GltfLoader.LoadPartsWithMaterials"/>, while the HLOD source loads through
/// <see cref="GltfLoader.LoadFlattenedAlbedo"/>. A mesh reference is
/// authored with forward slashes and relative to the root (<c>kit/wall.glb</c>), normalized to the platform
/// separator here, and an already-absolute reference is used as it stands.
///
/// <para>Content outlives a kit edit, so a missing file, a loader throw, or a mesh reference no path API will
/// even accept is NOT fatal: it logs ONE line naming the archetype, the path and the reason, then answers with
/// the fallback resolver's mesh. NOTHING here throws out of either <c>Resolve</c> overload for bad content. Pass a
/// <see cref="GreyboxMeshResolver"/> as the fallback and a half-authored kit renders as boxes where the glb is
/// missing and as the real mesh everywhere else. With no fallback the answer is null, which the view draws as its
/// placeholder box plus a line of its own. An archetype with an EMPTY mesh reference is not a failure at all (it
/// is simply not authored yet), so it goes straight to the fallback with no log line and without touching the
/// disk.</para>
///
/// <para>Every result is cached per MESH REFERENCE, failures included, so a later call for a failed reference hands
/// back the same answer without re-logging or probing the disk again, and the two <c>Resolve</c> overloads share
/// one entry: an avatar drawn through <see cref="Resolve(string)"/> and an archetype pointing at the same glb parse
/// it once between them. Two archetypes sharing one <c>MeshRef</c> likewise hold one copy rather than two, and a
/// missing file logs once per FILE rather than once per archetype. The cache is a plain dictionary, so resolve on
/// one thread, exactly as <see cref="GreyboxMeshResolver"/> documents, and the list handed out is read-only, so a
/// caller cannot write through it into the shared cache. What is NOT cached here is the fallback's answer, because
/// it is the ARCHETYPE's rather than the reference's: a missing glb asks the fallback again on every call, and both
/// shipped fallbacks cache, so the same list still comes back. There is no eviction either, so the
/// parts, including each part's decoded RGBA8 <see cref="GltfMaterialMaps"/> pixels, stay live for the resolver's
/// lifetime even after a view has uploaded them, and a glb regenerated while the app runs is never picked
/// up.</para>
///
/// <para>A glb is PLANE-LOCAL and drawn exactly as authored: nothing here scales, rotates or re-centres it. The
/// contract it has to meet is the one <see cref="GreyboxMeshResolver"/> builds to, and it is spelled out in this
/// package's README: the origin sits at the footprint CENTRE on the piece's own floor, x is east, minus z is
/// north, and 1 unit is 1 metre.</para></summary>
public sealed class GltfMeshResolver : ITileMeshResolver, ITileLodMeshResolver
{
    readonly string _rootDirectory;
    readonly ITileMeshResolver? _fallback;
    readonly Action<string>? _log;

    // Full and LOD parts are separate tiers even when a catalog deliberately gives them the same reference. The
    // two full Resolve overloads still share one entry. Null is a cached answer, not a miss.
    readonly Dictionary<string, IReadOnlyList<GltfMeshPart>?> _fullPartsCache = new(StringComparer.Ordinal);
    readonly Dictionary<string, IReadOnlyList<GltfMeshPart>?> _lodPartsCache = new(StringComparer.Ordinal);
    readonly Dictionary<string, GltfMesh?> _flatCache = new(StringComparer.Ordinal);
    readonly HashSet<(LoadPurpose Purpose, string Path)> _loggedFailures = new();

    enum LoadPurpose
    {
        FullParts,
        LodParts,
        FlatLod1,
        FlatLod0,
    }

    /// <summary>A resolver that loads each archetype's <c>MeshRef</c> from under <paramref name="rootDirectory"/>,
    /// answering with <paramref name="fallback"/> (null when none) whenever a glb is missing or fails to load, and
    /// reporting each such archetype once through <paramref name="log"/>.</summary>
    public GltfMeshResolver(string rootDirectory, ITileMeshResolver? fallback = null, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(rootDirectory);
        _rootDirectory = rootDirectory;
        _fallback = fallback;
        _log = log;
    }

    /// <summary>The parts that draw this archetype, loaded once and handed back on every later call, or the
    /// fallback's answer (null when there is no fallback) when the glb is missing or unreadable. The list is
    /// read-only, so a caller cannot write through it into the shared cache. Never throws over bad content.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="archetype"/> is null.</exception>
    public IReadOnlyList<GltfMeshPart>? Resolve(TileObjectArchetype archetype)
    {
        ArgumentNullException.ThrowIfNull(archetype);
        return Resolve(archetype.MeshRef, archetype);
    }

    /// <summary>The parts that draw this mesh reference with no archetype behind it, off the SAME cache the
    /// archetype overload fills, so an avatar and a tile object pointing at one glb parse it once between them.
    /// A reference that resolves to nothing falls back exactly as an archetype does, on an archetype carrying the
    /// reference and the type's own defaults. Never throws over bad content.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="meshRef"/> is null.</exception>
    public IReadOnlyList<GltfMeshPart>? Resolve(string meshRef)
    {
        ArgumentNullException.ThrowIfNull(meshRef);
        return Resolve(meshRef, archetype: null);
    }

    /// <summary>The optional authored LOD parts for this archetype. A blank, missing, or malformed LOD answers
    /// null without consulting the required-mesh fallback, so a caller can retain LOD0. Failures are cached and
    /// logged once. Never throws over bad content.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="archetype"/> is null.</exception>
    public IReadOnlyList<GltfMeshPart>? ResolveLod(TileObjectArchetype archetype)
    {
        ArgumentNullException.ThrowIfNull(archetype);
        return string.IsNullOrWhiteSpace(archetype.LodMeshRef)
            ? null
            : ResolveParts(_lodPartsCache, archetype.LodMeshRef, archetype, LoadPurpose.LodParts);
    }

    /// <summary>The flattened CPU mesh used to build HLOD for this archetype. The authored LOD is preferred when
    /// present and valid, otherwise the full mesh is tried. Missing or malformed content answers null when neither
    /// source loads, so individual geometry can remain visible. Results and failures are cached per mesh reference.
    /// Never throws over bad content.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="archetype"/> is null.</exception>
    public GltfMesh? ResolveFlatForHlod(TileObjectArchetype archetype)
    {
        ArgumentNullException.ThrowIfNull(archetype);
        if (!string.IsNullOrWhiteSpace(archetype.LodMeshRef))
        {
            GltfMesh? lod = ResolveFlat(archetype.LodMeshRef, archetype, LoadPurpose.FlatLod1);
            if (lod is not null) return lod;
        }
        return string.IsNullOrWhiteSpace(archetype.MeshRef)
            ? null
            : ResolveFlat(archetype.MeshRef, archetype, LoadPurpose.FlatLod0);
    }

    IReadOnlyList<GltfMeshPart>? Resolve(string meshRef, TileObjectArchetype? archetype)
    {
        // Not authored yet is not a failure, so an empty reference goes straight to the fallback: no log line, no
        // disk, and nothing cached, because there is no content behind the key to remember.
        if (string.IsNullOrWhiteSpace(meshRef)) return FallBack(meshRef, archetype);
        IReadOnlyList<GltfMeshPart>? cached =
            ResolveParts(_fullPartsCache, meshRef, archetype, LoadPurpose.FullParts);
        return cached ?? FallBack(meshRef, archetype);
    }

    static IReadOnlyList<GltfMeshPart>? CachedParts(
        Dictionary<string, IReadOnlyList<GltfMeshPart>?> cache,
        string meshRef,
        Func<IReadOnlyList<GltfMeshPart>?> load)
    {
        if (!cache.TryGetValue(meshRef, out IReadOnlyList<GltfMeshPart>? cached))
            cache[meshRef] = cached = load();
        return cached;
    }

    IReadOnlyList<GltfMeshPart>? ResolveParts(
        Dictionary<string, IReadOnlyList<GltfMeshPart>?> cache,
        string meshRef,
        TileObjectArchetype? archetype,
        LoadPurpose purpose) =>
        CachedParts(cache, meshRef, () => LoadPartsOnce(meshRef, archetype, purpose));

    GltfMesh? ResolveFlat(string meshRef, TileObjectArchetype archetype, LoadPurpose purpose)
    {
        if (!_flatCache.TryGetValue(meshRef, out GltfMesh? cached))
            _flatCache[meshRef] = cached = LoadFlatOnce(meshRef, archetype, purpose);
        return cached;
    }

    /// <summary>The absolute path an archetype's mesh reference names under this resolver's root. An absolute
    /// reference is returned as it stands, and forward slashes become the platform separator either way. Throws
    /// whatever <see cref="Path.GetFullPath(string)"/> throws for a reference no path API will accept, so a
    /// pre-flight caller catches it. Both <c>Resolve</c> overloads catch it for you and fall back.</summary>
    public string PathFor(string meshRef)
    {
        ArgumentNullException.ThrowIfNull(meshRef);
        // Path.Combine hands back an already-rooted second argument untouched, so this covers both forms.
        return Path.GetFullPath(Path.Combine(_rootDirectory, meshRef.Replace('/', Path.DirectorySeparatorChar)));
    }

    // Null means this reference has no parts of its own, which is the cached failure the log-once rule keys on.
    IReadOnlyList<GltfMeshPart>? LoadPartsOnce(
        string meshRef,
        TileObjectArchetype? archetype,
        LoadPurpose purpose)
    {
        // PathFor is INSIDE the try on purpose: a MeshRef out of a corrupt catalog can carry a character
        // Path.GetFullPath rejects (an embedded NUL throws ArgumentException), and a bad ref must never fault the
        // view. The ref itself goes in the line then, because there is no resolved path to name.
        string path = meshRef;
        try
        {
            path = PathFor(meshRef);
            // Probed rather than caught, because a missing kit piece is the ordinary half-authored case and
            // deserves a reason a reader can act on, not whatever the loader throws for an absent file.
            if (!File.Exists(path))
            {
                LogFailure(path, "file not found", archetype, purpose);
                return null;
            }
            // Wrapped before it leaves, so a caller cannot write through the handed-out list into the cache.
            return new ReadOnlyCollection<GltfMeshPart>(GltfLoader.LoadPartsWithMaterials(path).ToArray());
        }
        catch (Exception ex)
        {
            // Deliberately broad: a corrupt or unsupported glb, or a ref no path API will accept, surfaces as
            // anything from a format exception to an IO one, and none of them is worth taking the whole world
            // down for when a greybox box will do.
            LogFailure(path, ex.Message, archetype, purpose);
            return null;
        }
    }

    GltfMesh? LoadFlatOnce(string meshRef, TileObjectArchetype archetype, LoadPurpose purpose)
    {
        string path = meshRef;
        try
        {
            path = PathFor(meshRef);
            if (!File.Exists(path))
            {
                LogFailure(path, "file not found", archetype, purpose);
                return null;
            }
            return GltfLoader.LoadFlattenedAlbedo(path);
        }
        catch (Exception ex)
        {
            LogFailure(path, ex.Message, archetype, purpose);
            return null;
        }
    }

    // The archetype is named when there is one, because a catalog id is what a reader fixes the content under. It
    // is the FIRST caller's archetype either way: the line is written once per mesh reference, so a second
    // archetype on the same broken reference is answered off the cached failure and never reaches here.
    void LogFailure(
        string path,
        string reason,
        TileObjectArchetype? archetype,
        LoadPurpose purpose)
    {
        if (!_loggedFailures.Add((purpose, path))) return;
        string prefix = archetype is null ? "tile world:" : $"tile world: archetype '{archetype.Id}'";
        _log?.Invoke(purpose switch
        {
            LoadPurpose.FullParts => $"{prefix} could not load mesh '{path}' ({reason}), falling back.",
            LoadPurpose.LodParts =>
                $"{prefix} could not load optional LOD mesh '{path}' ({reason}), retaining LOD0.",
            LoadPurpose.FlatLod1 =>
                $"{prefix} could not load flattened LOD1 mesh '{path}' ({reason}), trying LOD0.",
            LoadPurpose.FlatLod0 =>
                $"{prefix} could not load flattened LOD0 mesh '{path}' ({reason}), retaining individual geometry.",
            _ => throw new InvalidOperationException($"Unknown mesh load purpose {purpose}."),
        });
    }

    IReadOnlyList<GltfMeshPart>? FallBack(string meshRef, TileObjectArchetype? archetype) =>
        archetype is null ? _fallback?.Resolve(meshRef) : _fallback?.Resolve(archetype);
}
