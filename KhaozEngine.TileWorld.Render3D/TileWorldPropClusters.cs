using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;

namespace KhaozEngine.TileWorld;

/// <summary>Validates TileWorld prop-layer definitions, resolves their mesh data, publishes detached
/// region-plane snapshots, and routes their background builds and frame draws through the shared cluster owner.</summary>
public sealed partial class TileWorldPropClusters : IDisposable
{
    readonly ITileWorldScene _scene;
    readonly TileWorldCatalogs _catalogs;
    readonly LayerResources[] _layers;
    readonly Dictionary<string, int> _archetypeLayers;
    readonly Dictionary<(RegionCoord Region, int Plane), long> _generations = new();
    bool _disposed;

    internal TileWorldPropClusters(ITileWorldScene scene, TileWorldCatalogs catalogs, ITileMeshResolver resolver,
                                   IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> fullMeshes,
                                   ValidatedLayers validated, float tileSize,
                                   TileWorldBuildQueueOptions? buildQueueOptions,
                                   IChunkBuildDispatcher? dispatcher)
    {
        _scene = scene;
        _catalogs = catalogs;
        _archetypeLayers = validated.ArchetypeLayers;
        _layers = new LayerResources[validated.Layers.Length];
        ITileLodMeshResolver? lodResolver = resolver as ITileLodMeshResolver;
        try
        {
            for (int i = 0; i < validated.Layers.Length; i++)
                _layers[i] = Resolve(validated.Layers[i], lodResolver, fullMeshes);
            InitializeRendering(tileSize, buildQueueOptions, dispatcher);
        }
        catch
        {
            DisposeRendering();
            for (int i = 0; i < _layers.Length; i++) _layers[i]?.Dispose(_scene);
            throw;
        }
    }

    /// <summary>Whether any archetype has opted into a selected layer.</summary>
    public bool IsEnabled => _layers.Length > 0;

    internal static ValidatedLayers Validate(TileWorldCatalogs catalogs,
                                             IReadOnlyList<TilePropLayerDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        ArgumentNullException.ThrowIfNull(definitions);
        var layerIds = new HashSet<string>(StringComparer.Ordinal);
        var selected = new Dictionary<string, int>(StringComparer.Ordinal);
        var layers = new ValidatedLayer[definitions.Count];
        for (int i = 0; i < definitions.Count; i++)
        {
            TilePropLayerDefinition definition = definitions[i]
                ?? throw new ArgumentException("A prop layer definition cannot be null.", nameof(definitions));
            if (string.IsNullOrWhiteSpace(definition.Id))
                throw new ArgumentException("A prop layer id is required.", nameof(definitions));
            if (!layerIds.Add(definition.Id))
                throw new ArgumentException($"Prop layer id '{definition.Id}' is duplicated.", nameof(definitions));
            if (definition.ArchetypeIds is null || definition.ArchetypeIds.Count == 0)
                throw new ArgumentException($"Prop layer '{definition.Id}' must select at least one archetype.", nameof(definitions));
            PositiveFinite(definition.DrawRadius, nameof(definition.DrawRadius));
            NonNegativeFinite(definition.LodDistance, nameof(definition.LodDistance));
            NonNegativeFinite(definition.HlodDistance, nameof(definition.HlodDistance));
            NonNegativeFinite(definition.LodCrossfadeWidth, nameof(definition.LodCrossfadeWidth));
            NonNegativeFinite(definition.HlodCrossfadeWidth, nameof(definition.HlodCrossfadeWidth));
            NonNegativeFinite(definition.HlodWeldCell, nameof(definition.HlodWeldCell));
            if (definition.LodDistance > definition.DrawRadius ||
                definition.HlodDistance > definition.DrawRadius ||
                definition.HlodDistance > 0f && definition.LodDistance >= definition.HlodDistance)
                throw new ArgumentOutOfRangeException(nameof(definitions),
                    $"Prop layer '{definition.Id}' distances must satisfy LOD < HLOD <= draw radius.");
            var archetypeIds = new string[definition.ArchetypeIds.Count];
            int archetypeIndex = 0;
            foreach (string archetypeId in definition.ArchetypeIds)
            {
                if (string.IsNullOrWhiteSpace(archetypeId) || catalogs.Archetype(archetypeId) is null)
                    throw new ArgumentException($"Prop layer '{definition.Id}' names unknown archetype '{archetypeId}'.",
                        nameof(definitions));
                if (!selected.TryAdd(archetypeId, i))
                    throw new ArgumentException($"Archetype '{archetypeId}' is selected by more than one prop layer.",
                        nameof(definitions));
                archetypeIds[archetypeIndex++] = archetypeId;
            }
            layers[i] = new ValidatedLayer(definition, archetypeIds);
        }
        return new ValidatedLayers(layers, selected);
    }

    /// <summary>Builds one detached snapshot from the document's current presentation state.</summary>
    internal TileRegionProps Build(TileWorldDocument doc, RegionCoord region, int plane,
                                   Func<long, string?>? archetypeOverride)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        TileRegionProps raw = TileObjectProps.Build(doc, _catalogs, region, plane, archetypeOverride);
        var key = (region, plane);
        long generation = _generations.TryGetValue(key, out long old) ? old + 1 : 1;
        _generations[key] = generation;
        if (_layers.Length == 0)
            return raw with { Region = region, Plane = plane, Generation = generation };

        var selected = new List<(long Id, PropPlacement Placement)>[_layers.Length];
        for (int i = 0; i < selected.Length; i++) selected[i] = new();
        var ground = new List<(long Id, PropPlacement Placement)>();
        Split(raw.Ground, raw.GroundObjectIds, ground, selected);
        var roofs = new List<(long Id, PropPlacement Placement, TileRect Footprint)>();
        for (int i = 0; i < raw.Roofs.Count; i++)
        {
            long id = i < raw.RoofObjectIds.Count ? raw.RoofObjectIds[i] : 0;
            if (_archetypeLayers.TryGetValue(raw.Roofs[i].Id, out int layer) && id != 0)
            {
                selected[layer].Add((id, raw.Roofs[i]));
                continue;
            }
            TileRect footprint = i < raw.RoofFootprints.Count ? raw.RoofFootprints[i] : default;
            roofs.Add((id, raw.Roofs[i], footprint));
        }
        ground.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        roofs.Sort(static (a, b) => a.Id.CompareTo(b.Id));

        var snapshots = new Dictionary<string, TilePropLayerSnapshot>(StringComparer.Ordinal);
        for (int i = 0; i < _layers.Length; i++)
        {
            selected[i].Sort(static (a, b) => a.Id.CompareTo(b.Id));
            var ids = new long[selected[i].Count];
            var placements = new PropPlacement[selected[i].Count];
            for (int at = 0; at < selected[i].Count; at++)
            {
                ids[at] = selected[i][at].Id;
                placements[at] = selected[i][at].Placement;
            }
            IReadOnlyList<long> frozenIds = Array.AsReadOnly(ids);
            IReadOnlyList<PropPlacement> frozenPlacements = Array.AsReadOnly(placements);
            LayerResources resources = _layers[i];
            snapshots.Add(resources.Definition.Id, new TilePropLayerSnapshot
            {
                Id = resources.Definition.Id,
                ObjectIds = frozenIds,
                Placements = frozenPlacements,
                Layer = resources.CreateLayer(frozenPlacements),
            });
        }

        var ordinaryGround = new PropPlacement[ground.Count];
        var groundIds = new long[ground.Count];
        for (int i = 0; i < ground.Count; i++)
        {
            groundIds[i] = ground[i].Id;
            ordinaryGround[i] = ground[i].Placement;
        }
        var ordinaryRoofs = new PropPlacement[roofs.Count];
        var roofIds = new long[roofs.Count];
        var footprints = new TileRect[roofs.Count];
        for (int i = 0; i < roofs.Count; i++)
        {
            roofIds[i] = roofs[i].Id;
            ordinaryRoofs[i] = roofs[i].Placement;
            footprints[i] = roofs[i].Footprint;
        }

        return new TileRegionProps(Array.AsReadOnly(ordinaryGround), Array.AsReadOnly(ordinaryRoofs))
        {
            Region = region,
            Plane = plane,
            Generation = generation,
            GroundObjectIds = Array.AsReadOnly(groundIds),
            RoofObjectIds = Array.AsReadOnly(roofIds),
            RoofFootprints = Array.AsReadOnly(footprints),
            Layers = new ReadOnlyDictionary<string, TilePropLayerSnapshot>(snapshots),
        };
    }

    void Split(IReadOnlyList<PropPlacement> source, IReadOnlyList<long> ids,
               List<(long Id, PropPlacement Placement)> ordinary,
               List<(long Id, PropPlacement Placement)>[] selected)
    {
        for (int i = 0; i < source.Count; i++)
        {
            long id = i < ids.Count ? ids[i] : 0;
            if (_archetypeLayers.TryGetValue(source[i].Id, out int layer) && id != 0)
            {
                selected[layer].Add((id, source[i]));
                continue;
            }
            ordinary.Add((id, source[i]));
        }
    }

    LayerResources Resolve(ValidatedLayer validated, ITileLodMeshResolver? resolver,
                           IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> fullMeshes)
    {
        TilePropLayerDefinition definition = validated.Definition;
        var full = new Dictionary<string, IReadOnlyList<MeshHandle>>(StringComparer.Ordinal);
        var lod = new Dictionary<string, IReadOnlyList<MeshHandle>>(StringComparer.Ordinal);
        var flat = new Dictionary<string, GltfMesh>(StringComparer.Ordinal);
        bool allFlat = resolver is not null;
        var uploaded = new List<IReadOnlyList<MeshHandle>>();
        try
        {
            foreach (string id in validated.ArchetypeIds)
            {
                full.Add(id, fullMeshes[id]);
                TileObjectArchetype archetype = _catalogs.Archetype(id)!;
                if (resolver?.ResolveLod(archetype) is { Count: > 0 } lodParts)
                {
                    IReadOnlyList<MeshHandle> handles = _scene.LoadPropMeshes(lodParts);
                    lod.Add(id, handles);
                    uploaded.Add(handles);
                }
                GltfMesh? source = resolver?.ResolveFlatForHlod(archetype);
                if (source is null) allFlat = false;
                else flat.Add(id, source);
            }
        }
        catch
        {
            foreach (IReadOnlyList<MeshHandle> handles in uploaded) _scene.UnloadPropMeshes(handles);
            throw;
        }
        return new LayerResources(definition,
            new ReadOnlyDictionary<string, IReadOnlyList<MeshHandle>>(full),
            lod.Count == 0 ? null : new ReadOnlyDictionary<string, IReadOnlyList<MeshHandle>>(lod),
            allFlat ? new ReadOnlyDictionary<string, GltfMesh>(flat) : null,
            uploaded);
    }

    /// <summary>Frees optional LOD mesh parts uploaded for the definitions. Safe to call twice.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeRendering();
        foreach (LayerResources layer in _layers) layer.Dispose(_scene);
        _generations.Clear();
    }

    static void PositiveFinite(float value, string name)
    {
        if (!float.IsFinite(value) || value <= 0f) throw new ArgumentOutOfRangeException(name);
    }

    static void NonNegativeFinite(float value, string name)
    {
        if (!float.IsFinite(value) || value < 0f) throw new ArgumentOutOfRangeException(name);
    }

    internal sealed record ValidatedLayers(ValidatedLayer[] Layers, Dictionary<string, int> ArchetypeLayers);

    internal sealed record ValidatedLayer(TilePropLayerDefinition Definition, string[] ArchetypeIds);

    sealed class LayerResources
    {
        readonly IReadOnlyList<IReadOnlyList<MeshHandle>> _uploadedLod;
        public TilePropLayerDefinition Definition { get; }
        public IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> Full { get; }
        public IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>>? Lod { get; }
        public IReadOnlyDictionary<string, GltfMesh>? Flat { get; }

        public LayerResources(TilePropLayerDefinition definition,
                              IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> full,
                              IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>>? lod,
                              IReadOnlyDictionary<string, GltfMesh>? flat,
                              IReadOnlyList<IReadOnlyList<MeshHandle>> uploadedLod)
        {
            Definition = definition;
            Full = full;
            Lod = lod;
            Flat = flat;
            _uploadedLod = uploadedLod;
        }

        public PropLayer CreateLayer(IReadOnlyList<PropPlacement> placements)
        {
            PropLayer layer = PropLayer.PlacementLayer(placements, Full, Definition.DrawRadius,
                Definition.HlodCrossfadeWidth, Lod, Definition.LodDistance, colliders: false,
                castsShadows: Definition.CastsShadows).WithLodCrossfade(Definition.LodCrossfadeWidth);
            return Flat is null || Definition.HlodDistance <= 0f
                ? layer
                : layer.WithHlod(Flat, Definition.HlodDistance, Definition.HlodWeldCell,
                    Definition.HlodCrossfadeWidth);
        }

        public void Dispose(ITileWorldScene scene)
        {
            foreach (IReadOnlyList<MeshHandle> handles in _uploadedLod) scene.UnloadPropMeshes(handles);
        }
    }
}
