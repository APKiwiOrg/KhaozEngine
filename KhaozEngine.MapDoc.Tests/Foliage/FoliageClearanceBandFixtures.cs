using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Prefabs;

namespace KhaozEngine.Tests.MapDoc.Foliage;

internal static partial class FoliageFixtures
{
    /// <summary>The roof fixture with floor-1 made a legacy tile surface at 2001 cm under an exterior legacy footprint,
    /// bound by a Space layer at ReferenceY 20.01f with no search below. Its slot (0, 0) cells are NoDraw, so only the
    /// legacy recipe supports the higher owner's sample and its cardinal neighbours. 2001 * 0.01f rounds up to
    /// 20.010000228881836, the binary32 value of 20.01f, while every exact vertex stays at 20.01, just under the band.</summary>
    internal static FoliageFixture LegacyBand()
    {
        var fixture = Clearance("roof");
        MapDocument document = fixture.Document;
        document.Placements[0].X = 4;
        document.Placements[1].X = 8;
        MapSurfacePatch[] patches = document.Surfaces.Patches.Values.Where(p => p.Key.SurfaceId == "floor-1").ToArray();
        foreach (MapSurfacePatch patch in patches) Array.Fill(patch.Heights, 2001);
        MapSurfacePatch anchor = document.Surfaces.Patches[new("floor-1", 0, 0)];
        for (int i = 0; i < anchor.Cells.Length; i++) anchor.Cells[i] = anchor.Cells[i] with { Flags = MapCellFlags.NoDraw };
        anchor.Records.Add(new MapSpaceDoc("level", MapSpaceKind.Exterior, null, null, [], [], [], []));
        anchor.Records.Add(new MapSpaceFootprint("level-domain", new("level", anchor.Key), anchor.Key,
            Enumerable.Range(0, anchor.Depth).SelectMany(z => Enumerable.Range(0, anchor.Width).Select(x => z * 64 + x)).ToArray(),
            new(MapBoundKind.LegacyExteriorV1, "floor-1", null), new(MapBoundKind.OpenTop, null, null)));
        int index = document.Surfaces.Refs.FindIndex(s => s.Id == "floor-1");
        MapSurfaceRef surface = document.Surfaces.Refs[index] with { PresencePolicy = MapPresencePolicy.LegacyTileWorld };
        document.Surfaces.Refs[index] = surface with
        {
            SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface,
                patches.Select(p => new KeyValuePair<MapPatchKey, string>(p.Key, MapSurfaceSemantics.PatchDigest(p))))
        };
        return WithClearance(fixture, l => ClearanceLayer(l, support: new(MapSupportBindingKind.Space, null, "level", 20.01f, 0, 0.5f)));
    }

    /// <summary>The same band edge on a prefab deck posed at scale 16 and lowered 320 m, so the effective transform
    /// moves height. The legacy height maps to 16 * 20.010000228881836 - 320 = 0.160003662109375, the band's low edge,
    /// while every exact vertex freezes to 167772 / 2^20, just under it. Edge fade is off, so a 16 m cell's cardinal
    /// neighbour centres stay inside the prepared chunk context.</summary>
    internal static FoliageFixture LegacyBandPosed()
    {
        var fixture = Clearance("roof");
        MapDocument document = fixture.Document;
        // Roof footprints test support-cell centres, so the higher owner stands on the 16 m deck cell's centre.
        document.Placements[0].X = 8;
        document.Placements[0].Z = -8;
        document.Placements[1].X = 8;
        MapSurfacePatch deck = PrefabFixtures.Patch("deck", 2, 2, 2001);
        for (int i = 0; i < deck.Cells.Length; i++)
            deck.Cells[i] = new(10, 0, MapOverlayCut.Full, 0, MapCellFlags.NoDraw, MapCellTopology.Auto);
        // A prefab space needs a parent. The world parent is one far floor-1 cell, clear of the sampled column.
        MapSurfacePatch far = document.Surfaces.Patches[new("floor-1", -1, -1)];
        far.Records.Add(new MapSpaceDoc("outside", MapSpaceKind.Exterior, null, null, [], [], [], []));
        far.Records.Add(new MapSpaceFootprint("outside-domain", new("outside", far.Key), far.Key, [34 * 64 + 34],
            new(MapBoundKind.SupportFloor, "floor-1", null), new(MapBoundKind.OpenTop, null, null)));
        MapPatchKey key = deck.Key;
        var asset = new MapPrefabAsset(1, "deck-prefab", new MapPrefabChild[]
        {
            new MapPrefabSurfaceChild(new(MapPrefabChildKind.Surface, "deck"),
                PrefabFixtures.Surface("deck", MapSurfaceRole.SupportFloor) with { PresencePolicy = MapPresencePolicy.LegacyTileWorld }, new[] { deck })
            { References = PrefabFixtures.Slots(PrefabFixtures.Surf("/patches/0/key/surfaceId", "deck")) },
            new MapPrefabRecordChild(new(MapPrefabChildKind.Record, "terrace"),
                new MapSpaceDoc("terrace", MapSpaceKind.Exterior, null, null, [], [], [], []), key)
            { References = PrefabFixtures.Slots(PrefabFixtures.Anchor("deck")) },
            new MapPrefabRecordChild(new(MapPrefabChildKind.Record, "terrace-domain"), new MapSpaceFootprint("terrace-domain",
                new("terrace", key), key, new[] { 0, 1, 64, 65 }, new(MapBoundKind.LegacyExteriorV1, "deck", null),
                new(MapBoundKind.OpenTop, null, null)), key)
            {
                References = PrefabFixtures.Slots(PrefabFixtures.Anchor("deck"), PrefabFixtures.Ref("/value/space", "terrace", "deck"),
                    PrefabFixtures.Surf("/value/lattice/surfaceId", "deck"), PrefabFixtures.Surf("/value/lower/surfaceId", "deck")),
            },
        });
        var (roots, source) = ClearanceAssets();
        MapAssetRef prefabRef = source.Add("deck-prefab", System.Text.Encoding.UTF8.GetString(MapPrefabFile.Write(asset)));
        MapAssetRef index = source.Add("deck-prefab-index", new JsonObject
        {
            ["payloadVersion"] = 1,
            ["assets"] = new JsonArray(),
            ["resources"] = new JsonArray(NativeAssetFixtures.Resource(prefabRef, "Prefab", "mesh", "collider", "selection", "material")),
        }.ToJsonString());
        roots = roots.Append(index).ToArray();
        document.NativeAssets = roots.ToList();
        document.Prefabs = new(1, 1, [new("p", "deck-prefab", prefabRef.Sha256, new MapTransform(new(0, -320, 0), 0, 16), new("outside", far.Key),
            new(asset.Children.Select(c => new MapPrefabBinding(c.Key, "p/" + c.Key.Key, null, false)).ToArray()), [])], []);
        var posed = new FoliageFixture(document, MapAssetClosure.Load(roots, source));
        return WithClearance(posed, l => ClearanceLayer(l, support: new(MapSupportBindingKind.Space, null, "p/terrace", 0.160003662109375f, 0, 0.5f),
            ordinals: l.CompatibilityOrdinals.Append(new("p/deck", 1)).ToArray(), edge: 0));
    }
}
