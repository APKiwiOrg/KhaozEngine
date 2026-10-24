using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Render3D;

public sealed class MapPlacementLayer
{
    public string Id { get; }
    public IReadOnlySet<string> AssetIds { get; }
    public IReadOnlyList<float> LodDistances { get; }
    public float LodCrossfadeWidth { get; }
    public float DrawRadius { get; }
    public float HlodDistance { get; }
    public float HlodCrossfadeWidth { get; }
    public float HlodWeldCell { get; }
    public bool CastsShadows { get; }
    public MapPlacementLayer(string id, IReadOnlySet<string> assetIds, IReadOnlyList<float> lodDistances,
        float lodCrossfadeWidth, float drawRadius, float hlodDistance, float hlodCrossfadeWidth, float hlodWeldCell, bool castsShadows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id); ArgumentNullException.ThrowIfNull(assetIds); ArgumentNullException.ThrowIfNull(lodDistances);
        if (assetIds.Any(string.IsNullOrWhiteSpace) || !Positive(drawRadius) || !Nonnegative(lodCrossfadeWidth) ||
            !Nonnegative(hlodDistance) || !Nonnegative(hlodCrossfadeWidth) || !Nonnegative(hlodWeldCell))
            throw new ArgumentException("Placement layer values must be finite with a positive draw radius.");
        float previous = 0;
        foreach (float distance in lodDistances)
        {
            if (!Positive(distance) || distance <= previous) throw new ArgumentException("LOD thresholds must be positive and strictly increasing.");
            previous = distance;
        }
        Id = id; AssetIds = assetIds.ToFrozenSet(StringComparer.Ordinal); LodDistances = Array.AsReadOnly(lodDistances.ToArray());
        LodCrossfadeWidth = lodCrossfadeWidth; DrawRadius = drawRadius; HlodDistance = hlodDistance;
        HlodCrossfadeWidth = hlodCrossfadeWidth; HlodWeldCell = hlodWeldCell; CastsShadows = castsShadows;
    }
    internal static bool Positive(float value) => float.IsFinite(value) && value > 0;
    static bool Nonnegative(float value) => float.IsFinite(value) && value >= 0;
}

public sealed class MapPlacementPolicy
{
    public IReadOnlyList<MapPlacementLayer> Layers { get; }
    public float DefaultDrawRadius { get; }
    internal string Digest { get; }
    public MapPlacementPolicy(IReadOnlyList<MapPlacementLayer> layers, float defaultDrawRadius)
    {
        ArgumentNullException.ThrowIfNull(layers);
        if (!MapPlacementLayer.Positive(defaultDrawRadius)) throw new ArgumentException("Default draw radius must be finite and positive.");
        var ids = new HashSet<string>(StringComparer.Ordinal); var assets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var layer in layers)
            if (layer is null || !ids.Add(layer.Id) || layer.AssetIds.Any(id => !assets.Add(id)))
                throw new ArgumentException("Placement layers require unique identities and disjoint asset selections.");
        Layers = Array.AsReadOnly(layers.ToArray()); DefaultDrawRadius = defaultDrawRadius;
        Digest = MapPlacementDigest.Hash(w =>
        {
            w.WriteStartArray(); w.WriteStringValue("kemap/placement-policy/1"); w.WriteNumberValue(defaultDrawRadius);
            foreach (var layer in Layers.OrderBy(l => l.Id, StringComparer.Ordinal))
            {
                w.WriteStartArray(); w.WriteStringValue(layer.Id);
                w.WriteStartArray(); foreach (string id in layer.AssetIds.Order(StringComparer.Ordinal)) w.WriteStringValue(id); w.WriteEndArray();
                w.WriteStartArray(); foreach (float distance in layer.LodDistances) w.WriteNumberValue(distance); w.WriteEndArray();
                foreach (float value in new[] { layer.LodCrossfadeWidth, layer.DrawRadius, layer.HlodDistance, layer.HlodCrossfadeWidth, layer.HlodWeldCell }) w.WriteNumberValue(value);
                w.WriteBooleanValue(layer.CastsShadows); w.WriteEndArray();
            }
            w.WriteEndArray();
        });
    }
    internal MapPlacementLayer? For(string assetId) => Layers.FirstOrDefault(l => l.AssetIds.Contains(assetId));
}

public enum MapPlacementRoute { Ordinary, Cluster, Animated }
public sealed record MapPlacementRoutingResult(MapSceneStatus Status, MapPlacementRouting? Routing);

/// <summary>The caller-derived immutable partition shared by rigid and animated preparation. Animated roofs are refused.</summary>
public sealed class MapPlacementRouting
{
    readonly MapScopedScene _scene;
    readonly MapPlacementPolicy _policy;
    readonly IReadOnlySet<string> _animated;
    public string SceneWitnessDigest { get; }
    public string PolicyDigest { get; }
    internal float DefaultDrawRadius { get; }
    public IReadOnlyDictionary<string, MapPlacementRoute> Entries { get; }
    MapPlacementRouting(MapScopedScene scene, MapPlacementPolicy policy, IReadOnlySet<string> animated, Dictionary<string, MapPlacementRoute> entries)
    {
        _scene = scene; _policy = policy; _animated = animated; SceneWitnessDigest = Witness(scene);
        DefaultDrawRadius = policy.DefaultDrawRadius;
        PolicyDigest = MapPlacementDigest.Hash(w =>
        {
            w.WriteStartArray(); w.WriteStringValue("kemap/placement-routing/1"); w.WriteStringValue(policy.Digest);
            foreach (string id in animated.Order(StringComparer.Ordinal)) w.WriteStringValue(id);
            w.WriteEndArray();
        });
        Entries = new ReadOnlyDictionary<string, MapPlacementRoute>(entries);
    }
    public static MapPlacementRoutingResult Create(MapScopedScene scene, MapPlacementPolicy policy, IReadOnlySet<string> animatedAssetIds)
    {
        ArgumentNullException.ThrowIfNull(scene); ArgumentNullException.ThrowIfNull(policy); ArgumentNullException.ThrowIfNull(animatedAssetIds);
        if (!scene.Witness.CompleteForScope || animatedAssetIds.Any(string.IsNullOrWhiteSpace)) return new(MapSceneStatus.Invalid, null);
        var animated = animatedAssetIds.ToFrozenSet(StringComparer.Ordinal);
        var clustered = policy.Layers.Where(l => l.HlodDistance > 0).SelectMany(l => l.AssetIds).ToFrozenSet(StringComparer.Ordinal);
        if (clustered.Overlaps(animated)) return new(MapSceneStatus.Invalid, null);
        foreach (var layer in policy.Layers)
            foreach (string assetId in layer.AssetIds)
            {
                if (!scene.Source.Data.Assets.Assets.Any(a => a.Id == assetId)) return new(MapSceneStatus.Invalid, null);
                var descriptor = scene.Source.Data.Assets.GetAsset(assetId);
                if (layer.LodDistances.Count > 0 && layer.LodDistances.Count != descriptor.LodResourceIds.Count)
                    return new(MapSceneStatus.Invalid, null);
            }
        var roofs = scene.CapturedBuildingIntent.Roofs.Select(r => r.Id).ToFrozenSet(StringComparer.Ordinal);
        var entries = new Dictionary<string, MapPlacementRoute>(StringComparer.Ordinal);
        foreach (var placement in scene.Placements)
        {
            string asset = placement.AssetId;
            var route = clustered.Contains(asset) ? MapPlacementRoute.Cluster : animated.Contains(asset) ? MapPlacementRoute.Animated : MapPlacementRoute.Ordinary;
            if (roofs.Contains(placement.PlacementId) && route == MapPlacementRoute.Animated) return new(MapSceneStatus.Invalid, null);
            entries.Add(placement.PlacementId, route);
        }
        return new(MapSceneStatus.CompleteForScope, new(scene, policy, animated, entries));
    }
    internal void Validate(MapScopedScene scene, MapPlacementPolicy? policy = null)
    {
        if (!ReferenceEquals(scene, _scene) || Witness(scene) != SceneWitnessDigest || policy is not null && policy.Digest != _policy.Digest)
            throw new MapDocumentException("Placement routing belongs to another scene or caller policy.");
    }
    internal MapPlacementLayer? ClusterLayer(string id) => _policy.Layers.FirstOrDefault(l => l.Id == id && l.HlodDistance > 0);
    internal IReadOnlyList<MapPlacementLayer> ClusterLayers => _policy.Layers.Where(l => l.HlodDistance > 0).ToArray();
    internal int ClusterLayerCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _policy.Layers.Count; i++) if (_policy.Layers[i].HlodDistance > 0) count++;
            return count;
        }
    }
    internal long ClusterAssetCount
    {
        get
        {
            long count = 0;
            for (int i = 0; i < _policy.Layers.Count; i++)
                if (_policy.Layers[i].HlodDistance > 0) count = checked(count + _policy.Layers[i].AssetIds.Count);
            return count;
        }
    }
    internal MapPlacementRouting ForScene(MapScopedScene scene) => Create(scene, _policy, _animated).Routing
        ?? throw new MapRenderAssetRefusal(MapRenderAssetStatus.Invalid, "Canonical cluster cell routing refused caller policy.");
    static string Witness(MapScopedScene scene) => MapPlacementDigest.Hash(w =>
    {
        var witness = scene.Witness;
        w.WriteStartArray(); w.WriteStringValue("kemap/placement-witness/1");
        w.WriteStringValue(witness.ClosureHash); w.WriteStringValue(witness.SourceGeneration); w.WriteStringValue(witness.ScopeDigest);
        w.WriteStringValue(witness.RenderExtentsDigest);
        foreach (var key in witness.Present.OrderBy(k => k.Kind, StringComparer.Ordinal).ThenBy(k => k.Id, StringComparer.Ordinal))
        { w.WriteStringValue(key.Kind); w.WriteStringValue(key.Id); }
        w.WriteEndArray();
    });
}

internal static class MapPlacementDigest
{
    internal static string Hash(Action<Utf8JsonWriter> write)
    {
        using var stream = new MapPreparationBuffer();
        using (var writer = new Utf8JsonWriter(stream, MapJsonTextOptions.IndentedWriter)) write(writer);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream.WrittenSpan));
    }
}
