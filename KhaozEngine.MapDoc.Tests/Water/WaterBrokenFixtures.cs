using System;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.Tests.MapDoc.Water;

internal static partial class WaterFixtures
{
    /// <summary><see cref="OceanOverCaves"/> with the named defect, unvalidated.</summary>
    internal static MapDocument Broken(string variant)
    {
        MapDocument doc = OceanOverCaves();
        MapWaterSet water = doc.Water!;
        int ocean = water.Bodies.FindIndex(b => b.Id == "ocean");
        MapWaterBodyDoc body = water.Bodies[ocean];
        MapWaterDomainDoc domain = body.Domain;
        MapRecordRef cover = body.Covers[0];
        switch (variant)
        {
            case "legacy-document": doc.ResolverIdentity = null; break;
            case "native-resolver-one": doc.ResolverIdentity = new(1, 1); break;
            case "duplicate-medium": water.Media.Add(water.Media[0]); break;
            case "zero-wade": water.Media[0] = water.Media[0] with { WadeSpeedScale = 0f }; break;
            case "duplicate-body": water.Bodies.Add(body); break;
            case "infinite-level": body = body with { SurfaceY = float.PositiveInfinity }; break;
            case "tiny-level": body = body with { SurfaceY = 1e-20f }; break;
            case "unknown-medium": body = body with { Medium = "brine" }; break;
            case "empty-material": body = body with { RenderMaterial = "" }; break;
            case "empty-domain":
                body = body with { Domain = domain with { Rects = Array.Empty<MapWaterRect>(), Rings = Array.Empty<MapWaterRing>() } };
                break;
            case "empty-rect": body = body with { Domain = domain with { Rects = new[] { new MapWaterRect(0, 0, 0, 4) } } }; break;
            case "far-rect": body = body with { Domain = domain with { Rects = new[] { new MapWaterRect(0, 0, 1_000_001, 4) } } }; break;
            case "short-ring":
                body = body with
                {
                    Domain = domain with
                    {
                        Rings = new[] { new MapWaterRing(new[] { MapLatticeAddress.Corner(0, 0), MapLatticeAddress.Corner(1, 0) }) },
                    },
                };
                break;
            case "no-cover": body = body with { Covers = Array.Empty<MapRecordRef>() }; break;
            case "repeated-cover": body = body with { Covers = new[] { cover, cover } }; break;
            default: throw new ArgumentException("unknown broken water variant", nameof(variant));
        }
        water.Bodies[ocean] = body;
        return doc;
    }
}
