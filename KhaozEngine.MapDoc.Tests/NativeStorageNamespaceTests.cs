using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeStorageNamespaceTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "native-storage-" + Guid.NewGuid().ToString("N"));

    public NativeStorageNamespaceTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    [Theory]
    [InlineData("tiles")]
    [InlineData("tiles/props/rock.glb")]
    [InlineData("./tiles/rock.glb")]
    [InlineData("kit/../tiles/00_00/rock.glb")]
    [InlineData("map.json")]
    [InlineData("map.json.tmp")]
    [InlineData(".mapdoc-save.lock")]
    public void NativeStorage_TiledFormReservesWriterOwnedPaths(string relative)
    {
        string full = Path.GetFullPath(relative, _root);
        Assert.True(MapDocumentStorage.IsReserved(_root, MapDocumentForm.Tiled, full));
        Assert.True(MapDocumentStorage.IsReserved(_root + Path.DirectorySeparatorChar, MapDocumentForm.Tiled, full));
    }

    [Theory]
    [InlineData("kit/tiles/rock.glb")]
    [InlineData("tilesets/rock.glb")]
    [InlineData("map.json.bak")]
    [InlineData("kit/map.json")]
    [InlineData("../outside/tiles/rock.glb")]
    public void NativeStorage_TiledFormAllowsPathsOutsideItsNamespace(string relative)
    {
        Assert.False(MapDocumentStorage.IsReserved(_root, MapDocumentForm.Tiled, Path.GetFullPath(relative, _root)));
    }

    [Fact]
    public void NativeStorage_CaseFollowsTheExistingPlatformPathPolicy()
    {
        bool insensitive = !OperatingSystem.IsLinux();
        Assert.Equal(insensitive, MapDocumentStorage.IsReserved(_root, MapDocumentForm.Tiled, Path.Combine(_root, "Tiles", "rock.glb")));
        Assert.Equal(insensitive, MapDocumentStorage.IsReserved(_root, MapDocumentForm.Tiled, Path.Combine(_root, "MAP.JSON")));
    }

    [Fact]
    public void NativeStorage_MonolithicFormReservesOnlyTheDocumentFile()
    {
        string document = Path.Combine(_root, "world.map.json");
        Assert.Equal(_root, MapDocumentStorage.ResourceRoot(document, MapDocumentForm.Monolithic));
        Assert.Equal(_root, MapDocumentStorage.ResourceRoot(_root, MapDocumentForm.Tiled));
        Assert.True(MapDocumentStorage.IsReserved(document, MapDocumentForm.Monolithic, Path.Combine(_root, "kit", "..", "world.map.json")));
        Assert.False(MapDocumentStorage.IsReserved(document, MapDocumentForm.Monolithic, Path.Combine(_root, "tiles", "rock.glb")));
        Assert.False(MapDocumentStorage.IsReserved(document, MapDocumentForm.Monolithic, Path.Combine(_root, "map.json")));
        Assert.Throws<ArgumentException>(() => MapDocumentStorage.ResourceRoot(document, MapDocumentForm.None));
    }

    [Fact]
    public void NativeStorage_OnlyAManifestMakesADirectoryHoldATiledDocument()
    {
        Assert.False(MapDocumentStorage.HoldsTiledDocument(Path.Combine(_root, "missing")));
        Assert.False(MapDocumentStorage.HoldsTiledDocument(_root));
        Directory.CreateDirectory(Path.Combine(_root, "kit"));
        File.WriteAllText(Path.Combine(_root, "kit", "mesh.bin"), "prepared resource");
        Assert.False(MapDocumentStorage.HoldsTiledDocument(_root));
        Assert.Equal(MapDocumentForm.Tiled, MapDocumentFile.DetectForm(_root));
        var doc = new MapDocument { Id = "held", Bounds = new() { MinX = -10, MinZ = -10, MaxX = 10, MaxZ = 10 } };
        doc.Terrain.Biomes.Add(new MapBiomeBand());
        MapDocumentFile.SaveTiled(doc, _root);
        Assert.True(MapDocumentStorage.HoldsTiledDocument(_root + Path.DirectorySeparatorChar));
    }

    [Theory]
    [InlineData("root", false)]
    [InlineData("mesh", false)]
    [InlineData("unused", false)]
    [InlineData("dependency", false)]
    [InlineData("mesh", true)]
    public void NativeStorage_GuardRefusesEveryClosureReferenceBeforeReadingIt(string reserved, bool absolute)
    {
        var roots = WriteClosure(reserved, absolute);
        string reservedFile = Path.Combine(_root, "tiles", reserved + ".bin");
        var ex = Assert.Throws<MapDocumentException>(() =>
            MapAssetClosure.Load(roots, new MapStorageGuardedAssetSource(_root, MapDocumentForm.Tiled)));
        Assert.Contains("reserved", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"'{reserved}'", ex.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(reservedFile));
    }

    [Fact]
    public void NativeStorage_GuardLoadsReferencesOutsideTheReservedNamespace()
    {
        var clean = WriteClosure(reserved: null, absolute: false);
        var closure = MapAssetClosure.Load(clean, new MapStorageGuardedAssetSource(_root, MapDocumentForm.Tiled));
        Assert.Equal("kit/mesh.bin", closure.GetResource("mesh").Reference.Path);

        // A monolithic document beside a tiles folder does not own it.
        var monolithic = WriteClosure("mesh", absolute: false);
        string document = Path.Combine(_root, "world.map.json");
        Assert.NotNull(MapAssetClosure.Load(monolithic, new MapStorageGuardedAssetSource(document, MapDocumentForm.Monolithic)));
    }

    // Absolute Windows references carry backslashes and a quote is the other character JSON must escape. The
    // closure manifest must hand every reference to the source exactly, or no storage guard sees the real path.
    // The source is in memory, so no platform has to create a file with these names.
    [Fact]
    public void NativeStorage_ClosureManifestCarriesBackslashAndQuotePathsExactly()
    {
        var source = new PathKeyedSource();
        MapAssetRef mesh = source.Add("mesh", @"C:\Users\runner\tiles\mesh.bin", "mesh bytes");
        MapAssetRef dependency = source.Add("dependency", @"\\server\share\kit\say ""hi"".bin", "material bytes");
        MapAssetRef unused = source.Add("unused", @"kit\unused.bin", "spare bytes");
        MapAssetRef root = source.Add("root", @"C:\Users\runner\kit\root.json", Manifest(mesh, dependency, unused));

        var closure = MapAssetClosure.Load(new[] { root }, source);

        Assert.Equal(mesh, closure.GetResource("mesh").Reference);
        Assert.Equal(dependency, closure.GetResource("dependency").Reference);
        Assert.Equal(unused, closure.GetResource("unused").Reference);
        Assert.Equal(MapResourceKind.Mesh, closure.GetResource("mesh").Kind);
        Assert.Equal(new[] { "dependency" }, closure.GetResource("mesh").Dependencies);
        Assert.Equal(MapResourceKind.Material, closure.GetResource("dependency").Kind);
        Assert.Empty(closure.GetResource("dependency").Dependencies);
        Assert.Equal(new[] { root.Path, dependency.Path, mesh.Path, unused.Path }.Order(StringComparer.Ordinal),
            source.Reads.Order(StringComparer.Ordinal));
    }

    // A root manifest with one asset whose mesh depends on a material, plus one declared but unused resource.
    // The reference named by reserved lives under tiles/, every other one under kit/.
    IReadOnlyList<MapAssetRef> WriteClosure(string? reserved, bool absolute)
    {
        MapAssetRef Write(string id, string text)
        {
            string relative = (id == reserved ? "tiles/" : "kit/") + id + ".bin";
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            string full = Path.Combine(_root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
            return new(id, absolute ? full : relative, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), 1);
        }

        MapAssetRef mesh = Write("mesh", "mesh bytes");
        MapAssetRef dependency = Write("dependency", "material bytes");
        MapAssetRef unused = Write("unused", "spare bytes");
        return new[] { Write("root", Manifest(mesh, dependency, unused)) };
    }

    // The JSON writer serializes every reference value, so absolute Windows paths stay valid JSON strings.
    static string Manifest(MapAssetRef mesh, MapAssetRef dependency, MapAssetRef unused)
    {
        JsonObject manifest = JsonNode.Parse("""
            {"payloadVersion":1,"assets":[
              {"id":"prop","meshResourceId":"mesh","supportResourceIds":[],"materialResourceIds":[],
               "lodResourceIds":[],"lightResourceIds":[],"sourceUnitsToMetres":1,
               "renderBounds":{"min":{"x":-1,"y":0,"z":-1},"max":{"x":1,"y":2,"z":1}},
               "source":"authored","license":"CC0","textured":false}]}
            """)!.AsObject();
        manifest["resources"] = new JsonArray(
            NativeAssetFixtures.Resource(mesh, "Mesh", "dependency"),
            NativeAssetFixtures.Resource(dependency, "Material"),
            NativeAssetFixtures.Resource(unused, "Material"));
        return manifest.ToJsonString();
    }

    // Serves bytes by exact reference path, so a path altered on its way through the manifest cannot be read.
    sealed class PathKeyedSource : IMapAssetSource
    {
        readonly Dictionary<string, byte[]> _bytes = new(StringComparer.Ordinal);
        public List<string> Reads { get; } = new();

        public MapAssetRef Add(string id, string path, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            _bytes.Add(path, bytes);
            return new(id, path, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), 1);
        }

        public ReadOnlyMemory<byte> Read(MapAssetRef reference)
        {
            Reads.Add(reference.Path);
            return _bytes[reference.Path];
        }
    }
}
