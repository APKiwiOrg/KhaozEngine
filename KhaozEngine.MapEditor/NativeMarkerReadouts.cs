using System;
using System.Collections.Generic;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapEditor;

/// <summary>R6-P7 and R6-P13 for the editor GUI. Marker and legacy spawn readouts of one native document, read from
/// authored support and never from the analytic terrain field. It owns one immutable effective snapshot of the
/// accepted whole document, built on first use and dropped by <see cref="Refresh"/> after an edit, undo or redo, so
/// every billboard of a frame reads the same snapshot. A partial (windowed) document holds no snapshot and reports
/// <see cref="Diagnostic"/>. The caller supplies the verified closure, the roles and the Surface owner.</summary>
internal sealed class NativeMarkerReadouts
{
    sealed record Snapshot(MapEffectiveResources Resources, MapMarkerRegistry Registry)
    {
        internal Dictionary<(float X, float Z), MapSupportResult> Samples { get; } = new();
    }

    readonly MapDocument _document;
    readonly MapAssetClosure _assets;
    readonly MapMarkerConsumerOptions _options;
    readonly bool _partial;
    Snapshot? _snapshot;
    string? _failure;
    // The one unavailable answer of the current snapshot state, so a frame of unplaceable rows builds it once.
    MapSupportResult? _unavailable;

    internal NativeMarkerReadouts(MapDocument document, MapAssetClosure assets, MapMarkerConsumerOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(options);
        options.Require();
        _document = document;
        _assets = assets;
        _options = options;
        _partial = document.Tiles is { IsPartial: true };
    }

    /// <summary>Why readouts are unavailable, or null when the current snapshot resolved. A windowed document never
    /// resolves, because effective resolution needs every tile.</summary>
    internal string? Diagnostic
    {
        get
        {
            if (_partial)
                return "Native marker readouts are unavailable on a windowed document. Effective resolution needs every " +
                    "tile, so open it whole (raise WholeWorldTileLimit) to read markers and legacy spawn heights.";
            Current();
            return _failure;
        }
    }

    /// <summary>The caller's player start role views of the snapshot, in ordinal ID order. Empty with no roles or no
    /// snapshot.</summary>
    internal IReadOnlyList<MapPlayerStartView> PlayerStarts(bool enabledOnly) =>
        _options.Roles is { } roles && Current() is { } snapshot
            ? MapMarkerProjection.PlayerStarts(snapshot.Registry, roles.PlayerStartRole, enabledOnly)
            : Array.Empty<MapPlayerStartView>();

    /// <summary>The caller's legacy spawn Surface owner sampled at (x, z) through the shared seat query. Every status is
    /// typed and only Supported carries a height. With no Surface owner or no snapshot the result is Invalid with a
    /// detail saying why. Samples and that unavailable answer are kept per snapshot, so a frame never repeats a query
    /// or rebuilds a result.</summary>
    internal MapSupportResult LegacySpawnHeight(float x, float z)
    {
        Snapshot? snapshot = Current();
        if (_options.LegacySpawnSurfaceId is not { } surfaceId)
            return _unavailable ??= Unavailable(snapshot, "no legacy spawn Surface owner is configured in MapMarkerConsumerOptions");
        if (snapshot is null) return _unavailable ??= Unavailable(null, Diagnostic ?? "no effective snapshot");
        if (!snapshot.Samples.TryGetValue((x, z), out MapSupportResult? support))
        {
            support = MapNativePointReadout.SurfaceAt(snapshot.Resources,
                new MapExactXz(MapExactValue.FromSingle(x), MapExactValue.FromSingle(z)), surfaceId);
            snapshot.Samples.Add((x, z), support);
        }
        return support;
    }

    /// <summary>Drops the snapshot after the document changed. The next readout resolves the accepted document again.</summary>
    internal void Refresh()
    {
        _snapshot = null;
        _failure = null;
        _unavailable = null;
    }

    Snapshot? Current()
    {
        if (_snapshot is not null || _partial || _failure is not null) return _snapshot;
        try
        {
            // A native transaction remembers the expansion it published, so the accepted document is not expanded twice.
            MapEffectiveResources resources = NativeAcceptedExpansions.TryBefore(_document, _assets, out Lazy<MapEffectiveResources>? accepted)
                ? accepted.Value
                : MapPrefabResolver.Expand(_document, _assets);
            _snapshot = new(resources, new MapMarkerRegistry(MapEffectiveAnnotations.Markers(resources)));
        }
        catch (Exception error) when (error is MapDocumentException or InvalidOperationException or ArgumentException)
        {
            _failure = "Native marker readouts are unavailable: " + error.Message;
        }
        return _snapshot;
    }

    static MapSupportResult Unavailable(Snapshot? snapshot, string detail) =>
        new(MapSupportStatus.Invalid, null, 0, null, null, null, null, detail,
            new MapReadWitness(snapshot?.Resources.Digest ?? "", "", default, MapScopedSurfaces.EffectiveQueryPolicyVersion, complete: false));
}
