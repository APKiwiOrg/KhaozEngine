using System;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Authoring;

public sealed class BundleConstructorRefusalTests
{
    [Theory]
    [InlineData("family")]
    [InlineData("schema")]
    [InlineData("rule-kind")]
    [InlineData("rule")]
    public void AnUnmappedConstructorRuleKeepsTheBundleCarrierAndInnerCause(string producer)
    {
        var malformed = new ArgumentException("constructor refusal");
        ContentAuthoringException Convert() => producer switch
        {
            "family" => ContentBundleConstructionRefusals.Family(null, malformed, "$.families[0]", "weapons", []),
            "schema" => ContentBundleConstructionRefusals.Schema(null, malformed, "$.types[0]"),
            "rule-kind" => ContentBundleConstructionRefusals.Rule(malformed, "$.rules[0]", (RemapRuleKind)99, []),
            _ => ContentBundleConstructionRefusals.Rule(malformed, "$.rules[0]", RemapRuleKind.ReplacedBy, []),
        };
        Action refuse = () => throw Convert();
        ContentAuthoringException refused = Assert.Throws<ContentAuthoringException>(refuse);
        Assert.Equal(ContentAuthoringRefusal.BundleConstructorRuleUnmapped, refused.Refusal);
        Assert.Equal("bundle-format", refused.Reason);
        Assert.Equal(default, refused.Type);
        Assert.Equal(0, refused.Id);
        Assert.Same(malformed, refused.InnerException);
        Assert.Null(refused.JsonPath);
        ContentBundle accepted = ContentBundleJson.Read("""
            { "formatVersion": 1, "storeEpoch": "epoch", "sourceVersion": 0,
              "types": [], "rows": [], "families": [], "rules": [] }
            """);
        Assert.Equal(0, accepted.SourceVersion);
    }
}
