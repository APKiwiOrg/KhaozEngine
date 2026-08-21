using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Render3D;

namespace KhaozEngine.TileWorld;

/// <summary>A resolver backed by real content: it maps an archetype's <see cref="TileObjectArchetype.MeshRef"/>
/// to a glb under a kit root and loads it through <see cref="GltfLoader.LoadPartsWithMaterials"/>, so a tile
/// world draws its authored kit instead of <see cref="GreyboxMeshResolver"/>'s boxes. A mesh reference is
/// authored with forward slashes and relative to the root (<c>kit/wall.glb</c>), normalized to the platform
/// separator here, and an already-absolute reference is used as it stands.
///
/// <para>Content outlives a kit edit, so a missing file or a loader throw is NOT fatal: it logs ONE line naming
/// the archetype, the resolved path and the reason, then answers with the fallback resolver's mesh. Pass a
/// <see cref="GreyboxMeshResolver"/> as the fallback and a half-authored kit renders as boxes where the glb is
/// missing and as the real mesh everywhere else. With no fallback the answer is null, which the view draws as its
/// placeholder box plus a line of its own. An archetype with an EMPTY mesh reference is not a failure at all (it
/// is simply not authored yet), so it goes straight to the fallback with no log line and without touching the
/// disk.</para>
///
/// <para>Every result is cached per archetype id, failures included, so a later call for a failed id hands back
/// the same answer without re-logging or probing the disk again. The cache is a plain dictionary, so resolve on
/// one thread, exactly as <see cref="GreyboxMeshResolver"/> documents.</para>
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
    /// fallback's answer (null when there is no fallback) when the glb is missing or unreadable.</summary>
    public IReadOnlyList<GltfMeshPart>? Resolve(TileObjectArchetype archetype)
    {
        ArgumentNullException.ThrowIfNull(archetype);
        if (_cache.TryGetValue(archetype.Id, out IReadOnlyList<GltfMeshPart>? cached)) return cached;

        IReadOnlyList<GltfMeshPart>? parts = LoadOnce(archetype);
        _cache[archetype.Id] = parts;
        return parts;
    }

    /// <summary>The absolute path an archetype's mesh reference names under this resolver's root. An absolute
    /// reference is returned as it stands, and forward slashes become the platform separator either way.</summary>
    public string PathFor(string meshRef)
    {
        ArgumentNullException.ThrowIfNull(meshRef);
        // Path.Combine hands back an already-rooted second argument untouched, so this covers both forms.
        return Path.GetFullPath(Path.Combine(_rootDirectory, meshRef.Replace('/', Path.DirectorySeparatorChar)));
    }

    IReadOnlyList<GltfMeshPart>? LoadOnce(TileObjectArchetype archetype)
    {
        if (string.IsNullOrWhiteSpace(archetype.MeshRef)) return _fallback?.Resolve(archetype);

        string path = PathFor(archetype.MeshRef);
        // Probed rather than caught, because a missing kit piece is the ordinary half-authored case and deserves
        // a reason a reader can act on, not whatever wording the loader happens to throw for an absent file.
        if (!File.Exists(path)) return FallBack(archetype, path, "file not found");

        try
        {
            return GltfLoader.LoadPartsWithMaterials(path);
        }
        catch (Exception ex)
        {
            // Deliberately broad: a corrupt or unsupported glb surfaces as anything from a format exception to an
            // IO one, and none of them is worth taking the whole world down for when a greybox box will do.
            return FallBack(archetype, path, ex.Message);
        }
    }

    IReadOnlyList<GltfMeshPart>? FallBack(TileObjectArchetype archetype, string path, string reason)
    {
        _log?.Invoke($"tile world: archetype '{archetype.Id}' could not load mesh '{path}' ({reason}), falling back.");
        return _fallback?.Resolve(archetype);
    }
}
