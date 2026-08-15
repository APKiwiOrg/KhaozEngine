using System;
using System.Linq;
using System.Text.Json;

namespace KhaozEngine.TileWorld;

/// <summary>Region file bytes: canonical write (compact, sorted content) and checked parse.</summary>
internal static class TileRegionFile
{
    public static byte[] WriteCanonical(TileRegion region)
    {
        var dto = new TileRegionFileDto { Rx = region.Coord.Rx, Rz = region.Coord.Rz };
        region.Trim();
        foreach (TilePlaneData p in region.Planes)
        {
            dto.Planes.Add(p.IsEmpty ? null : new TileRegionPlaneDto
            {
                Heights = p.Heights is null ? null : TileLayerCodec.Encode(p.Heights),
                Underlay = p.Underlay is null ? null : TileLayerCodec.Encode(p.Underlay),
                Overlay = p.Overlay is null ? null : TileLayerCodec.Encode(p.Overlay),
                OverlayShape = p.OverlayShape is null ? null : TileLayerCodec.Encode(p.OverlayShape),
                OverlayRotation = p.OverlayRotation is null ? null : TileLayerCodec.Encode(p.OverlayRotation),
                Settings = p.Settings is null ? null : TileLayerCodec.Encode(p.Settings),
            });
        }
        dto.Objects = region.Objects
            .OrderBy(o => o.Plane).ThenBy(o => o.Z).ThenBy(o => o.X).ThenBy(o => o.Id)
            .Select(o => new TileObjectDto { Id = o.Id, ArchetypeId = o.ArchetypeId, X = o.X, Z = o.Z, Plane = o.Plane, Rotation = o.Rotation & 3, Tags = o.Tags is { Count: > 0 } ? o.Tags : null })
            .ToList();
        dto.Markers = region.Markers
            .OrderBy(m => m.Plane).ThenBy(m => m.Z).ThenBy(m => m.X).ThenBy(m => m.Name, StringComparer.Ordinal)
            .Select(m => new TileMarkerDto { Name = m.Name, X = m.X, Z = m.Z, Plane = m.Plane, Tags = m.Tags is { Count: > 0 } ? m.Tags : null })
            .ToList();
        return JsonSerializer.SerializeToUtf8Bytes(dto, TileWorldJson.Region);
    }

    public static TileRegion Parse(ReadOnlySpan<byte> bytes, RegionCoord expected, int planeCount, string sourceName)
    {
        TileRegionFileDto dto;
        try { dto = JsonSerializer.Deserialize<TileRegionFileDto>(bytes, TileWorldJson.Region) ?? throw new TileWorldException($"{sourceName}: empty region file"); }
        catch (JsonException ex) { throw new TileWorldException($"{sourceName}: {ex.Message}", ex); }
        if (dto.Rx != expected.Rx || dto.Rz != expected.Rz)
            throw new TileWorldException($"{sourceName}: file says region ({dto.Rx}, {dto.Rz}), manifest says {expected}");
        if (dto.Planes.Count != planeCount)
            throw new TileWorldException($"{sourceName}: {dto.Planes.Count} planes, the world has {planeCount}");

        var region = new TileRegion(expected, planeCount);
        for (int i = 0; i < planeCount; i++)
        {
            TileRegionPlaneDto? pd = dto.Planes[i];
            if (pd is null) continue;
            TilePlaneData p = region.Planes[i];
            string where = $"{sourceName} plane {i}";
            const int n = TileRegion.TileCount;
            if (pd.Heights is not null) p.Heights = TileLayerCodec.DecodeShorts(pd.Heights, n, where + " heights");
            if (pd.Underlay is not null) p.Underlay = TileLayerCodec.DecodeUShorts(pd.Underlay, n, where + " underlay");
            if (pd.Overlay is not null) p.Overlay = TileLayerCodec.DecodeUShorts(pd.Overlay, n, where + " overlay");
            if (pd.OverlayShape is not null) p.OverlayShape = TileLayerCodec.DecodeBytes(pd.OverlayShape, n, where + " overlayShape");
            if (pd.OverlayRotation is not null) p.OverlayRotation = TileLayerCodec.DecodeBytes(pd.OverlayRotation, n, where + " overlayRotation");
            if (pd.Settings is not null) p.Settings = TileLayerCodec.DecodeBytes(pd.Settings, n, where + " settings");
        }
        TileRect rect = expected.Rect;
        foreach (TileObjectDto o in dto.Objects)
        {
            if (!rect.Contains(o.X, o.Z)) throw new TileWorldException($"{sourceName}: object {o.Id} at ({o.X}, {o.Z}) is outside region {expected}");
            if ((uint)o.Plane >= (uint)planeCount) throw new TileWorldException($"{sourceName}: object {o.Id} is on plane {o.Plane}, the world has {planeCount}");
            region.Objects.Add(new TileObject { Id = o.Id, ArchetypeId = o.ArchetypeId, X = o.X, Z = o.Z, Plane = o.Plane, Rotation = o.Rotation & 3, Tags = o.Tags });
        }
        foreach (TileMarkerDto m in dto.Markers)
        {
            if (!rect.Contains(m.X, m.Z)) throw new TileWorldException($"{sourceName}: marker '{m.Name}' at ({m.X}, {m.Z}) is outside region {expected}");
            if ((uint)m.Plane >= (uint)planeCount) throw new TileWorldException($"{sourceName}: marker '{m.Name}' is on plane {m.Plane}, the world has {planeCount}");
            region.Markers.Add(new TileMarker { Name = m.Name, X = m.X, Z = m.Z, Plane = m.Plane, Tags = m.Tags });
        }
        return region;
    }
}
