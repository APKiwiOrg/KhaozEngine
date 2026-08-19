using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;

namespace KhaozEngine.TileWorld;

/// <summary>The albedo layers one tile world's ground draws with, in slot order, plus the map from catalog
/// material id to slot. One layer per catalog material and one reserved layer LAST, filled with the magenta of
/// <see cref="TileGroundMesher.MissingMaterialColor"/>, so a material id nothing defines is visible rather than
/// silently borrowing a neighbour's look. Every layer is the same size, because the whole set uploads into ONE
/// texture array. Build one from the catalogs with <see cref="TileGroundMaterials.Build"/>, or hand-build one
/// here when the layers come from somewhere other than a catalog (a generated test set, a game's own atlas).</summary>
public sealed class TileGroundMaterialSet : ITileGroundSlotMap
{
    readonly Dictionary<ushort, int> _slots;
    readonly ReadOnlyCollection<TileGroundLayerImage> _layers;
    readonly ReadOnlyCollection<ushort> _materialIds;

    /// <summary>Builds a set from layers that are already decoded. <paramref name="layers"/> carries exactly one
    /// more entry than <paramref name="materialIds"/>: the trailing one is the reserved
    /// <see cref="MissingSlot"/> layer, which belongs to no material.</summary>
    /// <param name="width">Layer width in texels, shared by every layer.</param>
    /// <param name="height">Layer height in texels, shared by every layer.</param>
    /// <param name="materialIds">The catalog material of each leading slot, in slot order, no duplicates.</param>
    /// <param name="layers">One layer per material id, then the reserved missing-material layer.</param>
    public TileGroundMaterialSet(int width, int height, IReadOnlyList<ushort> materialIds,
                                 IReadOnlyList<TileGroundLayerImage> layers)
    {
        ArgumentNullException.ThrowIfNull(materialIds);
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (layers.Count != materialIds.Count + 1)
            throw new ArgumentException(
                $"a set of {materialIds.Count} materials needs {materialIds.Count + 1} layers (one each plus the " +
                $"reserved missing-material layer), got {layers.Count}.", nameof(layers));
        if (layers.Count > TileGroundMaterialConfig.MaxMaterials)
            throw new ArgumentException(
                $"a tile-ground material set holds at most {TileGroundMaterialConfig.MaxMaterials} layers, got {layers.Count}.",
                nameof(layers));

        int expected = width * height * 4;
        var copy = new TileGroundLayerImage[layers.Count];
        for (int slot = 0; slot < layers.Count; slot++)
        {
            TileGroundLayerImage layer = layers[slot];
            ArgumentNullException.ThrowIfNull(layer);
            // Caught here rather than at the upload, because the slot is what names the culprit: by the time the
            // renderer sees it, the layers are one flat array of bytes with no material left attached to them.
            if (layer.AlbedoRgba.Length != expected)
                throw new ArgumentException(
                    $"layer {slot} is {layer.AlbedoRgba.Length} bytes, expected {expected} for {width}x{height} RGBA8.",
                    nameof(layers));
            copy[slot] = layer;
        }

        _slots = new Dictionary<ushort, int>(materialIds.Count);
        var ids = new ushort[materialIds.Count];
        for (int slot = 0; slot < materialIds.Count; slot++)
        {
            ids[slot] = materialIds[slot];
            if (!_slots.TryAdd(materialIds[slot], slot))
                throw new ArgumentException(
                    $"material {materialIds[slot]} is in slot {_slots[materialIds[slot]]} already, so slot {slot} would shadow it.",
                    nameof(materialIds));
        }

        Width = width;
        Height = height;
        _layers = Array.AsReadOnly(copy);
        _materialIds = Array.AsReadOnly(ids);
    }

    /// <summary>Layer width in texels, shared by every layer.</summary>
    public int Width { get; }

    /// <summary>Layer height in texels, shared by every layer.</summary>
    public int Height { get; }

    /// <summary>Every layer in slot order, the reserved missing-material one last.</summary>
    public IReadOnlyList<TileGroundLayerImage> Layers => _layers;

    /// <summary>The catalog material of each leading slot, in slot order. One shorter than
    /// <see cref="Layers"/>, because the reserved slot belongs to no material.</summary>
    public IReadOnlyList<ushort> MaterialIds => _materialIds;

    /// <summary>The reserved slot, which is always the LAST layer of the set.</summary>
    public int MissingSlot => _layers.Count - 1;

    /// <summary>The slot holding this material, or <see cref="MissingSlot"/> when the set does not carry it.</summary>
    public int SlotOf(ushort materialId) => _slots.TryGetValue(materialId, out int slot) ? slot : MissingSlot;
}

/// <summary>Turns a tile world's ground catalog into the <see cref="TileGroundMaterialSet"/> its meshes are drawn
/// with: one layer per material in ascending id order, then the reserved magenta layer. A material with a texture
/// is decoded and takes a white tint (the texture IS the colour), one with no texture becomes a flat layer of its
/// catalog colour, so a colour-only world renders through the same pipeline as a textured one.</summary>
public static class TileGroundMaterials
{
    /// <summary>Texture repeats per world metre for a material that names none: a 2 m repeat, which at 1 unit =
    /// 1 metre puts two tiles inside one repeat.</summary>
    public const float DefaultTilesPerMetre = 0.5f;

    /// <summary>Builds the set for these catalogs. A material's <see cref="GroundMaterial.Texture"/> is resolved
    /// RELATIVE TO THE CATALOG FILE that declared it (an absolute path is taken as written) and decoded with
    /// <paramref name="load"/>, defaulting to <see cref="ImageRgba.Load"/>. Every textured material must decode
    /// to the size of the FIRST textured one, and every untextured material is filled flat at that size (1x1 when
    /// nothing is textured at all), because the whole set uploads into one texture array.</summary>
    /// <param name="catalogs">The ground catalog to slot, materials taken in ascending id order.</param>
    /// <param name="load">Decodes one resolved texture path, or null for <see cref="ImageRgba.Load"/>.</param>
    public static TileGroundMaterialSet Build(TileWorldCatalogs catalogs, Func<string, ImageRgba>? load = null)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        load ??= ImageRgba.Load;

        var ids = new List<ushort>(catalogs.Materials.Keys);
        ids.Sort();
        if (ids.Count + 1 > TileGroundMaterialConfig.MaxMaterials)
            throw new TileWorldException(
                $"this ground catalog has {ids.Count} materials, and one material set holds " +
                $"{TileGroundMaterialConfig.MaxMaterials - 1} of them plus the reserved missing-material slot " +
                $"(the ceiling is {TileGroundMaterialConfig.MaxMaterials} layers in one texture array). Splitting " +
                "a catalog across several sets is not built yet, so a world this big needs a smaller catalog.");

        // Decoded first, because the size of the first textured material is what every other layer is built to,
        // and an untextured material cannot be filled until that size is known.
        var decoded = new Dictionary<ushort, ImageRgba>();
        int width = 0, height = 0;
        ushort sizedBy = 0;
        foreach (ushort id in ids)
        {
            GroundMaterial material = catalogs.Materials[id];
            if (string.IsNullOrWhiteSpace(material.Texture)) continue;

            ImageRgba image = load(ResolvedTexture(catalogs, id, material));
            if (width == 0)
            {
                width = image.Width;
                height = image.Height;
                sizedBy = id;
            }
            else if (image.Width != width || image.Height != height)
            {
                throw new TileWorldException(
                    $"ground material {id} ('{material.Name}') decodes to {image.Width}x{image.Height}, but the set " +
                    $"is {width}x{height} (from material {sizedBy}, '{catalogs.Materials[sizedBy].Name}'). Every " +
                    "layer of one set is one slice of the same texture array, so resize the texture: the set never " +
                    "resamples one behind your back.");
            }
            decoded[id] = image;
        }

        // Nothing textured at all, so the flat fills only ever need one texel each.
        if (width == 0) { width = 1; height = 1; }

        var layers = new TileGroundLayerImage[ids.Count + 1];
        for (int slot = 0; slot < ids.Count; slot++)
        {
            GroundMaterial material = catalogs.Materials[ids[slot]];
            float tiles = material.TilesPerMetre ?? DefaultTilesPerMetre;
            layers[slot] = decoded.TryGetValue(ids[slot], out ImageRgba image)
                // White tint: the texture is the colour, and the catalog colour stays what the headless readers use.
                ? new TileGroundLayerImage { AlbedoRgba = image.Pixels, Tint = Color.White, TilesPerMetre = tiles }
                : Flat(TileColors.Parse(material), width, height, tiles);
        }
        layers[^1] = Flat(TileGroundMesher.MissingMaterialColor, width, height, DefaultTilesPerMetre);
        return new TileGroundMaterialSet(width, height, ids, layers);
    }

    // The path the material's texture decodes from. Absolute stays put, relative resolves against the catalog
    // FILE that declared this material, which is the same rule world.json uses for its catalog paths.
    static string ResolvedTexture(TileWorldCatalogs catalogs, ushort id, GroundMaterial material)
    {
        string texture = material.Texture!;
        if (Path.IsPathRooted(texture)) return texture;

        string? source = catalogs.MaterialSource(id);
        // A catalog built in memory has no directory to resolve against, and guessing one (the process working
        // directory, say) would find a different file on every machine.
        if (source is null)
            throw new TileWorldException(
                $"ground material {id} ('{material.Name}') carries the relative texture '{texture}', but its catalog " +
                "did not come from a file, so there is no directory to resolve it against. Load the catalog from a " +
                "file, or give the material an absolute texture path.");
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source) ?? string.Empty, texture));
    }

    // One layer filled with a single colour. Alpha is forced opaque: an authored #rrggbbaa would otherwise make
    // the ground translucent, and the ground is the thing everything else is drawn against.
    static TileGroundLayerImage Flat(Vector4 color, int width, int height, float tilesPerMetre)
    {
        var pixels = new byte[width * height * 4];
        byte r = Channel(color.X), g = Channel(color.Y), b = Channel(color.Z);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = 0xff;
        }
        // White tint over a filled image rather than a tinted grey, so the layer reads the same way a textured
        // one does and the params UBO carries nothing per-material but the tiling.
        return new TileGroundLayerImage { AlbedoRgba = pixels, Tint = Color.White, TilesPerMetre = tilesPerMetre };
    }

    static byte Channel(float value) => (byte)Math.Clamp(MathF.Round(value * 255f), 0f, 255f);
}
