using System.IO;
using System.Numerics;
using System.Linq;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.TileWorld;

namespace KhaozEngine.Tests.MapDocOracle;

internal static partial class ShippedSceneGuardFixtures
{
    static void WriteSceneMarkers(string root, MapDocument native, string variant)
    {
        var binding = new MapSupportBinding(MapSupportBindingKind.Surface, "plane-0", null, null, null, null);
        var marker = MapMarkerHeights.Apply(new(1, "scene-arrival", "arrival", new(1.5f, 0, -1.5f), .125f, "entry", true, ["first", "second"]), new(null, binding));
        var markers = Enumerable.Range(0, variant == "marker-many-moved" ? 24 : 1)
            .Select(i => marker with { Id = i == 0 ? marker.Id : marker.Id + "-" + i, Name = i == 0 ? marker.Name : marker.Name + "-" + i }).ToArray();
        native.BuildingIntent = new(1, [], [], [], markers);
        var scaffold = MapDocumentFile.LoadText(MapDocumentFile.SaveText(native)); scaffold.Placements.Clear();
        File.WriteAllText(Path.Combine(root, "marker-scaffold.json"), MapDocumentFile.SaveText(scaffold));
        File.WriteAllText(Path.Combine(root, "scene-marker-roles.json"), """
            {"schemaVersion":1,"generator":{"path":"generator/build-scaffold.py","helpers":["generator/helper.py"]},
             "sourcePrefabs":[],"buildingManifestPath":null,
             "supports":[{"name":"ground","binding":{"kind":"Surface","surfaceId":"plane-0"},"source":{"kind":"terrain","plane":0,"objectId":null}}],
             "markers":[{"sourceName":"arrival","id":"scene-arrival","role":"entry","projection":"player-start","offsetX":0.5,"offsetZ":0.5,
             "supportName":"ground","heightMode":"Support","heightRule":"terrain","yaw":0.125,"enabled":true,"archetype":null}]}
            """);
        File.WriteAllText(Path.Combine(root, "scene-marker-rows.json"), """
            {"schemaVersion":1,"markers":[{"sourceName":"arrival","x":1,"z":1,"plane":0,"regionX":0,"regionZ":0,"supportName":"ground","spaceId":null,"parents":[],"sceneSpaceId":null,"sceneParents":[]}]}
            """);
        if (variant == "marker-many-moved")
        {
            foreach (string file in new[] { "scene-marker-roles.json", "scene-marker-rows.json" })
            {
                var json = JsonNode.Parse(File.ReadAllText(Path.Combine(root, file)))!;
                var rows = json["markers"]!.AsArray();
                for (int i = 1; i < markers.Length; i++)
                {
                    var row = rows[0]!.DeepClone(); row["sourceName"] = markers[i].Name;
                    if (row.AsObject().ContainsKey("id")) row["id"] = markers[i].Id;
                    rows.Add(row);
                }
                File.WriteAllText(Path.Combine(root, file), json.ToJsonString());
            }
        }
        if (variant is "marker-scene-room" or "marker-wrong-scene-room" or "marker-wrong-scene-parent")
        {
            var patch = native.Surfaces.Patches.Values.First(); var key = patch.Key;
            patch.Records.Add(new MapSpaceDoc("scene-exterior", MapSpaceKind.Exterior, null, null, [], [], [], []));
            patch.Records.Add(new MapSpaceDoc("scene-room", MapSpaceKind.Room, new("scene-exterior", key), null, [], [], [], []));
            patch.Records.Add(new MapSpaceFootprint("scene-exterior-domain", new("scene-exterior", key), key, [65],
                new(MapBoundKind.SupportFloor, "plane-0", null), new(MapBoundKind.OpenTop, null, null)));
            native.BuildingIntent = new(1, [], [], [], [marker]) { Storeys = [new(1, "scene-storey", "Scene floor", null)] };
            patch.Records.Add(new MapPrismDomain("scene-room-domain", 1, new("scene-room", key), new("scene-exterior", key), "", "scene-storey",
                new(MapLatticeFrame.ImportedMetreCentimetre, new[] { new MapWaterRect(1, 1, 2, 2) }, []), 0, 4));
            var rows = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "scene-marker-rows.json")))!;
            rows["markers"]![0]!["sceneSpaceId"] = variant == "marker-wrong-scene-room" ? "wrong-room" : "scene-room";
            rows["markers"]![0]!["sceneParents"] = new JsonArray(variant == "marker-wrong-scene-parent" ? "wrong-parent" : "scene-exterior");
            File.WriteAllText(Path.Combine(root, "scene-marker-rows.json"), rows.ToJsonString());
        }
        if (variant.StartsWith("marker-missing-", System.StringComparison.Ordinal))
        {
            var rows = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "scene-marker-rows.json")))!;
            rows["markers"]![0]!.AsObject().Remove(variant["marker-missing-".Length..]);
            File.WriteAllText(Path.Combine(root, "scene-marker-rows.json"), rows.ToJsonString());
        }
        File.WriteAllText(Path.Combine(root, "scene-marker-comparison.json"), """
            {"schemaVersion":1,"nativeDocumentPath":"marker-scaffold.json","roleManifestPath":"scene-marker-roles.json","markerManifestPath":"scene-marker-rows.json",
             "expectations":{"total":1,"playerStart":1,"spawn":0,"landmark":0,"label":0,"negativeX":0,"supportBound":1,"enabled":1}}
            """);
        if (variant == "marker-many-moved")
        {
            string path = Path.Combine(root, "scene-marker-comparison.json");
            var json = JsonNode.Parse(File.ReadAllText(path))!;
            foreach (string field in new[] { "total", "playerStart", "supportBound", "enabled" }) json["expectations"]![field] = markers.Length;
            File.WriteAllText(path, json.ToJsonString());
            native.BuildingIntent = new(1, [], [], [], markers.Select(m => m with { Position = m.Position + Vector3.UnitX, YawRadians = m.YawRadians + .25f }).ToArray());
        }
        if (variant == "marker-omitted") native.BuildingIntent = new(1, [], [], [], []);
        if (variant == "marker-moved") native.BuildingIntent = new(1, [], [], [], [marker with { Position = marker.Position + Vector3.UnitX }]);
    }
}
