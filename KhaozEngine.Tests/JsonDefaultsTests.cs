using System.Text.Json;
using KhaozEngine.Serialization;
using Xunit;

namespace KhaozEngine.Tests;

public class JsonDefaultsTests
{
    private sealed class Config
    {
        public string Name { get; set; } = "";
        public int Count { get; set; }
    }

    private struct FieldBag
    {
        public int X;
        public string Label;
    }

    [Fact]
    public void TolerantRead_AllowsComments_TrailingCommas_AndCaseInsensitiveNames()
    {
        const string json = """
        {
            // leading comment
            "name": "hello",
            "COUNT": 3,
        }
        """;

        Config? cfg = JsonSerializer.Deserialize<Config>(json, JsonDefaults.TolerantRead);

        Assert.NotNull(cfg);
        Assert.Equal("hello", cfg!.Name);
        Assert.Equal(3, cfg.Count);
    }

    [Fact]
    public void IndentedWrite_ProducesMultiLineOutput()
    {
        string json = JsonSerializer.Serialize(new Config { Name = "x", Count = 1 }, JsonDefaults.IndentedWrite);

        Assert.Contains("\n", json);
        Assert.Contains("  ", json); // indentation
    }

    [Fact]
    public void IncludeFields_RoundTripsPublicFields()
    {
        var original = new FieldBag { X = 7, Label = "tag" };

        string json = JsonSerializer.Serialize(original, JsonDefaults.IncludeFields);
        FieldBag back = JsonSerializer.Deserialize<FieldBag>(json, JsonDefaults.IncludeFields);

        Assert.Equal(7, back.X);
        Assert.Equal("tag", back.Label);
    }

    [Fact]
    public void Properties_ReturnStableSharedInstances()
    {
        Assert.Same(JsonDefaults.TolerantRead, JsonDefaults.TolerantRead);
        Assert.Same(JsonDefaults.IndentedWrite, JsonDefaults.IndentedWrite);
        Assert.Same(JsonDefaults.IncludeFields, JsonDefaults.IncludeFields);
    }
}
