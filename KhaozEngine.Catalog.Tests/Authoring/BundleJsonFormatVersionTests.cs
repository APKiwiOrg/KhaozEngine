using System;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Authoring;

public class BundleJsonFormatVersionTests
{
    [Fact]
    public void FormatPrecheckAndReadAcceptTheSameHandAuthoredJson()
    {
        string json = """
            {
              // A committed seed may explain its format.
              "formatVersion": 1,
              "storeEpoch": "test",
              "sourceVersion": 0,
              "types": [],
              "families": [],
              "rows": [],
              "rules": [],
            }
            """;

        int formatVersion = ContentBundleJson.ReadFormatVersion(json);
        ContentBundle bundle = ContentBundleJson.Read(json);

        Assert.Equal(1, formatVersion);
        Assert.Equal(formatVersion, bundle.FormatVersion);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{ \"formatVersion\": \"1\" }")]
    public void FormatPrecheckAndReadRefuseInvalidVersionDocumentsTheSameWay(string json)
    {
        ContentAuthoringException precheckRefusal = Assert.Throws<ContentAuthoringException>(
            () => ContentBundleJson.ReadFormatVersion(json));
        ContentAuthoringException readRefusal = Assert.Throws<ContentAuthoringException>(
            () => ContentBundleJson.Read(json));

        Assert.Equal(ContentAuthoringException.BundleFormatReason, precheckRefusal.Reason);
        Assert.Equal(readRefusal.Reason, precheckRefusal.Reason);
        Assert.Equal(readRefusal.Message, precheckRefusal.Message);
    }

    [Fact]
    public void FormatPrecheckReturnsAnUnsupportedVersionForTheCallerToInspect()
    {
        const string json = """
            {
              "formatVersion": 2
            }
            """;

        Assert.Equal(2, ContentBundleJson.ReadFormatVersion(json));

        ContentAuthoringException refused = Assert.Throws<ContentAuthoringException>(
            () => ContentBundleJson.Read(json));
        Assert.Equal(ContentAuthoringException.BundleFormatReason, refused.Reason);
        Assert.Contains("format version 2", refused.Message, StringComparison.Ordinal);
    }
}
