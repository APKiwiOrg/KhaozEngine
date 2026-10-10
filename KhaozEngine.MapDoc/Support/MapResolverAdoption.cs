using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace KhaozEngine.MapDoc.Support;

public sealed record MapPlacementSupportChoice(float? ExplicitY, MapSupportBinding? Binding);

/// <summary>Explicitly adopts every missing height into the authored resolver contract on a detached copy.</summary>
public static class MapResolverAdoption
{
    public static MapDocument ConvertToAuthoredSupport(MapDocument document, IReadOnlyDictionary<string, MapPlacementSupportChoice> choices)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(choices);
        if (document.Tiles is { IsPartial: true }) throw new MapDocumentException("Cannot adopt authored support from a partial window.");
        foreach (MapPlacement placement in document.Placements.Where(p => p.Y is null).OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            if (!choices.TryGetValue(placement.Id, out MapPlacementSupportChoice? choice) || choice is null ||
                (choice.ExplicitY is null) == (choice.Binding is null) ||
                (choice.ExplicitY is { } height && !float.IsFinite(height)))
                throw new MapDocumentException($"Placement '{placement.Id}' requires exactly one explicit Y or authored support choice.");
        }
        var options = MapDocumentFile.CreateOptions(MapDocRegistry.CreateDefault(), write: false);
        // Detach DTOs without routing a complete tiled world's resident patches through monolithic embedding limits.
        MapDocument snapshot = MapTiledFile.GlobalsOnly(document);
        snapshot.Surfaces = new();
        snapshot.Placements = document.Placements;
        snapshot.Spawns = document.Spawns;
        snapshot.PlayerSpawns = document.PlayerSpawns;
        snapshot.TerrainOverrides = document.TerrainOverrides;
        MapDocument copy = JsonSerializer.Deserialize<MapDocument>(JsonSerializer.Serialize(snapshot, options), options)!;
        copy.Surfaces = document.Surfaces.Clone();
        copy.ResolverIdentity = new(1, 2);
        copy.SupportRecipe = MapSupportRecipe.AuthoredBindingsV2;
        foreach (MapPlacement placement in copy.Placements.Where(p => p.Y is null))
        {
            MapPlacementSupportChoice choice = choices[placement.Id];
            placement.Y = choice.ExplicitY;
            placement.SupportBinding = choice.Binding is { } binding ? binding with { } : null;
        }
        return copy;
    }
}
