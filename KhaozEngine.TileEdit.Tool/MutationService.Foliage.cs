using System;
using System.Collections.Generic;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Editing;

namespace KhaozEngine.TileEdit;

/// <summary>The cosmetic foliage mutation verbs. Every call replaces one immutable layer through one command.</summary>
public sealed partial class MutationService
{
    /// <summary>Adds or replaces one complete validated foliage layer.</summary>
    public MutationResult FoliageLayerSet(FoliageLayerInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        TileFoliageLayer layer = info.ToLayer();
        return session.Execute(e =>
        {
            ValidateCatalogs(e.Catalogs, layer);
            return new SetFoliageLayerCommand(e.Document, layer);
        });
    }

    /// <summary>Replaces the density raster. Row 0 starts at originZ and later rows advance in positive world Z.</summary>
    public MutationResult FoliageDensitySet(string id, int width, int height, IReadOnlyList<int[]> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return session.Execute(e =>
        {
            TileFoliageLayer layer = RequireLayer(e.Document, id);
            if (width != layer.Width || height != layer.Height)
                throw new ArgumentException(
                    $"density dimensions {width} by {height} do not match layer '{id}' dimensions {layer.Width} by {layer.Height}.",
                    nameof(width));
            if (rows.Count != height)
                throw new ArgumentException($"density needs {height} rows, {rows.Count} were given.", nameof(rows));
            var density = new byte[checked(width * height)];
            for (int z = 0; z < height; z++)
            {
                int[] row = rows[z] ?? throw new ArgumentException($"density row {z} is null.", nameof(rows));
                if (row.Length != width)
                    throw new ArgumentException($"density row {z} needs {width} values, {row.Length} were given.", nameof(rows));
                for (int x = 0; x < width; x++)
                {
                    int value = row[x];
                    if ((uint)value > byte.MaxValue)
                        throw new ArgumentOutOfRangeException(nameof(rows), value,
                            $"density at row {z}, column {x} must be 0..255.");
                    density[(z * width) + x] = (byte)value;
                }
            }
            return new SetFoliageLayerCommand(e.Document, layer.WithDensity(density));
        });
    }

    /// <summary>Paints a circular world-space brush into one density raster.</summary>
    public MutationResult FoliagePaint(string id, float worldX, float worldZ, float radius, int density,
        float hardness)
    {
        if (!float.IsFinite(worldX) || !float.IsFinite(worldZ))
            throw new ArgumentException("brush position must be finite.");
        if (!float.IsFinite(radius) || radius <= 0f)
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "radius must be finite and positive.");
        if ((uint)density > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(density), density, "density must be 0..255.");
        if (!float.IsFinite(hardness) || hardness < 0f || hardness > 1f)
            throw new ArgumentOutOfRangeException(nameof(hardness), hardness, "hardness must be 0..1.");

        return session.Execute(e =>
        {
            TileFoliageLayer layer = RequireLayer(e.Document, id);
            byte[] values = layer.CopyDensity();
            float hardRadius = radius * hardness;
            for (int z = 0; z < layer.Height; z++)
            {
                float sampleZ = layer.OriginZ + (z * layer.CellSize);
                for (int x = 0; x < layer.Width; x++)
                {
                    float sampleX = layer.OriginX + (x * layer.CellSize);
                    float distance = MathF.Sqrt(((sampleX - worldX) * (sampleX - worldX)) +
                        ((sampleZ - worldZ) * (sampleZ - worldZ)));
                    if (distance > radius) continue;
                    float strength = distance <= hardRadius || hardness == 1f
                        ? 1f
                        : (radius - distance) / (radius - hardRadius);
                    int index = (z * layer.Width) + x;
                    values[index] = (byte)Math.Clamp((int)MathF.Round(
                        values[index] + ((density - values[index]) * strength)), 0, byte.MaxValue);
                }
            }
            return new SetFoliageLayerCommand(e.Document, layer.WithDensity(values));
        });
    }

    /// <summary>Removes one foliage layer.</summary>
    public MutationResult FoliageRemove(string id) =>
        session.Execute(e => new RemoveFoliageLayerCommand(e.Document, id));

    static TileFoliageLayer RequireLayer(TileWorldDocument doc, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return doc.GetFoliageLayer(id) ?? throw new TileWorldException($"foliage layer '{id}' does not exist");
    }

    static void ValidateCatalogs(TileWorldCatalogs catalogs, TileFoliageLayer layer)
    {
        foreach (TileFoliageArchetype archetype in layer.Archetypes)
            if (catalogs.Archetype(archetype.Id) is null)
                throw new TileWorldException(
                    $"foliage layer '{layer.Id}' references archetype '{archetype.Id}', which is not in the catalog");
        foreach (ushort underlay in layer.AllowedUnderlays)
            if (catalogs.Material(underlay) is null)
                throw new TileWorldException(
                    $"foliage layer '{layer.Id}' allows underlay {underlay}, which is not in the catalog");
    }
}
