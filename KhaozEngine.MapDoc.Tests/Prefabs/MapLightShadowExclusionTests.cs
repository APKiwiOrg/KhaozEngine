using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public sealed class MapLightShadowExclusionTests
{
    [Theory]
    [InlineData("""{"kind":"None"}""", false)]
    [InlineData("""{"kind":"NearSphere","radius":0.625}""", false)]
    [InlineData("""{"kind":"FixtureBox","min":{"x":-1,"y":-2,"z":-3},"max":{"x":1,"y":2,"z":3},"margin":0.125}""", false)]
    [InlineData("""{"kind":"None"}""", true)]
    [InlineData("""{"kind":"NearSphere","radius":0.625}""", true)]
    [InlineData("""{"kind":"FixtureBox","min":{"x":-1,"y":-2,"z":-3},"max":{"x":1,"y":2,"z":3},"margin":0.125}""", true)]
    public void AuthoredLight_RoundTripsBothFormsAndEffectiveAnnotations(string declaration, bool tiled)
    {
        var light = Read(declaration);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(declaration), JsonNode.Parse(MapLightFile.Write(light))!["shadowExclusion"]));
        var fixture = PrefabFixtures.NativeOwnedLight(.371f, 2, false);
        fixture.Document.BuildingIntent = new(1, [], [], [light with { Owner = PrefabFixtures.World(MapPrefabChildKind.Placement, "owner") }], []);
        string before = fixture.Document.BuildingIntent.Digest();
        string path = Path.Combine(Path.GetTempPath(), "khaozengine-light-exclusion-" + Guid.NewGuid().ToString("N"));
        try
        {
            MapDocumentFile.SaveAs(fixture.Document, path, tiled ? MapDocumentForm.Tiled : MapDocumentForm.Monolithic);
            var reloaded = MapDocumentFile.Load(path);
            Assert.Equal(before, reloaded.BuildingIntent!.Digest());
            var effective = Assert.Single(MapEffectiveAnnotations.Lights(PrefabFixtures.Effective(new(reloaded, fixture.Assets))));
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(declaration), JsonNode.Parse(MapPrefabJson.Bytes(effective))!["shadowExclusion"]));
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData("""{"kind":"NearSphere","radius":-1}""")]
    [InlineData("""{"kind":"NearSphere","radius":1e100}""")]
    [InlineData("""{"kind":"NearSphere"}""")]
    [InlineData("""{"kind":"None","radius":1}""")]
    [InlineData("""{"kind":"FixtureBox","min":{"x":1,"y":0,"z":0},"max":{"x":0,"y":1,"z":1},"margin":0}""")]
    [InlineData("""{"kind":"FixtureBox","min":{"x":0,"y":1,"z":0},"max":{"x":1,"y":0,"z":1},"margin":0}""")]
    [InlineData("""{"kind":"FixtureBox","min":{"x":0,"y":0,"z":1},"max":{"x":1,"y":1,"z":0},"margin":0}""")]
    [InlineData("""{"kind":"FixtureBox","min":{"x":0,"y":0,"z":0},"max":{"x":0,"y":1,"z":1},"margin":0}""")]
    [InlineData("""{"kind":"FixtureBox","min":{"x":1e100,"y":0,"z":0},"max":{"x":1,"y":1,"z":1},"margin":0}""")]
    [InlineData("""{"kind":"FixtureBox","min":{"x":0,"y":0,"z":0},"max":{"x":1,"y":1e100,"z":1},"margin":0}""")]
    [InlineData("""{"kind":"FixtureBox","min":{"x":0,"y":0,"z":0},"max":{"x":1,"y":1,"z":1},"margin":-1}""")]
    [InlineData("""{"kind":"FixtureBox","min":{"x":0,"y":0,"z":0},"max":{"x":1,"y":1,"z":1},"margin":1e100}""")]
    [InlineData("""{"kind":"FixtureBox","min":{"x":0,"y":0,"z":0},"max":{"x":1,"y":1,"z":1}}""")]
    public void InvalidExclusions_RefuseAtTheAuthoredBoundary(string declaration)
    {
        Assert.Throws<MapDocumentException>(() => Read(declaration));
    }

    [Fact]
    public void FixtureBox_RequiresAnOwner()
    {
        var error = Assert.Throws<MapDocumentException>(() => Read("""{"kind":"FixtureBox","min":{"x":0,"y":0,"z":0},"max":{"x":1,"y":1,"z":1},"margin":0}""", false));
        Assert.Contains("owner", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UndeclaredExclusion_PreservesReleasedLightBytes()
    {
        byte[] before = MapLightFile.Write(PrefabFixtures.GlowLight());
        Assert.DoesNotContain("shadowExclusion", Encoding.UTF8.GetString(before));
        Assert.Equal(before, MapLightFile.Write(MapLightFile.Read(before, "glow-light")));
    }

    static MapLocalLightDoc Read(string declaration, bool owner = true)
    {
        var json = JsonNode.Parse(MapLightFile.Write(PrefabFixtures.GlowLight()))!;
        json["shadowExclusion"] = JsonNode.Parse(declaration);
        if (!owner) json["owner"] = null;
        return MapLightFile.Read(Encoding.UTF8.GetBytes(json.ToJsonString()), "glow-light");
    }
}
