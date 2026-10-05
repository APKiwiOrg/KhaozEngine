using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

/// <summary>Format 3 documents load under the closed-member policy: an undeclared member is refused rather
/// than dropped, while a registry-open terrain feature keeps accepting fields of its own.</summary>
public sealed class LegacyLoadPolicyTests
{
    static JsonObject Root() => JsonNode.Parse(NativeFixtures.AnalyticV3Json())!.AsObject();

    [Fact]
    public void LegacyDocument_StillLoads()
    {
        MapDocument doc = MapDocumentFile.LoadText(Root().ToJsonString());
        Assert.Equal(MapDocumentFile.CurrentFormatVersion, doc.FormatVersion);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("bounds")]
    [InlineData("terrain")]
    [InlineData("placement")]
    public void LegacyDocument_UndeclaredMemberIsRefused(string target)
    {
        JsonObject root = Root();
        JsonObject modified = target switch
        {
            "bounds" => root["bounds"]!.AsObject(),
            "terrain" => root["terrain"]!.AsObject(),
            "placement" => root["placements"]![0]!.AsObject(),
            _ => root,
        };
        modified["notes"] = "kept nowhere";
        var error = Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadText(root.ToJsonString()));
        Assert.Contains("unknown property 'notes'", error.Message);
    }

    [Fact]
    public void LegacyDocument_TerrainFeatureMayCarryUndeclaredFields()
    {
        JsonObject root = Root();
        root["terrain"]!["features"] = new JsonArray(new JsonObject
        {
            ["type"] = "lake",
            ["centerX"] = 0,
            ["centerZ"] = 0,
            ["radius"] = 10,
            ["depth"] = 2,
            ["customGameField"] = "registry-owned",
        });
        MapDocument doc = MapDocumentFile.LoadText(root.ToJsonString());
        Assert.IsType<LakeFeatureDoc>(Assert.Single(doc.Terrain.Features));
    }
}
