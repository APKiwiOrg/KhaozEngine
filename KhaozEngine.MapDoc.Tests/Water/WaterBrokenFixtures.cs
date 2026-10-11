using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
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
            case "bow-tie": body = OnlyRing(body, (0, 0), (4, 4), (4, 0), (0, 4)); break;
            case "collinear": body = OnlyRing(body, (0, 0), (2, 0), (4, 0)); break;
            case "overlapping-pieces": body = body with { Domain = domain with { Rects = new[] { Rect(0, 0, 8, 4), Rect(6, 0, 12, 4) } } }; break;
            case "overlapping-ring":
                body = OnlyRing(body, (6, 0), (12, 0), (12, 4), (10, 4), (10, 2), (6, 2));
                body = body with { Domain = body.Domain with { Rects = new[] { Rect(0, 0, 8, 4) } } };
                break;
            case "overlapping-bodies":
                water.Bodies.Add(Body("second-lake", -35f, Rect(4, 0, 6, 4), Cover("lake-cave", "lake-floor")));
                break;
            case "too-many-pieces":
                body = body with
                {
                    Domain = domain with { Rects = Enumerable.Range(0, 65_537).Select(i => Rect(i % 256, i / 256, i % 256 + 1, i / 256 + 1)).ToArray() },
                };
                break;
            case "too-many-vertices":
                // 65,535 rects count 262,140 vertices, and a five-vertex ring takes the domain one past the limit.
                body = OnlyRing(body, (0, 300), (2, 300), (3, 301), (2, 302), (0, 302));
                body = body with
                {
                    Domain = body.Domain with { Rects = Enumerable.Range(0, 65_535).Select(i => Rect(i % 256, i / 256, i % 256 + 1, i / 256 + 1)).ToArray() },
                };
                break;
            case "domain-off-footprint": body = body with { Domain = domain with { Rects = new[] { Rect(20, 0, 24, 4) } } }; break;
            case "missing-cover": body = body with { Covers = new[] { new MapRecordRef("nowhere", cover.Anchor) } }; break;
            case "absent-cover-patch":
                body = body with { Covers = new[] { new MapRecordRef("nowhere", new MapPatchKey("no-such-surface", 0, 0)) } };
                break;
            case "alias-cover":
                doc.Surfaces.Patches[cover.Anchor].Records.Add(new MapSpaceDoc("sea-alias", MapSpaceKind.Exterior, null, cover,
                    Array.Empty<string>(), Array.Empty<MapBoundaryRef>(), Array.Empty<MapBoundaryRef>(), Array.Empty<MapRecordRef>()));
                body = body with { Covers = new[] { cover, new MapRecordRef("sea-alias", cover.Anchor) } };
                break;
            case "unknown-material": body = body with { RenderMaterial = "no-such-material" }; break;
            // The -34 m contour of the lake roof becomes the diagonal x + row = 4.
            case "diagonal-roof": SetHeights(doc, "lake-roof", (x, row) => -3600 + 50 * (int)(x + row)); break;
            case "flat-roof-at-surface": SetHeights(doc, "lake-roof", (_, _) => -3400); break;
            case "fine-roof":
                {
                    MapSurfacePatch roof = doc.Surfaces.Patches[Slot0("lake-roof")];
                    for (int x = 0; x < roof.Width; x++)
                    {
                        roof.EdgeSubdivisions.Add(new MapEdgeSubdivision(x, 0, MapCellEdge.South, 8));
                        roof.EdgeSubdivisions.Add(new MapEdgeSubdivision(x, roof.Depth - 1, MapCellEdge.North, 8));
                    }
                    for (int z = 0; z < roof.Depth; z++)
                    {
                        roof.EdgeSubdivisions.Add(new MapEdgeSubdivision(0, z, MapCellEdge.West, 8));
                        roof.EdgeSubdivisions.Add(new MapEdgeSubdivision(roof.Width - 1, z, MapCellEdge.East, 8));
                    }
                    Reseal(doc);
                    break;
                }
            case "lake-roof-missing-cell": ClearPresence(doc, "lake-roof", (2, 1)); break;
            case "lake-roof-partial": ClearPresence(doc, "lake-roof", Enumerable.Range(0, 6).Select(x => (x, 1)).ToArray()); break;
            case "seabed-hole": ClearPresence(doc, "seabed", (9, 1)); break;
            case "flood-floor-lowered": SetHeights(doc, "flood-floor", (_, _) => -4100); break;
            case "seabed-half-height-unit": SetHeightUnit(doc, "seabed", new(1, 200)); break;
            case "flood-floor-half-height-unit": SetHeightUnit(doc, "flood-floor", new(1, 200)); break;
            case "split-ocean":
                water.Bodies.Add(body with { Id = "ocean-east", Domain = domain with { Rects = new[] { Rect(6, 0, 12, 4) } } });
                body = body with { Id = "ocean-west", Domain = domain with { Rects = new[] { Rect(0, 0, 6, 4) } } };
                break;
            default: throw new ArgumentException("unknown broken water variant", nameof(variant));
        }
        water.Bodies[ocean] = body;
        return doc;
    }

    static MapWaterBodyDoc OnlyRing(MapWaterBodyDoc body, params (long X, long Z)[] corners) => body with
    {
        Domain = body.Domain with
        {
            Rects = Array.Empty<MapWaterRect>(),
            Rings = new[] { new MapWaterRing(corners.Select(c => MapLatticeAddress.Corner(c.X, c.Z)).ToArray()) },
        },
    };
}
