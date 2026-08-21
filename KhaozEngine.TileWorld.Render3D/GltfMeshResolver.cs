using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using KhaozEngine.Render3D;

namespace KhaozEngine.TileWorld;

/// <summary>A resolver backed by real content: it maps an archetype's <see cref="TileObjectArchetype.MeshRef"/>
/// to a glb under a kit root and loads it through <see cref="GltfLoader.LoadPartsWithMaterials"/>, so a tile
/// world draws its authored kit instead of <see cref="GreyboxMeshResolver"/>'s boxes. A mesh reference is
/// authored with forward slashes and relative to the root (<c>kit/wall.glb</c>), normalized to the platform
/// separator here, and an already-absolute reference is used as it stands.
///
/// <para>Content outlives a kit edit, so a missing file, a loader throw, or a mesh reference no path API will
/// even accept is NOT fatal: it logs ONE line naming the archetype, the path and the reason, then answers with
/// the fallback resolver's mesh. NOTHING here throws out of <see cref="Resolve"/> for bad content. Pass a
/// <see cref="GreyboxMeshResolver"/> as the fallback and a half-authored kit renders as boxes where the glb is
/// missing and as the real mesh everywhere else. With no fallback the answer is null, which the view draws as its
/// placeholder box plus a line of its own. An archetype with an EMPTY mesh reference is not a failure at all (it
/// is simply not authored yet), so it goes straight to the fallback with no log line and without touching the
/// disk.</para>
///
/// <para>Every result is cached per archetype id, failures included, so a later call for a failed id hands back
/// the same answer without re-logging or probing the disk again. The cache is a plain dictionary, so resolve on
/// one thread, exactly as <see cref="GreyboxMeshResolver"/> documents, and the list handed out is read-only, so a
/// caller cannot write through it into the shared cache. Two things follow from keying on the ARCHETYPE ID rather
/// than the resolved path: two archetypes sharing one <c>MeshRef</c> parse that glb twice and hold two copies,
/// and one missing file logs once per archetype rather than once per file. There is no eviction either, so the
/// parts, including each part's decoded RGBA8 <see cref="GltfMaterialMaps"/> pixels, stay live for the resolver's
/// lifetime even after a view has uploaded them, and a glb regenerated while the app runs is never picked
/// up.</para>
///
/// <para>A glb is PLANE-LOCAL and drawn exactly as authored: nothing here scales, rotates or re-centres it. The
/// contract it has to meet is the one <see cref="GreyboxMeshResolver"/> builds to, and it is spelled out in this
/// package's README: the origin sits at the footprint CENTRE on the piece's own floor, x is east, minus z is
/// north, and 1 unit is 1 metre.</para></summary>
public sealed class GltfMeshResolver : ITileMeshResolver
{
    readonly string _rootDirectory;
    readonly ITileMeshResolver? _fallback;
    readonly Action<string>? _log;

    // Null is a CACHED answer here, not a miss, which is what makes the log-once rule hold: a failed id is
    // recorded with the value it resolved to (the fallback's parts, or null when there is no fallback).
    readonly Dictionary<string, IReadOnlyList<GltfMeshPart>?> _cache = new(StringComparer.Ordinal);

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
    public IReadOnlyList<GltfMeshPart>? Resolve(TileObjectArchetype archetype)
    {
        ArgumentNullException.ThrowIfNull(archetype);
        if (_cache.TryGetValue(archetype.Id, out IReadOnlyList<GltfMeshPart>? cached)) return cached;

        IReadOnlyList<GltfMeshPart>? parts = LoadOnce(archetype);
        _cache[archetype.Id] = parts;
        return parts;
    }

    /// <summary>The absolute path an archetype's mesh reference names under this resolver's root. An absolute
    /// reference is returned as it stands, and forward slashes become the platform separator either way. Throws
    /// whatever <see cref="Path.GetFullPath(string)"/> throws for a reference no path API will accept, so a
    /// pre-flight caller catches it. <see cref="Resolve"/> catches it for you and falls back.</summary>
    public string PathFor(string meshRef)
    {
        ArgumentNullException.ThrowIfNull(meshRef);
        // Path.Combine hands back an already-rooted second argument untouched, so this covers both forms.
        return Path.GetFullPath(Path.Combine(_rootDirectory, meshRef.Replace('/', Path.DirectorySeparatorChar)));
    }

    IReadOnlyList<GltfMeshPart>? LoadOnce(TileObjectArchetype archetype)
    {
        if (string.IsNullOrWhiteSpace(archetype.MeshRef)) return _fallback?.Resolve(archetype);

        // PathFor is INSIDE the try on purpose: a MeshRef out of a corrupt catalog can carry a character
        // Path.GetFullPath rejects (an embedded NUL throws ArgumentException), and a bad ref must never fault the
        // view. The ref itself goes in the line then, because there is no resolved path to name.
        string path = archetype.MeshRef;
        try
        {
            path = PathFor(archetype.MeshRef);
            // Probed rather than caught, because a missing kit piece is the ordinary half-authored case and
            // deserves a reason a reader can act on, not whatever the loader throws for an absent file.
            if (!File.Exists(path)) return FallBack(archetype, path, "file not found");
            // Wrapped before it leaves, so a caller cannot write through the handed-out list into the cache.
            return new ReadOnlyCollection<GltfMeshPart>(GltfLoader.LoadPartsWithMaterials(path).ToArray());
        }
        catch (Exception ex)
        {
            // Deliberately broad: a corrupt or unsupported glb, or a ref no path API will accept, surfaces as
            // anything from a format exception to an IO one, and none of them is worth taking the whole world
            // down for when a greybox box will do.
            return FallBack(archetype, path, ex.Message);
        }
    }

    IReadOnlyList<GltfMeshPart>? FallBack(TileObjectArchetype archetype, string path, string reason)
    {
        _log?.Invoke($"tile world: archetype '{archetype.Id}' could not load mesh '{path}' ({reason}), falling back.");
        return _fallback?.Resolve(archetype);
    }
}
