using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapEdit;
using KhaozEngine.MapEditor;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeLightExclusionEditTests
{
    [Fact]
    public void ExclusionOnlyChange_InvalidatesTheEmitterResidencyBounds()
    {
        var f = PrefabFixtures.NativeOwnedLight(.371f, 2, false);
        var json = JsonNode.Parse(MapDocumentFile.SaveText(f.Document))!;
        json["buildingIntent"]!["lights"]![0]!["shadowExclusion"] = JsonNode.Parse("""{"kind":"NearSphere","radius":0.625}""");
        var after = MapDocumentFile.LoadText(json.ToJsonString());
        var empty = new MapNativeEditEffects(null, null, [], [], [], [], MapNativeInvalidation.None);
        var effects = NativePlacementEffects.Combine(empty with { LightIds = ["lamp"] }, empty,
            f.Document, after, f.Assets, f.Assets, new([], [], [], [], [], [], false) { BuildingIntent = true }, default);
        Assert.Equal(MapNativeInvalidation.Residency, effects.Invalidates);
        Assert.NotNull(effects.OldBounds); Assert.Equal(effects.OldBounds, effects.NewBounds);
    }

    [Fact]
    public void PrefabEditor_ChangingLightReachPreservesItsExclusion()
    {
        var f = PrefabFixtures.Create("roomless");
        var asset = f.Assets.GetPrefab("hut");
        var owner = asset.Children.OfType<MapPrefabPlacementChild>().Single();
        asset = asset with
        {
            Children = asset.Children.Append(new MapPrefabLightChild(new(MapPrefabChildKind.Light, "emitter"),
                new(1, "emitter", new(MapPrefabReferenceScope.Local, MapPrefabChildKind.Placement, owner.Key.Key), Vector3.UnitY, Vector3.One, 1, 4, false))).ToArray()
        };
        var json = JsonNode.Parse(MapPrefabFile.Write(asset))!;
        var declaration = JsonNode.Parse("""{"kind":"NearSphere","radius":0.625}""");
        json["children"]!.AsArray().Single(c => c!["type"]!.GetValue<string>() == "Light")!["value"]!["shadowExclusion"] = declaration;
        asset = MapPrefabFile.Read(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()), asset.Id);
        var assets = PrefabFixtures.PublishPrefab(f.Assets, asset);
        f.Document.NativeAssets = assets.Roots.ToList();
        var parent = f.Document.Prefabs!.Instances.Single().ParentSpace;
        f.Document.Prefabs = new(1, 1, [], []);
        var service = new PrefabService(PrefabEditorFixtures.Session(new(f.Document, assets)));
        var opened = service.Open("hut", parent);
        var light = opened.Children.OfType<MapPrefabLightChild>().Single();
        service.Edit(light with { Value = light.Value with { Radius = 3 } });
        var exported = service.Export().Children.OfType<MapPrefabLightChild>().Single().Value;
        Assert.Equal(3, exported.Radius);
        Assert.True(JsonNode.DeepEquals(declaration, JsonNode.Parse(MapPrefabJson.Bytes(exported))!["shadowExclusion"]));
        service.Cancel();
    }
}
