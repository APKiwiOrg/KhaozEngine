using System;
using System.IO;
using System.Reflection;
using KhaozEngine.Content;
using Xunit;

namespace KhaozEngine.Tests;

public sealed class SampleConfig { public string Name { get; set; } = ""; public int Count { get; set; } }

public class ContentTests
{
    private static readonly Assembly Asm = typeof(ContentTests).Assembly;
    private const string SampleResource = "KhaozEngine.Tests.Fixtures.sample.json";

    [Fact]
    public void LoadsFromEmbeddedResource()
    {
        var c = ConfigLoader.Load<SampleConfig>(Asm, SampleResource);
        Assert.Equal("abc", c.Name);
        Assert.Equal(3, c.Count);
    }

    [Fact]
    public void DiskPathOverridesEmbedded()
    {
        string tmp = Path.GetTempFileName();
        File.WriteAllText(tmp, "{ \"name\": \"disk\", \"count\": 9 }");
        try
        {
            var c = ConfigLoader.Load<SampleConfig>(Asm, SampleResource, diskPath: tmp);
            Assert.Equal("disk", c.Name);
            Assert.Equal(9, c.Count);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void MissingConfigThrows()
    {
        Assert.Throws<InvalidOperationException>(
            () => ConfigLoader.Load<SampleConfig>(Asm, "KhaozEngine.Tests.Nope.json"));
    }

    private const string Schema = """
        { "$schema":"https://json-schema.org/draft/2020-12/schema",
          "type":"object","required":["name","count"],
          "properties":{ "name":{"type":"string"}, "count":{"type":"integer"} } }
        """;

    [Fact]
    public void ValidateAcceptsValidAndRejectsInvalid()
    {
        Assert.True(JsonSchemaValidator.Validate("{ \"name\":\"x\", \"count\":1 }", Schema).IsValid);
        var bad = JsonSchemaValidator.Validate("{ \"name\":\"x\" }", Schema);   // missing required count
        Assert.False(bad.IsValid);
        Assert.NotEmpty(bad.Errors);
    }

    [Fact]
    public void ValidateDirectoryPassesValidFailsInvalidSkipsUnschemad()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ke-content-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "schemas"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "schemas", "s.schema.json"), Schema);
            File.WriteAllText(Path.Combine(dir, "good.json"), "{ \"$schema\":\"schemas/s.schema.json\", \"name\":\"x\", \"count\":1 }");
            File.WriteAllText(Path.Combine(dir, "noschema.json"), "{ \"name\":\"x\" }");
            Assert.True(JsonSchemaValidator.ValidateDirectory(dir, new StringWriter()));   // good passes, noschema skipped

            File.WriteAllText(Path.Combine(dir, "bad.json"), "{ \"$schema\":\"schemas/s.schema.json\", \"name\":\"x\" }");
            Assert.False(JsonSchemaValidator.ValidateDirectory(dir, new StringWriter()));  // bad now fails
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("{ not json")]                    // broken text
    [InlineData("[1, 2, 3]")]                     // parses, but is not a schema
    [InlineData("{ \"type\": 5 }")]               // a keyword whose value is the wrong shape
    public void ValidateReportsAnUnparseableSchemaInsteadOfThrowing(string schemaJson)
    {
        var report = JsonSchemaValidator.Validate("{ \"name\":\"x\" }", schemaJson);
        Assert.False(report.IsValid);
        Assert.Contains(report.Errors, e => e.StartsWith("invalid schema:", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateDirectoryFailsOnlyTheFileWhoseSchemaIsBroken()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ke-content-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "schemas"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "schemas", "s.schema.json"), Schema);
            File.WriteAllText(Path.Combine(dir, "schemas", "broken.schema.json"), "{ not json");
            File.WriteAllText(Path.Combine(dir, "good.json"), "{ \"$schema\":\"schemas/s.schema.json\", \"name\":\"x\", \"count\":1 }");
            File.WriteAllText(Path.Combine(dir, "brokenschema.json"), "{ \"$schema\":\"schemas/broken.schema.json\", \"name\":\"x\" }");

            var log = new StringWriter();
            Assert.False(JsonSchemaValidator.ValidateDirectory(dir, log));   // the sweep returns rather than throwing

            string text = log.ToString();
            Assert.Contains("FAIL  brokenschema.json", text, StringComparison.Ordinal);
            Assert.Contains("invalid schema:", text, StringComparison.Ordinal);
            Assert.Contains("OK    good.json", text, StringComparison.Ordinal);   // the rest of the sweep still ran
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
