using System;
using System.Linq;
using KhaozEngine.TileWorld;

namespace KhaozEngine.TileEdit;

/// <summary>One weighted foliage archetype in an MCP layer object.</summary>
public sealed record FoliageArchetypeInfo(string Id, float Weight);

/// <summary>The detached MCP form of one cosmetic foliage layer. Density is row major. X advances within each
/// row and row index advances along positive world Z from <see cref="OriginZ"/>.</summary>
public sealed record FoliageLayerInfo(
    string Id,
    int Plane,
    float OriginX,
    float OriginZ,
    float CellSize,
    int Width,
    int Height,
    byte[] Density,
    int Seed,
    float Spacing,
    float ScaleMin,
    float ScaleMax,
    float RootOffset,
    FoliageArchetypeInfo[] Archetypes,
    int[] AllowedUnderlays,
    bool ExcludeIndoors,
    bool ExcludeSolidObjects,
    float DoorClearance,
    float EdgeFade)
{
    internal TileFoliageLayer ToLayer()
    {
        ArgumentNullException.ThrowIfNull(Density);
        ArgumentNullException.ThrowIfNull(Archetypes);
        ArgumentNullException.ThrowIfNull(AllowedUnderlays);
        var underlays = new ushort[AllowedUnderlays.Length];
        for (int i = 0; i < underlays.Length; i++)
        {
            int value = AllowedUnderlays[i];
            if ((uint)value > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(AllowedUnderlays), value,
                    $"allowed underlay ids must be 0..{ushort.MaxValue}.");
            underlays[i] = (ushort)value;
        }
        return new TileFoliageLayer(Id, Plane, OriginX, OriginZ, CellSize, Width, Height, Density, Seed,
            Spacing, ScaleMin, ScaleMax, RootOffset,
            Archetypes.Select(a => new TileFoliageArchetype(a.Id, a.Weight)), underlays,
            ExcludeIndoors, ExcludeSolidObjects, DoorClearance, EdgeFade);
    }

    internal static FoliageLayerInfo Of(TileFoliageLayer layer) => new(
        layer.Id, layer.Plane, layer.OriginX, layer.OriginZ, layer.CellSize, layer.Width, layer.Height,
        layer.CopyDensity(), layer.Seed, layer.Spacing, layer.ScaleMin, layer.ScaleMax, layer.RootOffset,
        layer.Archetypes.Select(a => new FoliageArchetypeInfo(a.Id, a.Weight)).ToArray(),
        layer.AllowedUnderlays.Select(id => (int)id).ToArray(), layer.ExcludeIndoors,
        layer.ExcludeSolidObjects, layer.DoorClearance, layer.EdgeFade);
}
