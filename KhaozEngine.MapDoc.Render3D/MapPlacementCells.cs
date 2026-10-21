using System;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Native HLOD uses the document's half-open storage cells, with precise origin assignment.</summary>
internal static class MapPlacementCells
{
    internal static PropClusterKey Key(string layerId, MapEffectiveTransform pose, float tileSize)
    {
        try { return new(layerId, checked((int)Math.Floor(pose.X / tileSize)), checked((int)Math.Floor(pose.Z / tileSize)), 0); }
        catch (OverflowException ex) { throw new MapDocumentException("Native placement cell is not representable.", ex); }
    }

    internal static (RenderAnchor Anchor, RectArea Area) Domain(PropClusterKey key, float tileSize)
    {
        if (key.Plane != 0 || !float.IsFinite(tileSize) || tileSize <= 0) throw new MapDocumentException("Invalid native placement cell.");
        double x = key.X * (double)tileSize, z = key.Z * (double)tileSize;
        try
        {
            // RenderAnchor is whole metres. Any fractional cell origin stays in the small local area.
            var anchor = new RenderAnchor(checked((long)Math.Floor(x)), 0, checked((long)Math.Floor(z)));
            return (anchor, new((float)(x - anchor.X), (float)(z - anchor.Z),
                (float)(x - anchor.X + tileSize), (float)(z - anchor.Z + tileSize)));
        }
        catch (OverflowException ex) { throw new MapDocumentException("Native placement cell anchor is not representable.", ex); }
    }

    internal static string Owner(PropClusterKey key) => FormattableString.Invariant($"cluster/{key.LayerId}/{key.X}/{key.Z}/{key.Plane}");
}
