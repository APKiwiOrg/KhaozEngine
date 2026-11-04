using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using KhaozEngine.TileEdit;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileEdit;

/// <summary>The format-preserving catalog writer: it changes the named archetype entries' <c>collisionHeight</c>
/// and nothing else, so a hand-authored catalog keeps its layout, comments and line endings, and a catalog it did
/// not change is never rewritten.</summary>
public class CatalogWriterTests
{
    // The layout a game ships today: one archetype per line, LF endings, a $schema line.
    static readonly string OneLine = Lf("""
        {
          "$schema": "https://khaozengine.dev/schemas/tileworld.catalog.schema.json",
          "archetypes": [
            { "id": "wall", "name": "Wall", "meshRef": "kit/wall.glb", "sizeX": 1, "sizeZ": 1, "collisionKind": "Wall", "tags": ["no-examine", "blocks-camera"] },
            { "id": "tree", "name": "Tree", "meshRef": "kit/tree.glb", "lodMeshRef": "kit/lod/tree.glb", "sizeX": 1, "sizeZ": 1, "collisionKind": "Solid", "interactive": true, "tags": ["tree"] },
            { "id": "bridge", "name": "Timber bridge", "meshRef": "kit/bridge.glb", "sizeX": 3, "sizeZ": 3, "collisionKind": "None", "tags": ["bridge"], "walkSurfaces": [{ "height": 0.825, "minX": -2.5, "maxX": 2.5 }] },
            { "id": "rug", "name": "Rug", "meshRef": "kit/rug.glb" }
          ]
        }

        """);

    // Everything the loader tolerates that a re-serialiser would lose: a byte order mark, CRLF endings, tab
    // indentation, line and block comments, trailing commas and a number written with a redundant zero.
    static readonly string Commented = Lf("""
        // Props, hand authored.
        {
        	"materials": [ { "id": 1, "name": "grass", "color": "#4d8a3a" } ],
        	"archetypes": [
        		{
        			"id": "crate", // the plain one
        			"name": "Crate",
        			"meshRef": "kit/crate.glb",
        			"collisionKind": "Solid",
        			/* tags follow */
        			"tags": [ "dressing" ]
        		},
        		{
        			"id": "fence",
        			"name": "Fence",
        			"meshRef": "kit/fence.glb",
        			"collisionKind": "Wall"
        		},
        		{
        			"id": "pillar",
        			"name": "Pillar",
        			"meshRef": "kit/pillar.glb",
        			"collisionKind": "Solid",
        			"collisionHeight": 2.50,
        		},
        	],
        }

        """).Replace("\n", "\r\n", StringComparison.Ordinal);

    static readonly IReadOnlyDictionary<string, float> Nothing = new Dictionary<string, float>();

    [Fact]
    public void AnUnchangedCatalogWritesBackByteIdentical()
    {
        using var temp = new TempDir();
        foreach ((string name, byte[] bytes) in Fixtures())
        {
            Assert.Equal(bytes, CatalogWriter.SetCollisionHeights(bytes, Nothing));

            string path = temp.Sub(name);
            File.WriteAllBytes(path, bytes);
            DateTime before = File.GetLastWriteTimeUtc(path);
            Assert.False(CatalogWriter.WriteCollisionHeights(path, Nothing));
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Equal(before, File.GetLastWriteTimeUtc(path));
        }

        // A height already written in its shortest form, set again to the same value, changes no byte either, so
        // the file is not rewritten.
        byte[] pillar = Encoding.UTF8.GetBytes(Commented.Replace("2.50", "2.5", StringComparison.Ordinal));
        string again = temp.Sub("again.json");
        File.WriteAllBytes(again, pillar);
        Assert.False(CatalogWriter.WriteCollisionHeights(again, new Dictionary<string, float> { ["pillar"] = 2.5f }));
        Assert.Equal(pillar, File.ReadAllBytes(again));
    }

    [Fact]
    public void SettingAHeightAddsOnlyThatProperty()
    {
        using var temp = new TempDir();

        // One line per archetype: the new property follows collisionKind with the entry's own ", " separator.
        string oneLine = temp.Sub("one-line.json");
        File.WriteAllText(oneLine, OneLine);
        Assert.True(CatalogWriter.WriteCollisionHeights(oneLine, new Dictionary<string, float> { ["wall"] = 2.5f }));
        string expected = OneLine.Replace("\"collisionKind\": \"Wall\", \"tags\"",
            "\"collisionKind\": \"Wall\", \"collisionHeight\": 2.5, \"tags\"", StringComparison.Ordinal);
        Assert.NotEqual(OneLine, expected);
        Assert.Equal(expected, File.ReadAllText(oneLine));

        // One property per line, CRLF, tabs, comments and a BOM: the new property takes collisionKind's line break
        // and indentation, the comments stay where they were, and the BOM survives.
        string commented = temp.Sub("commented.json");
        File.WriteAllBytes(commented, WithBom(Commented));
        Assert.True(CatalogWriter.WriteCollisionHeights(commented,
            new Dictionary<string, float> { ["crate"] = 1.75f, ["fence"] = 1.2f }));
        string expectedCommented = Commented
            .Replace("\"collisionKind\": \"Solid\",\r\n\t\t\t/* tags follow */",
                "\"collisionKind\": \"Solid\",\r\n\t\t\t\"collisionHeight\": 1.75,\r\n\t\t\t/* tags follow */",
                StringComparison.Ordinal)
            .Replace("\"collisionKind\": \"Wall\"\r\n",
                "\"collisionKind\": \"Wall\",\r\n\t\t\t\"collisionHeight\": 1.2\r\n", StringComparison.Ordinal);
        Assert.Equal(WithBom(expectedCommented), File.ReadAllBytes(commented));

        // Both files load through the engine loader, schema check included, and carry exactly the heights set.
        TileWorldCatalogs loaded = TileWorldCatalogs.Load(new[] { oneLine, commented });
        Assert.Equal(2.5f, loaded.Archetype("wall")!.CollisionHeight);
        Assert.Equal(1.75f, loaded.Archetype("crate")!.CollisionHeight);
        Assert.Equal(1.2f, loaded.Archetype("fence")!.CollisionHeight);
        Assert.Equal(2.5f, loaded.Archetype("pillar")!.CollisionHeight);
        Assert.Null(loaded.Archetype("tree")!.CollisionHeight);
        Assert.Null(loaded.Archetype("bridge")!.CollisionHeight);
        Assert.Equal(new[] { "dressing" }, loaded.Archetype("crate")!.Tags);
    }

    // A // comment ending collisionKind's line annotates collisionKind, so the new property takes the next line and
    // the comment stays where it was. Without a comma after the value (the entry's last property) one is added
    // before the comment.
    [Fact]
    public void ALineCommentAfterCollisionKindStaysWithIt()
    {
        string before = Lf("""
            {
            	"archetypes": [
            		{
            			"id": "gate",
            			"name": "Gate",
            			"meshRef": "kit/gate.glb",
            			"collisionKind": "Wall", // swings open
            			"tags": [ "door" ]
            		},
            		{
            			"id": "post",
            			"name": "Post",
            			"meshRef": "kit/post.glb",
            			"collisionKind": "Solid" // the last property
            		},
            		{
            			"id": "stile",
            			"name": "Stile",
            			"meshRef": "kit/stile.glb",
            			"collisionKind": "Wall", // trailing comma
            		}
            	]
            }

            """).Replace("\n", "\r\n", StringComparison.Ordinal);
        byte[] after = CatalogWriter.SetCollisionHeights(Encoding.UTF8.GetBytes(before),
            new Dictionary<string, float> { ["gate"] = 2f, ["post"] = 1.5f, ["stile"] = 1.25f });

        string expected = before
            .Replace("\"collisionKind\": \"Wall\", // swings open\r\n",
                "\"collisionKind\": \"Wall\", // swings open\r\n\t\t\t\"collisionHeight\": 2,\r\n", StringComparison.Ordinal)
            .Replace("\"collisionKind\": \"Solid\" // the last property\r\n",
                "\"collisionKind\": \"Solid\", // the last property\r\n\t\t\t\"collisionHeight\": 1.5\r\n", StringComparison.Ordinal)
            .Replace("\"collisionKind\": \"Wall\", // trailing comma\r\n",
                "\"collisionKind\": \"Wall\", // trailing comma\r\n\t\t\t\"collisionHeight\": 1.25\r\n", StringComparison.Ordinal);
        Assert.Equal(expected, Encoding.UTF8.GetString(after));

        TileWorldCatalogs loaded = TileWorldCatalogs.LoadJson(Encoding.UTF8.GetString(after), "commented.json");
        Assert.Equal(2f, loaded.Archetype("gate")!.CollisionHeight);
        Assert.Equal(1.5f, loaded.Archetype("post")!.CollisionHeight);
        Assert.Equal(1.25f, loaded.Archetype("stile")!.CollisionHeight);
        Assert.Equal(new[] { "door" }, loaded.Archetype("gate")!.Tags);
    }

    [Fact]
    public void ReplacingAHeightChangesOnlyItsValueText()
    {
        byte[] before = WithBom(Commented);
        byte[] after = CatalogWriter.SetCollisionHeights(before, new Dictionary<string, float> { ["pillar"] = 3.25f });
        Assert.Equal(WithBom(Commented.Replace("\"collisionHeight\": 2.50,", "\"collisionHeight\": 3.25,",
            StringComparison.Ordinal)), after);
    }

    [Fact]
    public void AnArchetypeWithNoCollisionKindTakesTheHeightAsItsLastProperty()
    {
        byte[] after = CatalogWriter.SetCollisionHeights(Encoding.UTF8.GetBytes(OneLine),
            new Dictionary<string, float> { ["rug"] = 0.05f });
        Assert.Equal(OneLine.Replace("\"meshRef\": \"kit/rug.glb\" }",
            "\"meshRef\": \"kit/rug.glb\", \"collisionHeight\": 0.05 }", StringComparison.Ordinal),
            Encoding.UTF8.GetString(after));
    }

    [Theory]
    [InlineData(2.5f, "2.5")]
    [InlineData(3f, "3")]
    [InlineData(0.1f, "0.1")]
    [InlineData(1f / 3f, "0.33333334")]
    [InlineData(2.4999998f, "2.4999998")]
    [InlineData(1e-5f, "1E-05")]
    public void AHeightIsWrittenInItsShortestInvariantRoundTripForm(float height, string text)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            // A decimal comma culture, so a culture-sensitive format would write "2,5" and break the JSON.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal(text, CatalogWriter.FormatHeight(height));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
        Assert.Equal(height, float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture));

        byte[] after = CatalogWriter.SetCollisionHeights(Encoding.UTF8.GetBytes(OneLine),
            new Dictionary<string, float> { ["tree"] = height });
        Assert.Equal(height, TileWorldCatalogs.LoadJson(Encoding.UTF8.GetString(after), "t").Archetype("tree")!.CollisionHeight);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void TheWriterRefusesAHeightTheLoaderWouldReject(float height)
    {
        var ex = Assert.Throws<CatalogEditRangeException>(() => CatalogWriter.SetCollisionHeights(
            Encoding.UTF8.GetBytes(OneLine), new Dictionary<string, float> { ["wall"] = height }));
        Assert.IsAssignableFrom<ArgumentOutOfRangeException>(ex);
        Assert.Equal(TileWorldRefusal.CatalogEditHeightInvalid, ex.Reason);
        Assert.Equal("wall", ex.ArchetypeId);
        Assert.Equal("heights", ex.ParamName);
        Assert.Equal(height, ex.ActualValue);
    }

    [Fact]
    public void TheWriterRefusesAnIdTheCatalogDoesNotDefine()
    {
        using var temp = new TempDir();
        string path = temp.Sub("one-line.json");
        File.WriteAllText(path, OneLine);
        var ex = Assert.Throws<TileWorldException>(() => CatalogWriter.WriteCollisionHeights(path,
            new Dictionary<string, float> { ["wall"] = 2f, ["ghost"] = 1f, ["absent"] = 3f }));
        Assert.Equal(TileWorldRefusal.CatalogEditArchetypesMissing, ex.Reason);
        Assert.Equal(path, ex.SourceName);
        Assert.Equal(new[] { "absent", "ghost" }, ex.ArchetypeIds);
        var inner = Assert.IsType<TileWorldException>(ex.InnerException);
        Assert.Equal(TileWorldRefusal.CatalogEditArchetypesMissing, inner.Reason);
        Assert.Equal(new[] { "absent", "ghost" }, inner.ArchetypeIds);
        Assert.Null(inner.SourceName);
        Assert.Equal(OneLine, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("{]", TileWorldRefusal.CatalogEditJsonMalformed, null, null)]
    [InlineData("[]", TileWorldRefusal.CatalogEditNotObject, null, null)]
    [InlineData("{\"archetypes\":[{\"id\":\"wall\"},{\"id\":\"wall\"}]}",
        TileWorldRefusal.CatalogEditArchetypeDuplicate, "wall", null)]
    [InlineData("{\"archetypes\":[{\"id\":\"wall\",\"collisionHeight\":1,\"collisionHeight\":2}]}",
        TileWorldRefusal.CatalogEditHeightDuplicate, "wall", "collisionHeight")]
    public void CatalogEditRefusalsKeepTheirReasonAndIdentifiersThroughPrepare(
        string catalog, TileWorldRefusal reason, string? archetypeId, string? field)
    {
        var heights = new Dictionary<string, float> { ["wall"] = 2f };
        byte[] bytes = Encoding.UTF8.GetBytes(catalog);
        var direct = Assert.Throws<TileWorldException>(() => CatalogWriter.SetCollisionHeights(bytes, heights));
        Assert.Equal(reason, direct.Reason);
        Assert.Equal(archetypeId, direct.ArchetypeId);
        Assert.Equal(field, direct.Field);
        Assert.Null(direct.SourceName);
        if (reason == TileWorldRefusal.CatalogEditJsonMalformed)
            Assert.IsAssignableFrom<JsonException>(direct.InnerException);

        using var temp = new TempDir();
        string path = temp.Sub("refused.json");
        File.WriteAllBytes(path, bytes);
        var wrapped = Assert.Throws<TileWorldException>(() => CatalogWriter.Prepare(path, heights));
        Assert.Equal(reason, wrapped.Reason);
        Assert.Equal(path, wrapped.SourceName);
        Assert.Equal(archetypeId, wrapped.ArchetypeId);
        Assert.Equal(field, wrapped.Field);
        var inner = Assert.IsType<TileWorldException>(wrapped.InnerException);
        Assert.Equal(reason, inner.Reason);
        Assert.Equal(archetypeId, inner.ArchetypeId);
        Assert.Equal(field, inner.Field);
        Assert.Null(inner.SourceName);
        if (reason == TileWorldRefusal.CatalogEditJsonMalformed)
            Assert.IsAssignableFrom<JsonException>(inner.InnerException);
        Assert.Equal(bytes, File.ReadAllBytes(path));

        File.WriteAllText(path, OneLine);
        Assert.NotNull(CatalogWriter.Prepare(path, heights));
        Assert.Equal(OneLine, File.ReadAllText(path));
    }

    [Fact]
    public void PrepareIdentifiesAnUnreadableCatalogAndRetainsItsCause()
    {
        using var temp = new TempDir();
        string path = temp.Sub("missing.json");
        var ex = Assert.Throws<TileWorldException>(() => CatalogWriter.Prepare(path, Nothing));
        Assert.Equal(TileWorldRefusal.CatalogEditReadFailed, ex.Reason);
        Assert.Equal(path, ex.SourceName);
        Assert.NotNull(ex.InnerException);
        Assert.False(File.Exists(path));

        File.WriteAllText(path, OneLine);
        Assert.Null(CatalogWriter.Prepare(path, Nothing));
        Assert.Equal(OneLine, File.ReadAllText(path));
    }

    [Fact]
    public void MissingArchetypeIdentifiersAreOwnedReadOnlySnapshots()
    {
        var ids = new List<string> { "absent", "ghost" };
        var ex = new TileWorldException(TileWorldRefusal.CatalogEditArchetypesMissing, "missing archetypes")
        { ArchetypeIds = ids };
        ids[0] = "changed";
        ids.Clear();
        Assert.Equal(new[] { "absent", "ghost" }, ex.ArchetypeIds);
        var exposed = Assert.IsAssignableFrom<IList<string>>(ex.ArchetypeIds);
        Assert.Throws<NotSupportedException>(() => exposed[0] = "changed");
        Assert.Equal(new[] { "absent", "ghost" }, ex.ArchetypeIds);
    }

    static IEnumerable<(string Name, byte[] Bytes)> Fixtures()
    {
        yield return ("one-line.json", Encoding.UTF8.GetBytes(OneLine));
        yield return ("commented.json", WithBom(Commented));
        yield return ("commented-no-bom.json", Encoding.UTF8.GetBytes(Commented));
        yield return ("tool-fixture.json", Encoding.UTF8.GetBytes(TileEditTestWorld.CatalogJson));
    }

    static byte[] WithBom(string text) => Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();

    // Raw string literals carry the source file's line endings, so every fixture is normalised to LF first and
    // the CRLF one is built from that, whatever the checkout did to this file.
    static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);
}
