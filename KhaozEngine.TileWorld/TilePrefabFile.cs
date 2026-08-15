using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace KhaozEngine.TileWorld;

/// <summary>Prefab JSON: dense arrays as base64 like region files, indented for git.</summary>
public static class TilePrefabFile
{
    sealed class PlaneDto
    {
        public string? HeightsRelative { get; set; }
        public string? Underlay { get; set; }
        public string? Overlay { get; set; }
        public string? OverlayShape { get; set; }
        public string? OverlayRotation { get; set; }
        public string? Settings { get; set; }
    }

    sealed class PrefabDto
    {
        public string Name { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public int PlaneCount { get; set; }
        // Nullable on purpose: a hand-written prefab may omit an array or spell it "null", and that must land as
        // an empty list rather than a null field on a non-nullable property that blows up two calls later.
        public List<PlaneDto?>? Planes { get; set; }
        public List<TilePrefabObject>? Objects { get; set; }
        public List<TilePrefabMarker>? Markers { get; set; }
    }

    /// <summary>Writes the prefab to <paramref name="path"/>, creating the directory and replacing atomically.</summary>
    public static void Save(TilePrefab prefab, string path)
    {
        ArgumentNullException.ThrowIfNull(prefab);
        var dto = new PrefabDto
        {
            Name = prefab.Name, Width = prefab.Width, Height = prefab.Height, PlaneCount = prefab.PlaneCount,
            Objects = prefab.Objects, Markers = prefab.Markers,
            Planes = prefab.Planes.Select(p => p is null ? null : new PlaneDto
            {
                HeightsRelative = p.HeightsRelative is null ? null : TileLayerCodec.Encode(p.HeightsRelative),
                Underlay = p.Underlay is null ? null : TileLayerCodec.Encode(p.Underlay),
                Overlay = p.Overlay is null ? null : TileLayerCodec.Encode(p.Overlay),
                OverlayShape = p.OverlayShape is null ? null : TileLayerCodec.Encode(p.OverlayShape),
                OverlayRotation = p.OverlayRotation is null ? null : TileLayerCodec.Encode(p.OverlayRotation),
                Settings = p.Settings is null ? null : TileLayerCodec.Encode(p.Settings),
            }).ToList(),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(dto, TileWorldJson.Manifest));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Reads a prefab back, throwing a <see cref="TileWorldException"/> naming the file on any
    /// malformed or inconsistent content.</summary>
    public static TilePrefab Load(string path)
    {
        PrefabDto dto;
        try { dto = JsonSerializer.Deserialize<PrefabDto>(File.ReadAllBytes(path), TileWorldJson.Manifest) ?? throw new TileWorldException($"{path}: empty prefab"); }
        catch (JsonException ex) { throw new TileWorldException($"{path}: {ex.Message}", ex); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new TileWorldException($"{path}: cannot read prefab. {ex.Message}", ex); }
        List<PlaneDto?> planes = dto.Planes ?? new();
        if (dto.Width < 1 || dto.Height < 1 || dto.PlaneCount < 1 || planes.Count != dto.PlaneCount)
            throw new TileWorldException($"{path}: prefab dimensions are inconsistent");
        int tiles = dto.Width * dto.Height, corners = (dto.Width + 1) * (dto.Height + 1);
        var prefab = new TilePrefab { Name = dto.Name, Width = dto.Width, Height = dto.Height, PlaneCount = dto.PlaneCount, Objects = dto.Objects ?? new(), Markers = dto.Markers ?? new() };
        foreach (TilePrefabObject o in prefab.Objects)
            if (!InFootprint(prefab, o.X, o.Z))
                throw new TileWorldException($"{path}: object '{o.ArchetypeId}' at ({o.X}, {o.Z}) is outside the {prefab.Width}x{prefab.Height} prefab");
        foreach (TilePrefabMarker m in prefab.Markers)
            if (!InFootprint(prefab, m.X, m.Z))
                throw new TileWorldException($"{path}: marker '{m.Name}' at ({m.X}, {m.Z}) is outside the {prefab.Width}x{prefab.Height} prefab");
        for (int i = 0; i < planes.Count; i++)
        {
            PlaneDto? p = planes[i];
            string where = $"{path} plane {i}";
            prefab.Planes.Add(p is null ? null : new TilePrefabPlane
            {
                HeightsRelative = p.HeightsRelative is null ? null : TileLayerCodec.DecodeShorts(p.HeightsRelative, corners, where + " heights"),
                Underlay = p.Underlay is null ? null : TileLayerCodec.DecodeUShorts(p.Underlay, tiles, where + " underlay"),
                Overlay = p.Overlay is null ? null : TileLayerCodec.DecodeUShorts(p.Overlay, tiles, where + " overlay"),
                OverlayShape = p.OverlayShape is null ? null : TileLayerCodec.DecodeBytes(p.OverlayShape, tiles, where + " overlayShape"),
                OverlayRotation = p.OverlayRotation is null ? null : TileLayerCodec.DecodeBytes(p.OverlayRotation, tiles, where + " overlayRotation"),
                Settings = p.Settings is null ? null : TileLayerCodec.DecodeBytes(p.Settings, tiles, where + " settings"),
            });
        }
        return prefab;
    }

    static bool InFootprint(TilePrefab p, int x, int z) => x >= 0 && x < p.Width && z >= 0 && z < p.Height;
}
