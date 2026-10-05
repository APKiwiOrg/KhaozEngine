using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapEdit;

namespace KhaozEngine.Tests.MapDoc;

/// <summary>A saved native document and its local resources in an isolated directory. Asset IDs, resource IDs
/// and resource paths deliberately differ so no consumer can infer a path from an identity.</summary>
internal sealed class NativeLifecycleFixture : IDisposable
{
    /// <summary>2^53 + 1, the first identity a double-based JSON number cannot carry exactly.</summary>
    public const long HighWater = 9_007_199_254_740_993;

    public const string MeshRelativePath = "kit/meshes/prop-body.glb";
    public const string LodRelativePath = "kit/meshes/prop-far.glb";
    public const string ManifestRelativePath = "kit/props.manifest.json";

    readonly string _root = Path.Combine(Path.GetTempPath(), "native-lifecycle-" + Guid.NewGuid().ToString("N"));
    readonly bool _absoluteReferences;

    public MapEditSession Session { get; } = new();
    public string ResourceRoot => _root;
    public string ValidPath => Path.Combine(_root, "world.map.json");
    public string BadPath => Path.Combine(_root, "stale.map.json");
    public MapDocument Document { get; }
    public MapAssetClosure Closure { get; }
    public MapResolvedAsset Asset { get; }

    public NativeLifecycleFixture(bool absoluteReferences = false)
    {
        _absoluteReferences = absoluteReferences;
        Directory.CreateDirectory(Path.Combine(_root, "kit", "meshes"));
        MapAssetRef mesh = Write("prop.mesh", MeshRelativePath, "mesh-v1");
        MapAssetRef lod = Write("prop.lod0", LodRelativePath, "lod-v1");
        string manifest = """
            {"payloadVersion":1,"assets":[
              {"id":"prop","meshResourceId":"prop.mesh","supportResourceIds":[],"materialResourceIds":[],
               "lodResourceIds":["prop.lod0"],"lightResourceIds":[],"sourceUnitsToMetres":0.5,
               "renderBounds":{"min":{"x":-1,"y":-1,"z":-1},"max":{"x":1,"y":3,"z":1}},
               "source":"authored kit","license":"CC0","category":"fence","textured":true}],
             "resources":[MESH,LOD]}
            """
            .Replace("MESH", Resource(mesh, "Mesh"), StringComparison.Ordinal)
            .Replace("LOD", Resource(lod, "Lod"), StringComparison.Ordinal);
        MapAssetRef root = Write("kit", ManifestRelativePath, manifest);

        Document = NewDocument("native-lifecycle", root);
        MapDocumentFile.Save(Document, ValidPath);
        MapDocument stale = NewDocument("stale-lifecycle", root with { Sha256 = new string('0', 64) });
        MapDocumentFile.Save(stale, BadPath);

        Closure = MapAssetClosure.Load(Document.NativeAssets, new MapDirectoryAssetSource(_root));
        Asset = Closure.GetAsset("prop");
        Session.Open(ValidPath);
    }

    static MapDocument NewDocument(string id, MapAssetRef root)
    {
        var doc = new MapDocument
        {
            Id = id,
            Bounds = new() { MinX = -1000, MinZ = -1000, MaxX = 1000, MaxZ = 1000 },
            PlayableBounds = new() { MinX = -500, MinZ = -500, MaxX = 500, MaxZ = 500 },
            ResolverIdentity = new(1, 1),
            NativeAssets = new() { root },
            NumericIdHighWaterMark = HighWater,
        };
        doc.Terrain.Biomes.Add(new MapBiomeBand());
        doc.Placements.Add(new MapPlacement { Id = "gate", Kind = "prop", AssetId = "prop", NumericId = HighWater, X = 4, Z = 5 });
        doc.Placements.Add(new MapPlacement { Id = "post", Kind = "prop", AssetId = "prop", X = 9, Y = 2, Z = -3 });
        return doc;
    }

    MapAssetRef Write(string id, string relativePath, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        File.WriteAllBytes(Path.Combine(_root, relativePath), bytes);
        string path = _absoluteReferences ? Path.Combine(_root, relativePath) : relativePath;
        return new(id, path, Digest(bytes), 1);
    }

    static string Resource(MapAssetRef reference, string kind) =>
        $$"""{"reference":{"id":"{{reference.Id}}","path":"{{reference.Path}}","sha256":"{{reference.Sha256}}","payloadVersion":1},"kind":"{{kind}}","dependencies":[]}""";

    public static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Copies every resource below <paramref name="directory"/> at the same relative paths.</summary>
    public void CopyResourcesTo(string directory)
    {
        foreach (string relative in new[] { MeshRelativePath, LodRelativePath, ManifestRelativePath })
        {
            string target = Path.Combine(directory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(_root, relative), target, overwrite: true);
        }
    }

    /// <summary>Replaces the mesh bytes, leaving every declared digest stale.</summary>
    public void CorruptResource() => File.WriteAllText(Path.Combine(_root, MeshRelativePath), "mesh-v2");

    public void CorruptLod() => File.WriteAllText(Path.Combine(_root, LodRelativePath), "lod-v2");

    public byte[] ReadSavedBytes() => File.ReadAllBytes(ValidPath);

    public MapDocument SessionDocument => Session.WithDocument((doc, _) => doc);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
