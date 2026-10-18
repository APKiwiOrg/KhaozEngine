using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapEditor;

/// <summary>Native marker readouts for the GUI. The caller's <see cref="MapEditorOptions.MarkerOptions"/> and
/// <see cref="MapEditorOptions.MarkerAssets"/> bind one <see cref="NativeMarkerReadouts"/> to the open native document.
/// Spawn billboards, spawn picking and spawn focus read it through <see cref="SpawnBillboards"/>, and a whole-loaded
/// document starts its camera at the caller's player start. Without both options a native document has no readouts
/// and says so, and nothing falls back to the analytic terrain.</summary>
public partial class MapEditorScene
{
    EditorDocument? _markerDocument;
    NativeMarkerReadouts? _markerReadouts;
    string? _markerDiagnostic;
    MapMarkerAnchor? _windowAnchor;

    /// <summary>The retained navigation pivot. Exposed for tests.</summary>
    internal Vector3? NavigationPivot => _navigation.Pivot;

    /// <summary>The viewport world, built or not. Exposed for tests.</summary>
    internal ViewportWorld SceneViewport => _viewport;

    /// <summary>The readouts of the open document, bound on first use after an open or reload. Null for an analytic
    /// document, or for a native one whose readouts are unavailable (see <see cref="MarkerDiagnostic"/>).</summary>
    internal NativeMarkerReadouts? MarkerReadouts
    {
        get
        {
            if (!ReferenceEquals(_markerDocument, _document)) BindMarkerReadouts();
            return _markerReadouts;
        }
    }

    /// <summary>Why the open native document's readouts are unavailable, or null.</summary>
    internal string? MarkerDiagnostic => MarkerReadouts?.Diagnostic ?? _markerDiagnostic;

    /// <summary>The spawn markers the viewport draws. Spawn picking stands on the same frame.</summary>
    internal SpawnBillboardFrame SpawnFrame() => _viewport.IsBuilt
        ? _viewport.SpawnFrame(_visibility)
        : SpawnBillboards.Build(_document.Doc, _visibility, (x, z) => GroundHeight(_controller.Field ?? _viewport.Field, x, z),
            MarkerReadouts);

    void BindMarkerReadouts()
    {
        _markerDocument = _document;
        _markerReadouts = null;
        _markerDiagnostic = null;
        MapDocument doc = _document.Doc;
        if (doc.ResolverIdentity is null) return;
        if (_options.MarkerOptions is not { } options || _options.MarkerAssets is not { } assetsOf)
        {
            _markerDiagnostic = "Native marker readouts are unavailable: MapEditorOptions.MarkerOptions and MarkerAssets " +
                "must supply the caller's roles, legacy spawn Surface owner and verified asset closure.";
            return;
        }
        try
        {
            _markerReadouts = new NativeMarkerReadouts(doc, HostAssets(assetsOf, doc), options);
        }
        catch (Exception error) when (error is MapDocumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _markerDiagnostic = "Native marker readouts are unavailable: " + error.Message;
        }
    }

    // Edits, undo and redo publish a new accepted document, so the next readout resolves it again.
    void OnMarkerSourceChanged()
    {
        if (ReferenceEquals(_markerDocument, _document)) _markerReadouts?.Refresh();
    }

    /// <summary>Loads <see cref="MapEditorOptions.DocumentPath"/> under the windowing policy. With the caller's player
    /// start role and closure, a windowed native manifest centres on that role's start (<see cref="MapMarkerAnchors"/>),
    /// reported as <paramref name="anchor"/>.</summary>
    MapDocument LoadEditorDocument(MapDocRegistry registry, out MapTileRect? window, out MapMarkerAnchor? anchor)
    {
        var load = new MapDocumentLoadOptions { Registry = registry };
        anchor = null;
        if (_options.MarkerOptions?.Roles is not { } roles || _options.MarkerAssets is not { } assetsOf)
            return MapDocumentWindowing.Load(_options.DocumentPath, load, _options.WholeWorldTileLimit, EffectiveWindowRadius,
                _options.PlayerSpawnSearchTileLimit, out _, out window);
        return MapDocumentWindowing.Load(_options.DocumentPath, load, _options.WholeWorldTileLimit, EffectiveWindowRadius,
            _options.PlayerSpawnSearchTileLimit, out _, out window, roles.PlayerStartRole,
            manifest => MapMarkerAnchors.Read(manifest, HostAssets(assetsOf, manifest)), out anchor);
    }

    // The host's closure delegate runs outside the engine. Any failure it raises is reported as a refusal, so readouts
    // show unavailable and an open or reload refuses as a load failure rather than crashing a frame.
    static MapAssetClosure HostAssets(Func<MapDocument, MapAssetClosure> assetsOf, MapDocument document)
    {
        try
        {
            return assetsOf(document) ?? throw new MapDocumentException("MapEditorOptions.MarkerAssets returned no asset closure.");
        }
        catch (Exception error) when (error is not MapDocumentException)
        {
            throw new MapDocumentException("MapEditorOptions.MarkerAssets failed: " + error.Message, error);
        }
    }

    ViewportWorld CreateViewport(RenderDistanceProfile renderDistance) => new(_scene, _options.ManifestPaths)
    {
        ScatterLayerVisible = _visibility.GetLayer,
        RenderDistance = renderDistance,
        TexturedPropsEnabled = () => _options.TexturedProps,
        PropCategoryResolver = _options.ResolvePropCategory,
        PropKindVisible = PropKindVisible,
        SpawnReadouts = () => MarkerReadouts,
    };

    /// <summary>The camera start of an open document before the released offset. A windowed native open starts at its
    /// window anchor's XZ with Y 0, as the released windowed spawn start does. A whole-loaded native document starts at
    /// the effective XYZ of the enabled start of the caller's role nearest the bounds centre. Null keeps the released
    /// start.</summary>
    Vector3? MarkerCameraStart()
    {
        if (_window is not null)
            return _windowAnchor is { } anchor ? new Vector3(anchor.Position.X.ToSingle(), 0, anchor.Position.Z.ToSingle()) : null;
        if (_options.MarkerOptions?.Roles is not { } roles || MarkerReadouts is not { } readouts) return null;
        IReadOnlyList<MapPlayerStartView> starts = readouts.PlayerStarts(enabledOnly: true);
        MapMarkerAnchor[] anchors = starts.Select(s => new MapMarkerAnchor(s.Marker.Id,
            new MapExactXz(s.Marker.Position.X, s.Marker.Position.Z), s.Marker.Enabled, s.Marker.Role)).ToArray();
        if (MapMarkerAnchors.Select(anchors, roles.PlayerStartRole, BoundsCentre(_document.Doc.Bounds)) is not { } chosen) return null;
        MapExactPoint position = starts.First(s => string.Equals(s.Marker.Id, chosen.Id, StringComparison.Ordinal)).Marker.Position;
        return new Vector3(position.X.ToSingle(), position.Y.ToSingle(), position.Z.ToSingle());
    }

    // A spawn focus stands on the ground its billboard is drawn on. A native spawn with no Supported sample keeps the
    // camera where it is and says why in the status strip.
    bool TrySpawnFrame(EditorSelection selection, TerrainField? field, out Vector3 pivot, out float radius)
    {
        (pivot, radius) = (Vector3.Zero, 1f);
        Vector2? xz = selection.Kind == SelectionKind.Spawn
            ? Spawn(selection.Id) is { } spawn ? new Vector2(spawn.X, spawn.Z) : null
            : PlayerSpawn(selection.Id) is { } start ? new Vector2(start.X, start.Z) : null;
        if (xz is not { } at) return false;
        if (SpawnBillboards.FocusGround(_document.Doc, selection.Kind, selection.Id, (x, z) => GroundHeight(field, x, z),
                MarkerReadouts, out string? diagnostic) is not float ground)
        {
            if (diagnostic is not null) _statusText = diagnostic;
            return false;
        }
        pivot = new Vector3(at.X, ground + SpawnBillboards.FocusLift, at.Y);
        return FiniteFrame(pivot, radius);
    }

    static MapExactXz BoundsCentre(MapBounds bounds) => new(
        MapExactValue.FromSingle((bounds.MinX + bounds.MaxX) * 0.5f), MapExactValue.FromSingle((bounds.MinZ + bounds.MaxZ) * 0.5f));
}
