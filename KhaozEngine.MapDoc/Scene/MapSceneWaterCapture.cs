using System.Linq;
using System.Text;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Scene;

/// <summary>Admits a root water owner, including its variable metadata, before copying any retained arrays.</summary>
internal static class MapSceneWaterCapture
{
    internal static long BodyBytes(MapWaterBodyDoc body)
    {
        long bytes = checked(256L + body.Domain.Rects.Count * 32L + body.Domain.Rings.Count * 64L + body.Covers.Count * 40L +
            Encoding.UTF8.GetByteCount(body.Medium) + Encoding.UTF8.GetByteCount(body.RenderMaterial) + Encoding.UTF8.GetByteCount(body.DisplayName ?? ""));
        foreach (var ring in body.Domain.Rings) bytes = checked(bytes + ring.Vertices.Count * 24L);
        foreach (var cover in body.Covers)
            bytes = checked(bytes + Encoding.UTF8.GetByteCount(cover.Id) + Encoding.UTF8.GetByteCount(cover.Anchor.SurfaceId));
        return bytes;
    }

    internal static MapWaterSet Copy(MapWaterSet source, MapWaterBodyDoc body, MapSceneScan scan)
    {
        var child = new MapPrefabWaterChild(new(MapPrefabChildKind.WaterBody, body.Id), body);
        long bytes = checked(MapSceneResourceBounds.MetadataBytes(child) + BodyBytes(body) +
            Encoding.UTF8.GetByteCount("WaterBody:" + body.Id) + 128L + source.Media.Count * 16L);
        foreach (var medium in source.Media) bytes = checked(bytes + 64L + Encoding.UTF8.GetByteCount(medium.Key));
        int memberships = checked(MapSceneResourceBounds.Dependencies(child).Distinct().Count() + 1);
        scan.Retain(bytes, memberships);
        var water = new MapWaterSet();
        water.Media.AddRange(source.Media);
        water.Bodies.Add(body with
        {
            Domain = body.Domain with
            {
                Rects = body.Domain.Rects.ToArray(),
                Rings = body.Domain.Rings.Select(r => r with { Vertices = r.Vertices.ToArray() }).ToArray(),
            },
            Covers = body.Covers.ToArray(),
        });
        return water;
    }
}
