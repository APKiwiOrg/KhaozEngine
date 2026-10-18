using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapEditor;

namespace KhaozEngine.MapEdit;

public sealed partial class MapEditSession
{
    MapMarkerConsumerOptions _markerOptions = MapMarkerConsumerOptions.None;

    /// <summary>The caller marker configuration staged by the last successful <see cref="Open(string, IReadOnlyList{string}, MapMarkerConsumerOptions)"/>.
    /// The released open overload stages <see cref="MapMarkerConsumerOptions.None"/>.</summary>
    internal MapMarkerConsumerOptions MarkerOptions { get { lock (_lock) return _markerOptions; } }

    /// <summary>Opens <paramref name="path"/> as the released overload does, with caller marker options staged atomically.
    /// When roles are given, a windowed native manifest anchors its window on the nearest enabled marker of the player
    /// start role, read from root metadata and prefab bytes verified under this storage root. The released complete
    /// native validation still runs, so a partial native result keeps its refusal and the session keeps its prior
    /// document, options and history. A whole native document reports its markers in <see cref="MapSummary.Markers"/>.</summary>
    public OpenResult Open(string path, IReadOnlyList<string>? manifestPaths, MapMarkerConsumerOptions markerOptions)
    {
        ArgumentNullException.ThrowIfNull(markerOptions);
        markerOptions.Require();
        return OpenWith(path, manifestPaths, markerOptions);
    }

    MapDocument LoadForOpen(string fullPath, MapDocumentLoadOptions options, MapMarkerConsumerOptions markerOptions,
        out MapTileRect? window)
    {
        if (markerOptions.Roles is not { } roles)
            return MapDocumentWindowing.Load(fullPath, options, WholeWorldTileLimit, EditorWindowRadius, out _, out window);
        return MapDocumentWindowing.Load(fullPath, options, WholeWorldTileLimit, EditorWindowRadius,
            MapDocumentWindowing.DefaultPlayerSpawnSearchTileLimit, out _, out window, roles.PlayerStartRole,
            manifest => MapMarkerAnchors.Read(manifest,
                MapAssetClosure.Load(manifest.NativeAssets, new MapStorageGuardedAssetSource(fullPath, MapDocumentForm.Tiled))));
    }

    // Markers of a complete resolver-2 validation, in the same summary as its native identity. Resolver (1, 1) documents
    // have no effective markers and report none.
    MapMarkerSummary? MarkerSummaryLocked(MapResolvedDocument? resolved)
    {
        if (resolved?.EffectiveResources is not { } resources) return null;
        var registry = new MapMarkerRegistry(MapEffectiveAnnotations.Markers(resources));
        string? role = _markerOptions.Roles?.PlayerStartRole;
        IReadOnlyList<string> Ids(bool enabledOnly) => role is null
            ? Array.Empty<string>()
            : Array.AsReadOnly(MapMarkerProjection.PlayerStarts(registry, role, enabledOnly).Select(v => v.Marker.Id).ToArray());
        return new MapMarkerSummary(registry.All.Count, registry.Enabled.Count, role, Ids(false), Ids(true));
    }
}
