using System;
using System.IO;
using KhaozEngine.ShaderBuild;
using Xunit;

namespace KhaozEngine.Tests.ShaderBuild;

public sealed class ShaderManifestTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "khaozengine-shader-manifest-tests", Guid.NewGuid().ToString("N"));

    public ShaderManifestTests()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "sprite.slang"), "");
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Reads_programs_and_gives_an_omitted_variant_list_one_unnamed_variant()
    {
        var manifest = ShaderManifest.Load(Write("""
            { "programs": [
              { "id": "sprite", "source": "sprite.slang", "vertex": "vertexMain", "fragment": "fragmentMain" },
              { "id": "masked", "source": "sprite.slang", "vertex": "vertexMain", "fragment": "fragmentMain",
                "vertexPreciseMath": true,
                "variants": [ { "name": "soft", "defines": { "SOFT": "1" } } ] }
            ] }
            """));

        var sprite = manifest.Programs[0];
        Assert.Equal(("sprite", "sprite.slang", "vertexMain", "fragmentMain", false), (sprite.Id, sprite.Source, sprite.Vertex, sprite.Fragment, sprite.VertexPreciseMath));
        var unnamed = Assert.Single(sprite.Variants);
        Assert.Equal("", unnamed.Name);
        Assert.Empty(unnamed.Defines);

        var masked = manifest.Programs[1];
        Assert.True(masked.VertexPreciseMath);
        var soft = Assert.Single(masked.Variants);
        Assert.Equal("soft", soft.Name);
        Assert.Equal("1", soft.Defines["SOFT"]);
    }

    [Theory]
    [InlineData("""{ "programs": [ { "id": "sprite", "source": "sprite.slang", "vertex": "vertexMain" } ] }""")]
    [InlineData("""{ "programs": [ { "id": "sprite", "source": "../sprite.slang", "vertex": "vertexMain", "fragment": "fragmentMain" } ] }""")]
    [InlineData("""{ "programs": [ { "id": "sprite", "source": "missing.slang", "vertex": "vertexMain", "fragment": "fragmentMain" } ] }""")]
    [InlineData("""{ "programs": [ { "id": "sprite", "source": "sprite.slang", "vertex": "-o", "fragment": "fragmentMain" } ] }""")]
    [InlineData("""{ "programs": [ { "id": "sprite", "source": "sprite.slang", "vertex": "vertexMain", "fragment": "fragmentMain", "variants": [ { "name": "a", "defines": {} }, { "name": "a", "defines": {} } ] } ] }""")]
    [InlineData("""{ "programs": [ { "id": "sprite", "source": "sprite.slang", "vertex": "vertexMain", "fragment": "fragmentMain", "variants": [ { "name": "a", "defines": { "SOFT": 1 } } ] } ] }""")]
    [InlineData("""{ "programs": [ { "id": "sprite", "source": "sprite.slang", "vertex": "vertexMain", "fragment": "fragmentMain", "variants": [ { "name": "a", "defines": { "-o": "x" } } ] } ] }""")]
    [InlineData("""{ "programs": [ { "id": "sprite", "source": "sprite.slang", "vertex": "vertexMain", "fragment": "fragmentMain" }, { "id": "sprite", "source": "sprite.slang", "vertex": "vertexMain", "fragment": "fragmentMain" } ] }""")]
    [InlineData("""{ "programs": [ """)]
    public void Refuses_an_invalid_manifest(string json)
    {
        var refusal = Assert.Throws<ShaderBuildException>(() => ShaderManifest.Load(Write(json)));

        Assert.Equal(ShaderBuildFailure.ManifestInvalid, refusal.Failure);
    }

    private string Write(string json)
    {
        var path = Path.Combine(_folder, "programs.json");
        File.WriteAllText(path, json);
        return path;
    }
}
