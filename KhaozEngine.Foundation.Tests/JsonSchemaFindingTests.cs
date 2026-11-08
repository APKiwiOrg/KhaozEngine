using System;
using System.Collections.Generic;
using KhaozEngine.Content;
using Xunit;

namespace KhaozEngine.Tests;

public class JsonSchemaFindingTests
{
    [Theory]
    [InlineData("{ \"count\": \"bad\" }", "/count", "type", "count")]
    [InlineData("{ \"count\": 1, \"extra/~\": true }", "/extra~1~0", "additionalProperties", "extra/~")]
    [InlineData("{}", "", "required", null)]
    [InlineData("{ \"count\": 1, \"values\": [false] }", "/values/0", "type", null)]
    public void Findings_keep_instance_locations_keywords_and_known_property_names(
        string json, string location, string keyword, string? property)
    {
        const string schema = """
            { "type": "object", "required": ["count"],
              "properties": { "count": { "type": "integer" },
                              "values": { "type": "array", "items": { "type": "integer" } } },
              "additionalProperties": false }
            """;
        ValidationReport report = JsonSchemaValidator.Validate(json, schema);
        Assert.False(report.IsValid);
        Assert.Contains(new JsonSchemaFinding(location, keyword, property), report.Findings);
        Assert.True(JsonSchemaValidator.Validate("{ \"count\": 1 }", schema).IsValid);
    }

    [Fact]
    public void Findings_are_an_owned_read_only_snapshot_and_legacy_reports_default_to_empty()
    {
        var finding = new JsonSchemaFinding("/count", "type", "count");
        var source = new List<JsonSchemaFinding> { finding };
        var report = new ValidationReport(false, Array.Empty<string>()) { Findings = source };
        source.Clear();
        Assert.Equal(finding, Assert.Single(report.Findings));
        if (report.Findings is IList<JsonSchemaFinding> list)
        {
            Assert.Throws<NotSupportedException>(() => list[0] = finding with { Keyword = "other" });
            Assert.Throws<NotSupportedException>(() => list.Clear());
        }
        Assert.Equal(finding, Assert.Single(report.Findings));
        var legacy = new ValidationReport(true, Array.Empty<string>());
        (bool valid, IReadOnlyList<string> errors) = legacy;
        Assert.True(valid);
        Assert.Empty(errors);
        Assert.Empty(legacy.Findings);
        Assert.Empty(JsonSchemaValidator.Validate("{}", "{ invalid").Findings);
    }
}
