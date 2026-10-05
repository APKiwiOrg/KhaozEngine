using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapEditor;
using KhaozEngine.MapEdit;

namespace KhaozEngine.Tests.MapDoc;

internal sealed class NativePlacementHistoryFixture : IDisposable
{
    readonly string _directory = Path.Combine(Path.GetTempPath(), "native-history-" + Guid.NewGuid().ToString("N"));
    public MapDocument Document { get; }
    public EditorDocument Editor { get; }
    public MapEditSession Session { get; } = new();
    public MutationService Service { get; }
    public MapAssetClosure Assets { get; }
    public string PathName => Path.Combine(_directory, "map.json");

    public NativePlacementHistoryFixture()
    {
        Directory.CreateDirectory(_directory);
        var mesh = Write("mesh", "mesh bytes");
        string manifest = """
            {"payloadVersion":1,"assets":[
              {"id":"prop","meshResourceId":"mesh","supportResourceIds":[],"materialResourceIds":[],
               "lodResourceIds":[],"lightResourceIds":[],"sourceUnitsToMetres":1,
               "renderBounds":{"min":{"x":-1,"y":-2,"z":-1},"max":{"x":1,"y":2,"z":1}},
               "source":"authored","license":"CC0","textured":false}],
             "resources":[{"reference":{"id":"mesh","path":"mesh","sha256":"DIGEST","payloadVersion":1},
               "kind":"Mesh","dependencies":[]}]}
            """.Replace("DIGEST", mesh.Sha256, StringComparison.Ordinal);
        var json = JsonNode.Parse(manifest)!;
        var unplaced = json["assets"]![0]!.DeepClone();
        unplaced["id"] = "unplaced";
        json["assets"]!.AsArray().Add(unplaced);
        var root = Write("manifest", json.ToJsonString());
        Document = new MapDocument
        {
            Id = "native-history",
            Bounds = new() { MinX = -1000, MinZ = -1000, MaxX = 1000, MaxZ = 1000 },
            PlayableBounds = new() { MinX = -1000, MinZ = -1000, MaxX = 1000, MaxZ = 1000 },
            ResolverIdentity = new(1, 1),
            NativeAssets = new() { root },
            NumericIdHighWaterMark = 10,
        };
        Document.Terrain.Biomes.Add(new MapBiomeBand());
        var existing = NewProp("existing");
        existing.NumericId = 10;
        Document.Placements.Add(existing);
        Assets = MapAssetClosure.Load(Document.NativeAssets, new MapDirectoryAssetSource(_directory));
        MapBoundDocumentValidation.Validate(Document, Assets);
        Editor = new EditorDocument(Document);
        Editor.BindNativeAssets(Assets);
        MapDocumentFile.Save(Document, PathName);
        Session.Open(PathName);
        Session.BindNativeAssets(Assets);
        Service = new MutationService(Session);
    }

    MapAssetRef Write(string id, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        File.WriteAllBytes(Path.Combine(_directory, id), bytes);
        return new(id, id, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), 1);
    }

    /// <summary>Copies the manifest and mesh beside a tiled directory, the resource root native lifecycle binds.</summary>
    public void CopyResourcesTo(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (string name in new[] { "manifest", "mesh" })
            File.Copy(Path.Combine(_directory, name), Path.Combine(directory, name), overwrite: true);
    }

    public MapPlacement NewProp(string id) => new()
    {
        Id = id,
        Kind = "prop",
        AssetId = "prop",
        Y = -123.5f,
        Tags = new() { "existing", "prop", "mesh" },
    };

    public MapDocument SessionDocument => Session.WithDocument((doc, _) => doc);
    public void Dispose() => Directory.Delete(_directory, true);
}
