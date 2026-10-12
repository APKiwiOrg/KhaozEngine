using System;

namespace KhaozEngine.TileWorld;

/// <summary>Detached terrain and material values for CPU region builds.</summary>
internal static class TileWorldBuildSnapshot
{
    internal static TileWorldDocument CaptureGround(TileWorldDocument document, RegionCoord region)
    {
        ArgumentNullException.ThrowIfNull(document);
        var snapshot = new TileWorldDocument
        {
            Id = document.Id,
            DisplayName = document.DisplayName,
            TileSize = document.TileSize,
            PlaneCount = document.PlaneCount,
            PlaneHeight = document.PlaneHeight,
        };
        for (int rz = region.Rz - 1; rz <= region.Rz + 1; rz++)
            for (int rx = region.Rx - 1; rx <= region.Rx + 1; rx++)
            {
                var coord = new RegionCoord(rx, rz);
                if (document.GetRegion(coord) is not { } source) continue;
                TileRegion copy = snapshot.GetOrCreateRegion(coord);
                for (int plane = 0; plane < document.PlaneCount; plane++)
                    CopyPlane(source.Planes[plane], copy.Planes[plane]);
                copy.Dirty = false;
            }
        return snapshot;
    }

    internal static TileWorldCatalogs CaptureMaterials(TileWorldCatalogs catalogs)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        return catalogs.CopyMaterials();
    }

    static void CopyPlane(TilePlaneData source, TilePlaneData destination)
    {
        destination.Heights = (short[]?)source.Heights?.Clone();
        destination.Underlay = (ushort[]?)source.Underlay?.Clone();
        destination.Overlay = (ushort[]?)source.Overlay?.Clone();
        destination.OverlayShape = (byte[]?)source.OverlayShape?.Clone();
        destination.OverlayRotation = (byte[]?)source.OverlayRotation?.Clone();
        destination.Settings = (byte[]?)source.Settings?.Clone();
    }
}
