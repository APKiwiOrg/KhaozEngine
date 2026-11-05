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

    internal static TileFoliageLayer FoliageFromDto(TileFoliageLayerDto dto, int? layerIndex = null)
    {
        if (dto is null)
            throw new TileFoliageArgumentException(TileWorldRefusal.FoliageLayerNull,
                TileFoliageLayer.InvalidMessage("layer entry cannot be null"), null) { LayerIndex = layerIndex };
        if (dto.Archetypes is null)
            throw new TileFoliageArgumentException(TileWorldRefusal.FoliageArchetypesNull,
                TileFoliageLayer.InvalidMessage($"layer '{dto.Id}' archetypes cannot be null"), null) { LayerId = dto.Id };
        return new TileFoliageLayer(
            dto.Id, dto.Plane, dto.OriginX, dto.OriginZ, dto.CellSize, dto.Width, dto.Height, dto.Density,
            dto.Seed, dto.Spacing, dto.ScaleMin, dto.ScaleMax, dto.RootOffset,
            dto.Archetypes.Select((x, index) => x is null
                ? throw new TileFoliageArgumentException(TileWorldRefusal.FoliageArchetypeNull,
                    TileFoliageLayer.InvalidMessage($"layer '{dto.Id}' archetype entry cannot be null"), null)
                    { LayerId = dto.Id, ArchetypeIndex = index }
                : new TileFoliageArchetype(x.Id, x.Weight)), dto.AllowedUnderlays,
            dto.ExcludeIndoors, dto.ExcludeSolidObjects, dto.DoorClearance, dto.EdgeFade);
    }

    static void ValidateFoliageDtos(List<TileFoliageLayerDto>? rows, int planeCount, string path)
    {
        if (rows is null) return;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < rows.Count; index++)
        {
            TileFoliageLayerDto dto = rows[index];
            try
            {
                TileFoliageLayer layer = FoliageFromDto(dto, index);
                if (!ids.Add(layer.Id))
                    throw new TileFoliageArgumentException(TileWorldRefusal.FoliageLayerDuplicate,
                        TileFoliageLayer.InvalidMessage($"layer '{layer.Id}' is listed twice"), null) { LayerId = layer.Id };
                if ((uint)layer.Plane >= (uint)planeCount)
                    throw new TileFoliageArgumentException(TileWorldRefusal.FoliagePlaneOutsideWorld,
                        TileFoliageLayer.InvalidMessage($"layer '{layer.Id}' uses plane {layer.Plane}, the world has planes 0 through {planeCount - 1}"), null)
                    { LayerId = layer.Id, Plane = layer.Plane, PlaneCount = planeCount };
            }
            catch (TileFoliageArgumentException ex)
            {
                // The sample-limit guard precedes density length, so ExpectedCount fits the existing int property.
                throw new TileWorldException(ex.Reason, $"{path}: {ex.Message}", ex)
                {
                    SourceName = path, LayerId = ex.LayerId, LayerIndex = ex.LayerIndex,
                    Plane = ex.Plane, PlaneCount = ex.PlaneCount, OriginX = ex.OriginX, OriginZ = ex.OriginZ,
                    CellSize = ex.CellSize, Width = ex.Width, Height = ex.Height,
                    SampleCount = ex.SampleCount, MaximumSampleCount = ex.MaximumSampleCount,
                    ActualCount = ex.ActualCount, ExpectedCount = (int?)ex.ExpectedCount,
                    Spacing = ex.Spacing, ScaleMin = ex.ScaleMin, ScaleMax = ex.ScaleMax,
                    RootOffset = ex.RootOffset, DoorClearance = ex.DoorClearance, EdgeFade = ex.EdgeFade,
                    ArchetypeCount = ex.ArchetypeCount, ArchetypeId = ex.ArchetypeId,
                    ArchetypeIndex = ex.ArchetypeIndex, Weight = ex.Weight, TotalWeight = ex.TotalWeight,
                    MaterialId = ex.MaterialId,
                };
            }
            catch (ArgumentNullException ex) when (ex.ParamName == "density")
            {
                throw new TileWorldException(TileWorldRefusal.FoliageDensityNull, $"{path}: {ex.Message}", ex)
                { SourceName = path, LayerId = dto.Id, Field = "density" };
            }
        }
    }
}
