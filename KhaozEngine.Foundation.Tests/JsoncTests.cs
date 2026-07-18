using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.Content;
using KhaozEngine.Ecs;
using KhaozEngine.Serialization;
using Xunit;

namespace KhaozEngine.Tests;

public class JsoncTests
{
    private sealed class Config
    {
        public string Name { get; set; } = "";
        public int Count { get; set; }
    }

    private const string JsoncSample = """
    {
        // line comment
        "name": "hello",
        /* block comment */
        "COUNT": 3,
    }
    """;

    [Fact]
    public void Deserialize_AcceptsComments_TrailingCommas_AndCaseInsensitiveNames()
    {
        Config? cfg = Jsonc.Deserialize<Config>(JsoncSample);

        Assert.NotNull(cfg);
        Assert.Equal("hello", cfg!.Name);
        Assert.Equal(3, cfg.Count);
    }

    [Fact]
    public void DeserializeFile_ReadsJsoncFromDisk()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ke-jsonc-{System.Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsoncSample);
        try
        {
            Config? cfg = Jsonc.DeserializeFile<Config>(path);
            Assert.NotNull(cfg);
            Assert.Equal("hello", cfg!.Name);
            Assert.Equal(3, cfg.Count);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ParseDocument_AcceptsComments_AndTrailingCommas()
    {
        using JsonDocument doc = Jsonc.ParseDocument(JsoncSample);
        Assert.Equal("hello", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("COUNT").GetInt32());
    }

    [Fact]
    public void ParseNode_AcceptsComments_AndTrailingCommas()
    {
        JsonNode? node = Jsonc.ParseNode(JsoncSample);
        Assert.NotNull(node);
        Assert.Equal("hello", node!["name"]!.GetValue<string>());
    }

    [Fact]
    public void ParseNode_ReturnsNull_ForLiteralNull()
    {
        Assert.Null(Jsonc.ParseNode("null"));
    }

    [Fact]
    public void TolerantRead_IsTheSameInstanceAsJsoncOptions()
    {
        // The historical name and the canonical name must be one shared, frozen instance, so every
        // existing caller of JsonDefaults.TolerantRead routes through the one JSONC policy.
        Assert.Same(Jsonc.Options, JsonDefaults.TolerantRead);
    }

    [Fact]
    public void Options_AreConfiguredForJsonc()
    {
        Assert.Equal(JsonCommentHandling.Skip, Jsonc.Options.ReadCommentHandling);
        Assert.True(Jsonc.Options.AllowTrailingCommas);
        Assert.True(Jsonc.Options.PropertyNameCaseInsensitive);
    }

    // --- Routed call sites: prove JSONC reaches the consumers, not just the policy object ---

    private struct JcHealth : IComponent { public int Hp; }

    [Fact]
    public void WorldSerializer_LoadsSaveWithCommentsAndTrailingCommas()
    {
        // A hand-edited save with a comment and a trailing comma must still load.
        var w = new World();
        Entity e = w.Spawn();
        w.Set(e, new JcHealth { Hp = 42 });
        var ser = new WorldSerializer(typeof(JcHealth));

        string saved = ser.Save(w);
        string edited = "// hand-edited save\n" + saved.TrimEnd().TrimEnd('}') + ",\n}";

        World loaded = ser.Load(edited);
        Assert.True(loaded.IsAlive(e));
        Assert.Equal(42, loaded.Get<JcHealth>(e).Hp);
    }

    [Fact]
    public void JsonSchemaValidator_AcceptsJsoncInstance()
    {
        const string schema = """
            { "$schema":"https://json-schema.org/draft/2020-12/schema",
              "type":"object","required":["name","count"],
              "properties":{ "name":{"type":"string"}, "count":{"type":"integer"} } }
            """;
        const string instance = """
            {
                // a comment a strict parser would reject
                "name": "x",
                "count": 1,
            }
            """;

        Assert.True(JsonSchemaValidator.Validate(instance, schema).IsValid);
    }
}
