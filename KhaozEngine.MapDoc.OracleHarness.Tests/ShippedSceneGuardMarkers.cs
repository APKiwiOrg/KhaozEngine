using System.IO;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.TileWorld;

namespace KhaozEngine.Tests.MapDocOracle;

internal static partial class ShippedSceneGuardFixtures
{
    static void WriteSceneMarkers(string root, MapDocument native, string variant)
    {
        var binding = new MapSupportBinding(MapSupportBindingKind.Surface, "plane-0", null, null, null, null);
        var marker = MapMarkerHeights.Apply(new(1, "scene-arrival", "arrival", new(1.5f, 0, -1.5f), .125f, "entry", true, ["first", "second"]), new(null, binding));
        native.BuildingIntent = new(1, [], [], [], [marker]);
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
            {"schemaVersion":1,"markers":[{"sourceName":"arrival","x":1,"z":1,"plane":0,"regionX":0,"regionZ":0,"supportName":"ground","spaceId":null,"parents":[]}]}
            """);
        File.WriteAllText(Path.Combine(root, "scene-marker-comparison.json"), """
            {"schemaVersion":1,"nativeDocumentPath":"marker-scaffold.json","roleManifestPath":"scene-marker-roles.json","markerManifestPath":"scene-marker-rows.json",
             "expectations":{"total":1,"playerStart":1,"spawn":0,"landmark":0,"label":0,"negativeX":0,"supportBound":1,"enabled":1}}
            """);
        if (variant == "marker-omitted") native.BuildingIntent = new(1, [], [], [], []);
        if (variant == "marker-moved") native.BuildingIntent = new(1, [], [], [], [marker with { Position = marker.Position + Vector3.UnitX }]);
    }
}
