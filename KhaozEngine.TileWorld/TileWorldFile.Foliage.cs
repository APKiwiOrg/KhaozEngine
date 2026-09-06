using System;
using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.TileWorld;

public static partial class TileWorldFile
{
    internal static TileFoliageLayerDto FoliageToDto(TileFoliageLayer layer) => new()
    {
        Id = layer.Id,
        Plane = layer.Plane,
        OriginX = layer.OriginX,
        OriginZ = layer.OriginZ,
        CellSize = layer.CellSize,
        Width = layer.Width,
        Height = layer.Height,
        Density = layer.CopyDensity(),
        Seed = layer.Seed,
        Spacing = layer.Spacing,
        ScaleMin = layer.ScaleMin,
        ScaleMax = layer.ScaleMax,
        RootOffset = layer.RootOffset,
        Archetypes = layer.Archetypes.Select(x => new TileFoliageArchetypeDto { Id = x.Id, Weight = x.Weight }).ToList(),
        AllowedUnderlays = layer.AllowedUnderlays.Count == 0 ? null : layer.AllowedUnderlays.ToList(),
        ExcludeIndoors = layer.ExcludeIndoors,
        ExcludeSolidObjects = layer.ExcludeSolidObjects,
        DoorClearance = layer.DoorClearance,
        EdgeFade = layer.EdgeFade,
    };

    internal static TileFoliageLayer FoliageFromDto(TileFoliageLayerDto dto)
    {
        if (dto is null) throw TileFoliageLayer.Invalid("layer entry cannot be null");
        if (dto.Archetypes is null) throw TileFoliageLayer.Invalid($"layer '{dto.Id}' archetypes cannot be null");
        return new TileFoliageLayer(
            dto.Id, dto.Plane, dto.OriginX, dto.OriginZ, dto.CellSize, dto.Width, dto.Height, dto.Density,
            dto.Seed, dto.Spacing, dto.ScaleMin, dto.ScaleMax, dto.RootOffset,
            dto.Archetypes.Select(x => x is null
                ? throw TileFoliageLayer.Invalid($"layer '{dto.Id}' archetype entry cannot be null")
                : new TileFoliageArchetype(x.Id, x.Weight)), dto.AllowedUnderlays,
            dto.ExcludeIndoors, dto.ExcludeSolidObjects, dto.DoorClearance, dto.EdgeFade);
    }

    static void ValidateFoliageDtos(List<TileFoliageLayerDto>? rows, int planeCount, string path)
    {
        if (rows is null) return;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (TileFoliageLayerDto dto in rows)
            {
                TileFoliageLayer layer = FoliageFromDto(dto);
                if (!ids.Add(layer.Id)) throw TileFoliageLayer.Invalid($"layer '{layer.Id}' is listed twice");
                if ((uint)layer.Plane >= (uint)planeCount)
                    throw TileFoliageLayer.Invalid($"layer '{layer.Id}' uses plane {layer.Plane}, the world has planes 0 through {planeCount - 1}");
            }
        }
        catch (ArgumentException ex)
        {
            throw new TileWorldException($"{path}: {ex.Message}", ex);
        }
    }
}
