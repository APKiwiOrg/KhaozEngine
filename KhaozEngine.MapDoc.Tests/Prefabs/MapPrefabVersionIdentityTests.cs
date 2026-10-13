using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public sealed class MapPrefabVersionIdentityTests
{
    [Theory]
    [InlineData("hut")]
    [InlineData("hut / ü")]
    public void DigestAddressedIdentity_VerifiesBytesInsideRead(string logicalId)
    {
        var asset = PrefabFixtures.Create("roomless").Assets.GetPrefab("hut") with { Id = logicalId };
        byte[] bytes = MapPrefabFile.Write(asset);
        string escaped = logicalId == "hut" ? "hut" : "hut%20%2F%20%C3%BC";
        string id = "prefab/" + escaped + "/" + Convert.ToHexStringLower(SHA256.HashData(bytes));
        Assert.Equal(logicalId, MapPrefabFile.Read(bytes, id).Id);
        Assert.Equal(logicalId, MapPrefabFile.Read(bytes, logicalId).Id);
    }
    [Theory]
    [InlineData("wrong-sha")]
    [InlineData("wrong-logical")]
    [InlineData("uppercase")]
    [InlineData("short")]
    [InlineData("extra-segment")]
    [InlineData("wrong-prefix")]
    public void InvalidVersionIdentity_RetainsTheReleasedRefusal(string variant)
    {
        byte[] bytes = PrefabFixtures.AssetBytes("roomless");
        string sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        string id = variant switch
        {
            "wrong-sha" => "prefab/hut/" + new string('0', 64),
            "wrong-logical" => "prefab/other/" + sha,
            "uppercase" => "prefab/hut/" + sha.ToUpperInvariant(),
            "short" => "prefab/hut/" + sha[..63],
            "extra-segment" => "prefab/hut/" + sha + "/extra",
            _ => "other/hut/" + sha,
        };
        Assert.Equal("Prefab payload ID does not match its resource ID.", Assert.Throws<MapDocumentException>(() => MapPrefabFile.Read(bytes, id)).Message);
    }
    [Fact]
    public void LoadedAndStoredClosures_AcceptTheSameLogicalPayload()
    {
        var f = PrefabFixtures.Create("roomless");
        byte[] bytes = f.Assets.GetResource("hut").Bytes.ToArray();
        string sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        string versionId = "prefab/hut/" + sha;
        var manifest = JsonNode.Parse(f.Assets.GetResource("manifest").Bytes.Span)!.AsObject();
        var row = manifest["resources"]!.AsArray().Single(r => r!["reference"]!["id"]!.GetValue<string>() == "hut")!;
        row["reference"]!["id"] = versionId;
        row["reference"]!["path"] = "versions/" + versionId;
        byte[] manifestBytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        var root = f.Assets.Roots.Single() with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(manifestBytes)) };
        var source = new CapturedSource(f.Assets, versionId, bytes, manifestBytes);
        var closure = MapAssetClosure.Load(new[] { root }, source);
        Assert.Equal("hut", closure.GetPrefab(versionId).Id);
        f.Document.NativeAssets = closure.Roots.ToList();
        var instance = f.Document.Prefabs!.Instances.Single();
        f.Document.Prefabs = new(1, 1, new[] { instance with { ResourceId = versionId } }, []);
        string dir = PrefabFixtures.TempDirectory();
        try
        {
            foreach (var resource in closure.CapturedResources)
            {
                string target = Path.Combine(dir, resource.Reference.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, resource.Bytes.ToArray());
            }
            string path = Path.Combine(dir, "world.map.json");
            MapDocumentFile.Save(f.Document, path);
            var stored = MapDocumentFile.Load(path);
            var storedClosure = MapAssetClosure.Load(stored.NativeAssets, new MapDirectoryAssetSource(dir));
            Assert.Equal("hut", storedClosure.GetPrefab(versionId).Id);
            Assert.Equal(MapNativeResolution.Resolve(f.Document, closure, PrefabFixtures.Options()).AuthoredHash,
                MapNativeResolution.Resolve(stored, storedClosure, PrefabFixtures.Options()).AuthoredHash);
        }
        finally { Directory.Delete(dir, true); }
    }
    sealed class CapturedSource(MapAssetClosure assets, string id, byte[] prefab, byte[] manifest) : IMapAssetSource
    {
        public ReadOnlyMemory<byte> Read(MapAssetRef reference) => reference.Id == "manifest" ? manifest : reference.Id == id ? prefab : assets.GetResource(reference.Id).Bytes;
    }
}
