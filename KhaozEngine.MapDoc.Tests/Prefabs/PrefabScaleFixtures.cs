using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

internal static partial class PrefabFixtures
{
    // Invented public geometry. Copies of the painted roomless hut over one flat world floor of several regions.
    // Each region is a 64 cell wide patch of the given depth, from slot firstSlot eastward along one slot row, so the
    // floor's face count is fixed by the caller.
    internal static PrefabFixture ScatteredPaint(IReadOnlyList<Vector3> sites, float scale, int regions, int depth, MapRational cellUnit,
        int firstSlot = 0)
    {
        var f = Create("uneven-painted");
        var document = f.Document;
        document.Surfaces.Patches.Remove(new("ground", 0, 0));
        int index = document.Surfaces.Refs.FindIndex(s => s.Id == "ground");
        var ground = document.Surfaces.Refs[index];
        document.Surfaces.Refs[index] = ground with { Frame = ground.Frame with { CellUnitMetres = cellUnit } };
        for (int slot = firstSlot; slot < firstSlot + regions; slot++)
        {
            var patch = Patch("ground", 64, depth, 0);
            patch.Key = new("ground", slot, 0);
            Array.Fill(patch.Cells, new(3, 0, MapOverlayCut.Full, 0, MapCellFlags.None, MapCellTopology.Auto));
            document.Surfaces.Patches.Add(patch.Key, patch);
        }
        Place(document, f.Assets, sites.Select(site => new MapTransform(site, 0.371f, scale)).ToArray(), null);
        return f;
    }

    // Invented public geometry. Copies of the basic hut, each with one 4 by 4 m room, inside one exterior
    // footprint of 64 by 64 cells at y -2. Sites with distinct heights stack rooms in one column.
    internal static PrefabFixture ScatteredRooms(IReadOnlyList<Vector3> sites, MapRational cellUnit)
    {
        var f = Create();
        var document = f.Document;
        var key = new MapPatchKey("exterior", 0, 0);
        var exterior = Patch("exterior", 64, 64, -200);
        exterior.Records.Add(new MapSpaceDoc("exterior", MapSpaceKind.Exterior, null, null, [], [], [], []));
        exterior.Records.Add(new MapSpaceFootprint("exterior-domain", new("exterior", key), key, Enumerable.Range(0, 4096).ToArray(),
            new(MapBoundKind.SupportFloor, "exterior", null), new(MapBoundKind.OpenTop, null, null)));
        document.Surfaces.Patches[key] = exterior;
        int index = document.Surfaces.Refs.FindIndex(s => s.Id == "exterior");
        var surface = Surface("exterior", MapSurfaceRole.SupportFloor);
        document.Surfaces.Refs[index] = surface with { Frame = surface.Frame with { CellUnitMetres = cellUnit } };
        SealWorldParent(document);
        Place(document, f.Assets, sites.Select(site => new MapTransform(site, 0, 1)).ToArray(), new("exterior", key));
        return f;
    }

    // The first copy keeps instance ID a, so its world IDs match the single-instance fixtures.
    static void Place(MapDocument document, MapAssetClosure assets, IReadOnlyList<MapTransform> poses, MapRecordRef? parent)
    {
        var asset = assets.GetPrefab("hut");
        string sha = assets.GetResource("hut").Reference.Sha256;
        document.Prefabs = new(1, 1, [], []);
        document.NumericIdHighWaterMark = 100;
        for (int i = 0; i < poses.Count; i++)
        {
            string id = i == 0 ? "a" : "copy" + i;
            var bindings = asset.Children.Select(c => new MapPrefabBinding(c.Key, id + "/" + c.Key.Key,
                c.Key.Kind == MapPrefabChildKind.Placement ? ++document.NumericIdHighWaterMark : null, false)).ToArray();
            // Every copy after the first renames its marker, because marker names are globally unique.
            var instance = new MapPrefabInstance(id, "hut", sha, poses[i], parent, new(bindings), i == 0 ? [] : MarkerNames(asset, id));
            document.Prefabs = new(1, 1, document.Prefabs.Instances.Append(instance).ToArray(), []);
            var plan = MapPrefabBindingPlanner.Plan(document, asset, assets, id, instance.Bindings);
            document.Prefabs = new(1, 1, document.Prefabs.Instances.Select(p => p.Id == id ? p with { Bindings = plan.Bindings } : p).ToArray(), []);
            document.NumericIdHighWaterMark = plan.HighWaterMark;
        }
    }
}
