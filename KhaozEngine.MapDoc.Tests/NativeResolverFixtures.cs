using System.Linq;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;

namespace KhaozEngine.Tests.MapDoc;

internal static class NativeResolverFixtures
{
    internal static (MapDocument Document, MapAssetClosure Assets, MapResolveOptions Options) Create()
    {
        var f = NativeAssetFixtures.Valid();
        var doc = MapDocumentFile.LoadText(NativeFixtures.AnalyticV3Json());
        doc.ResolverIdentity = new(1, 1);
        doc.NativeAssets = f.Roots.ToList();
        doc.NumericIdHighWaterMark = 12;
        var a = doc.Placements[0];
        a.Id = "a";
        a.AssetId = "tree";
        a.NumericId = 11;
        doc.Placements.Add(new MapPlacement
        {
            Id = "b",
            Kind = "scenery",
            AssetId = "tree",
            NumericId = 12,
            X = 70,
            Z = -70,
            Y = 2.5f,
            Tags = new() { "first", "second" }
        });
        return (doc, MapAssetClosure.Load(f.Roots, f.Source), new("headless", 1, "options"));
    }

    internal static (MapDocument Document, MapAssetClosure Assets) WithChangedClosure(MapDocument document, MapAssetClosure assets)
    {
        var f = NativeAssetFixtures.Valid();
        var material = f.Source.Add("material", "changed material byte");
        var manifest = JsonNode.Parse(f.Source.Read(f.Roots[1]).Span)!.AsObject();
        var resource = manifest["resources"]!.AsArray().Single(n => n!["reference"]!["id"]!.GetValue<string>() == "material")!;
        resource["reference"]!["sha256"] = material.Sha256;
        var roots = new[] { f.Roots[0], f.Source.Add("root-b", manifest.ToJsonString()) };
        var clone = MapDocumentFile.LoadText(MapDocumentFile.SaveText(document));
        clone.NativeAssets = roots.ToList();
        return (clone, MapAssetClosure.Load(roots, f.Source));
    }
}
