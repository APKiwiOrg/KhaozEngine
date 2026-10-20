using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Foliage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class MapJsonTextTests
{
    [Fact]
    public void IndentedJsonConfiguration_UsesSharedMapDocHome()
    {
        string root = RepositoryRoot();
        var violations = new List<string>();
        var assignment = new Regex(@"\b(?:WriteIndented|Indented)\s*=\s*(?<value>[^,;}\r\n]+)");
        foreach (string project in new[] { "KhaozEngine.MapDoc", "KhaozEngine.MapDoc.Physics", "KhaozEngine.MapEditor", "KhaozEngine.MapEdit.Tool" })
        {
            int scanned = 0;
            foreach (string path in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(root, path);
                if (relative.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj")) continue;
                scanned++;
                if (relative == Path.Combine("KhaozEngine.MapDoc", "MapJsonTextOptions.cs")) continue;
                foreach (Match match in assignment.Matches(File.ReadAllText(path)))
                    if (match.Groups["value"].Value.Trim() != "false") violations.Add(relative + ": " + match.Value);
            }
            Assert.True(scanned > 0, "No source files scanned in " + project);
        }
        Assert.True(violations.Count == 0,
            "Indented JSON must use the shared MapDoc newline policy:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void SaveText_AllResourceKindsAndFoliage_UsesOnlyLf()
    {
        MapDocument document = AllResourcesWithFoliage();
        string text = MapDocumentFile.SaveText(document);
        AssertLf(text);
        Assert.NotEmpty(MapDocumentFile.LoadText(text).Foliage!.Layers);
    }

    [Fact]
    public void SaveTo_AllResourceKindsAndFoliage_UsesOnlyLf()
    {
        MapDocument document = AllResourcesWithFoliage();
        using var stream = new MemoryStream();
        MapDocumentFile.SaveTo(document, stream);
        string text = Encoding.UTF8.GetString(stream.ToArray());
        AssertLf(text);
        Assert.Equal(MapDocumentFile.SaveText(document), text);
    }

    [Fact]
    public void SaveTiled_ManifestAndTileJson_UseOnlyLf()
    {
        InDirectory(root =>
        {
            MapDocumentFile.SaveTiled(AllResourcesWithFoliage(), root);
            AssertLf(File.ReadAllText(Path.Combine(root, "map.json")));
            foreach (string json in Directory.GetFiles(root, "*.json", SearchOption.AllDirectories))
                Assert.DoesNotContain("\r", File.ReadAllText(json), StringComparison.Ordinal);
            string[] tiles = Directory.GetFiles(Path.Combine(root, "tiles"), "t_*.json", SearchOption.AllDirectories);
            Assert.NotEmpty(tiles);
            foreach (string tile in tiles) AssertLf(File.ReadAllText(tile));
        });
    }

    [Fact]
    public void SaveTiled_BinaryFoliagePacks_PreserveEveryByteIncludingDensity13()
    {
        InDirectory(root =>
        {
            string first = Path.Combine(root, "first"), second = Path.Combine(root, "second");
            MapDocument original = AllResourcesWithFoliage();
            MapDocumentFile.SaveTiled(original, first);
            Dictionary<string, byte[]> before = Packs(first);
            Assert.NotEmpty(before);
            MapDocument reloaded = MapDocumentFile.LoadTiled(first);
            AssertDensity(original, reloaded);
            MapDocumentFile.SaveTiled(reloaded, second);
            Dictionary<string, byte[]> after = Packs(second);
            Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
            foreach (string path in before.Keys) Assert.Equal(before[path], after[path]);
            AssertDensity(original, MapDocumentFile.LoadTiled(second));
        });
    }

    [Theory]
    [InlineData(MapDocumentSchema.ManifestFileName)]
    [InlineData(MapDocumentSchema.TileFileName)]
    public void DerivedSchemaText_UsesOnlyLf(string name)
    {
        AssertLf(name == MapDocumentSchema.ManifestFileName ? MapDocumentSchema.GetManifestJson() : MapDocumentSchema.GetTileJson());
        InDirectory(root =>
        {
            MapDocumentSchema.WriteAllTo(root);
            AssertLf(File.ReadAllText(Path.Combine(root, name)));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SerializerOptions_PreserveEveryReleasedSettingExceptNewLine(bool indented)
    {
        var registry = MapDocRegistry.CreateDefault();
        var released = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            AllowOutOfOrderMetadataProperties = true,
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        MapNativeJson.Configure(released);
        released.Converters.Add(new MapRationalJsonConverter());
        released.Converters.Add(new MapPatchKeyJsonConverter());
        released.Converters.Add(new JsonStringEnumConverter());
        released.Converters.Add(new MapFeatureConverter(registry));
        JsonSerializerOptions actual = indented ? MapDocumentFile.CreateOptions(registry, write: true) : MapDocumentFile.CreateCompactOptions(registry);
        foreach (PropertyInfo property in typeof(JsonSerializerOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.Name == nameof(JsonSerializerOptions.NewLine)) Assert.Equal("\n", actual.NewLine);
            else if (property.Name == nameof(JsonSerializerOptions.Converters))
                Assert.Equal(released.Converters.Select(c => c.GetType()), actual.Converters.Select(c => c.GetType()));
            else if (property.Name == nameof(JsonSerializerOptions.TypeInfoResolverChain))
                Assert.Equal(released.TypeInfoResolverChain, actual.TypeInfoResolverChain);
            else Assert.True(Equals(property.GetValue(released), property.GetValue(actual)), "Changed serializer option: " + property.Name);
        }
    }

    static MapDocument AllResourcesWithFoliage()
    {
        MapDocument document = FoliageFixtures.Create().Document;
        var (roots, source) = FoliageFixtures.AssetInputs();
        MapAssetRef prefab = source.Add("lf-prefab", MapPrefabFile.Write(new(1, "lf-prefab", [])));
        MapAssetRef manifest = source.Add("lf-prefab-manifest", new JsonObject
        {
            ["payloadVersion"] = 1,
            ["assets"] = new JsonArray(),
            ["resources"] = new JsonArray(NativeAssetFixtures.Resource(prefab, "Prefab")),
        }.ToJsonString());
        document.NativeAssets = roots.Append(manifest).ToList();
        MapAssetClosure assets = MapAssetClosure.Load(document.NativeAssets, source);
        Assert.Equal(Enum.GetValues<MapResourceKind>().Order(), assets.CapturedResources.Select(r => r.Kind).Distinct().Order());
        Assert.Empty(MapFoliageValidation.ValidateBound(document, assets));
        document.Placements.Add(new MapPlacement { Id = "lf-stone", Kind = "stone", X = 1, Z = 1, Scale = 1 });
        return document;
    }

    static void AssertLf(string text)
    {
        Assert.Contains("\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
    }

    static Dictionary<string, byte[]> Packs(string root) => Directory.GetFiles(root, "*.pack", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(root, path), File.ReadAllBytes, StringComparer.Ordinal);

    static void AssertDensity(MapDocument expected, MapDocument actual)
    {
        MapFoliageLayer layer = Assert.Single(actual.Foliage!.Layers);
        Assert.Equal((byte)13, layer.DensityAt(13, 0));
        MapFoliageLayer original = Assert.Single(expected.Foliage!.Layers);
        Assert.Equal(original.Chunks.Select(c => c.Key), layer.Chunks.Select(c => c.Key));
        for (int i = 0; i < original.Chunks.Count; i++) Assert.Equal(original.Chunks[i].CopyDensity(), layer.Chunks[i].CopyDensity());
    }

    static void InDirectory(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "khaozengine-mapdoc-newlines-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally { Directory.Delete(root, recursive: true); }
    }

    static string RepositoryRoot([CallerFilePath] string path = "") => Path.GetDirectoryName(Path.GetDirectoryName(path))!;
}
