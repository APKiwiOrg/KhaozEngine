using System;
using System.Linq;
using System.Text.Json;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc;

/// <summary>A detached authored snapshot, independent of the monolithic storage envelope.</summary>
internal static class MapDocumentSnapshot
{
    internal static MapDocument Copy(MapDocument source, MapDocRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Tiles is { IsPartial: true })
            throw new MapDocumentException("Snapshots require a complete document, not a windowed document.");
        var errors = MapDocumentValidator.Validate(source, registry);
        if (errors.Count != 0)
            throw new MapDocumentException("refusing to copy an invalid map document:\n  " + string.Join("\n  ", errors));
        MapSurfaceSaveGuard.Whole(source.Surfaces);
        var patchDigests = source.Surfaces.Patches.ToDictionary(p => p.Key, p => MapSurfaceSemantics.PatchDigest(p.Value));
        var copy = DetachStructure(source, registry);
        for (int i = 0; i < copy.Surfaces.Refs.Count; i++)
        {
            var surface = copy.Surfaces.Refs[i];
            copy.Surfaces.Refs[i] = surface with
            { SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface, patchDigests.Where(p => p.Key.SurfaceId == surface.Id)) };
        }
        return copy;
    }

    /// <summary>Detaches mutable members after the caller's admission checks. Publication admits native relationships later.</summary>
    internal static MapDocument DetachStructure(MapDocument source, MapDocRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Tiles is { IsPartial: true })
            throw new MapDocumentException("Snapshots require a complete document, not a windowed document.");
        var copy = new MapDocument
        {
            Schema = source.Schema,
            FormatVersion = source.FormatVersion,
            Id = source.Id,
            DisplayName = source.DisplayName,
            Bounds = Bounds(source.Bounds),
            PlayableBounds = source.PlayableBounds is { } playable ? Bounds(playable) : null,
            TileSize = source.TileSize,
            Terrain = Terrain(source.Terrain, registry),
            ScatterLayers = source.ScatterLayers.Select(l => new MapScatterLayer
            {
                Name = l.Name,
                Seed = l.Seed,
                CellSize = Finite(l.CellSize),
                Jitter = Finite(l.Jitter),
                MaxHeight = Finite(l.MaxHeight),
                ScaleMin = Finite(l.ScaleMin),
                ScaleMax = Finite(l.ScaleMax),
                Rules = l.Rules.Select(r => new MapBiomeScatterRule
                { Biome = r.Biome, Density = Finite(r.Density), Kinds = r.Kinds.Select(Kind).ToList() }).ToList(),
            }).ToList(),
            CompanionLayers = source.CompanionLayers.Select(l => new MapCompanionLayer
            {
                Name = l.Name,
                HostLayer = l.HostLayer,
                Seed = l.Seed,
                HostKinds = l.HostKinds.ToList(),
                Kinds = l.Kinds.Select(Kind).ToList(),
                CountMin = l.CountMin,
                CountMax = l.CountMax,
                RadiusMin = Finite(l.RadiusMin),
                RadiusMax = Finite(l.RadiusMax),
                ScaleMin = Finite(l.ScaleMin),
                ScaleMax = Finite(l.ScaleMax),
                MaxHeight = Finite(l.MaxHeight),
            }).ToList(),
            Exclusions = source.Exclusions.Select(e => new MapExclusion
            { Name = e.Name, Shape = Shape(e.Shape), Layers = e.Layers?.ToList() }).ToList(),
            ScatterOverrides = source.ScatterOverrides.Select(o => new MapScatterOverrideDoc
            { Name = o.Name, Shape = Shape(o.Shape), Layers = o.Layers?.ToList(), DensityMultiplier = Finite(o.DensityMultiplier), Kinds = o.Kinds?.Select(Kind).ToList() }).ToList(),
            Placements = source.Placements.Select(Placement).ToList(),
            Spawns = source.Spawns.Select(s => new MapSpawn
            { Id = s.Id, ArchetypeId = s.ArchetypeId, X = Finite(s.X), Z = Finite(s.Z), Enabled = s.Enabled, Tags = s.Tags?.ToList() ?? [] }).ToList(),
            PlayerSpawns = source.PlayerSpawns.Select(s => new MapPlayerSpawn
            { Id = s.Id, X = Finite(s.X), Z = Finite(s.Z), Yaw = Finite(s.Yaw), Enabled = s.Enabled, Tags = s.Tags?.ToList() ?? [] }).ToList(),
            Regions = source.Regions.Select(r => new MapRegion
            { Name = r.Name, Shape = Shape(r.Shape), Tags = r.Tags?.ToList() ?? [] }).ToList(),
            NativeAssets = source.NativeAssets.ToList(),
            NumericIdHighWaterMark = source.NumericIdHighWaterMark,
            ResolverIdentity = source.ResolverIdentity,
            SupportRecipe = source.SupportRecipe,
            Surfaces = source.Surfaces.Clone(),
            Water = source.Water?.Clone(),
            Foliage = source.Foliage,
            Prefabs = source.Prefabs?.Clone(),
            BuildingIntent = source.BuildingIntent?.Clone(),
            NativeResourceTransforms = source.NativeResourceTransforms?.ToArray(),
        };
        if (source.TerrainOverrides is { } sculpt && !(sculpt.IsEmpty && sculpt.CellSize == MapTerrainOverrides.DefaultCellSize))
        {
            copy.TerrainOverrides = new(sculpt.CellSize);
            foreach (var tile in sculpt.Tiles)
                copy.TerrainOverrides.PutTile(new(tile.TileX, tile.TileZ, tile.Deltas.Select(value => Finite(value)).ToArray()));
        }
        return copy;
    }

    static float Finite(float value) => float.IsFinite(value) ? value
        : throw new MapDocumentException("Snapshot numeric values must be finite.");
    static float? Finite(float? value) => value is { } number ? Finite(number) : null;
    static MapPlacement Placement(MapPlacement p)
    {
        var copy = MapPrefabReferenceWalker.Copy(p, p.Id, p.SupportBinding);
        copy.Tags ??= [];
        copy.X = Finite(copy.X); copy.Y = Finite(copy.Y); copy.Z = Finite(copy.Z);
        copy.Yaw = Finite(copy.Yaw); copy.Scale = Finite(copy.Scale);
        return copy;
    }
    static MapBounds Bounds(MapBounds b) => new() { MinX = Finite(b.MinX), MinZ = Finite(b.MinZ), MaxX = Finite(b.MaxX), MaxZ = Finite(b.MaxZ) };
    static MapPropKind Kind(MapPropKind k) => new() { Id = k.Id, Weight = Finite(k.Weight) };
    static MapShapeDoc? Shape(MapShapeDoc? shape) => shape switch
    {
        null => null,
        DiscShapeDoc d => new DiscShapeDoc { CenterX = Finite(d.CenterX), CenterZ = Finite(d.CenterZ), Radius = Finite(d.Radius) },
        RectShapeDoc r => new RectShapeDoc { MinX = Finite(r.MinX), MinZ = Finite(r.MinZ), MaxX = Finite(r.MaxX), MaxZ = Finite(r.MaxZ) },
        PolygonShapeDoc p => new PolygonShapeDoc { Points = p.Points.Select(p => p.Select(v => Finite(v)).ToArray()).ToList() },
        _ => throw new MapDocumentException("Unknown map shape in snapshot."),
    };
    static MapTerrain Terrain(MapTerrain t, MapDocRegistry registry)
    {
        // Features are an open DTO set. Only these individual extension members use the registry codec.
        var options = MapDocumentFile.CreateOptions(registry, write: false);
        return new()
        {
            Seed = t.Seed,
            WaterLevel = Finite(t.WaterLevel),
            BiomeBlend = Finite(t.BiomeBlend),
            GentleFrequency = Finite(t.GentleFrequency),
            GentleAmplitude = Finite(t.GentleAmplitude),
            DetailFrequency = Finite(t.DetailFrequency),
            DetailOctaves = t.DetailOctaves,
            Biomes = t.Biomes.Select(b => new MapBiomeBand
            { Start = Finite(b.Start), End = Finite(b.End), Biome = b.Biome, BaseHeight = Finite(b.BaseHeight), HillAmplitude = Finite(b.HillAmplitude) }).ToList(),
            Features = t.Features.Select(f => JsonSerializer.SerializeToElement(f, options).Deserialize<MapFeature>(options)!).ToList(),
        };
    }
}
