using System;
using System.Text.Json;
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
        Assert.Null(bundle.TextState);
    }

    [Theory]
    [InlineData("[]", ContentAuthoringRefusal.BundleRootObjectRequired, "$", JsonValueKind.Array)]
    [InlineData("{}", ContentAuthoringRefusal.BundleMemberRequired, "$.formatVersion", null)]
    [InlineData("{ \"formatVersion\": \"1\" }", ContentAuthoringRefusal.BundleMemberKindInvalid, "$.formatVersion", JsonValueKind.String)]
    [InlineData("{ \"formatVersion\": 1.5 }", ContentAuthoringRefusal.BundleInt32Unrepresentable, "$.formatVersion", null)]
    public void FormatPrecheckAndReadRefuseInvalidVersionDocumentsTheSameWay(
        string json, ContentAuthoringRefusal refusal, string path, JsonValueKind? actualKind)
    {
        ContentAuthoringException precheckRefusal = Assert.Throws<ContentAuthoringException>(
            () => ContentBundleJson.ReadFormatVersion(json));
        ContentAuthoringException readRefusal = Assert.Throws<ContentAuthoringException>(
            () => ContentBundleJson.Read(json));

        foreach (ContentAuthoringException refused in new[] { precheckRefusal, readRefusal })
        {
            Assert.Equal(ContentAuthoringException.BundleFormatReason, refused.Reason);
            Assert.Equal(refusal, refused.Refusal);
            Assert.Equal(path, refused.JsonPath);
            Assert.Equal(actualKind, refused.ActualJsonKind);
        }
    }

    [Fact]
    public void TextThatIsNotJsonKeepsTheReadersFaultAndItsPosition()
    {
        // The second line's double comma is the first defect: comments and one trailing comma are accepted.
        const string json = "{\n  \"formatVersion\": 1,,\n}";

        ContentAuthoringException precheckRefusal = Assert.Throws<ContentAuthoringException>(
            () => ContentBundleJson.ReadFormatVersion(json));
        ContentAuthoringException readRefusal = Assert.Throws<ContentAuthoringException>(
            () => ContentBundleJson.Read(json));

        foreach (ContentAuthoringException refused in new[] { precheckRefusal, readRefusal })
        {
            Assert.Equal(ContentAuthoringException.BundleFormatReason, refused.Reason);
            Assert.Equal(ContentAuthoringRefusal.BundleJsonMalformed, refused.Refusal);
            JsonException reader = Assert.IsAssignableFrom<JsonException>(refused.InnerException);
            Assert.Equal(1L, refused.LineNumber);
            Assert.Equal(reader.LineNumber, refused.LineNumber);
            Assert.Equal(reader.BytePositionInLine, refused.BytePositionInLine);
            Assert.Equal(reader.Path, refused.JsonPath);
        }

        Assert.Equal(1, ContentBundleJson.ReadFormatVersion(json.Replace(",,", ",", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(3, 2)]
    [InlineData(0, 1)]
    public void FormatPrecheckReturnsAnUnsupportedVersionForTheCallerToInspect(int unsupported, int supported)
    {
        string json = CompleteDocument(unsupported);

        Assert.Equal(unsupported, ContentBundleJson.ReadFormatVersion(json));

        ContentAuthoringException refused = Assert.Throws<ContentAuthoringException>(
            () => ContentBundleJson.Read(json));
        Assert.Equal(ContentAuthoringException.BundleFormatReason, refused.Reason);
        Assert.Equal(ContentAuthoringRefusal.BundleVersionUnsupported, refused.Refusal);
        Assert.Equal(unsupported, refused.ActualFormatVersion);
        Assert.Equal(new[] { 1, 2 }, refused.SupportedFormatVersions);
        Assert.Null(refused.JsonPath);

        Assert.Equal(supported, ContentBundleJson.Read(CompleteDocument(supported)).FormatVersion);
    }

    [Fact]
    public void SupportedFormatVersionsIsAnOwnedReadOnlySnapshot()
    {
        int[] source = [1, 2];
        var refused = new ContentAuthoringException(
            ContentAuthoringRefusal.BundleVersionUnsupported, "detail", default, 0, ContentAuthoringException.BundleFormatReason)
        {
            SupportedFormatVersions = source,
        };
        source[0] = 9;

        Assert.Equal(new[] { 1, 2 }, refused.SupportedFormatVersions);
        Assert.Throws<NotSupportedException>(
            () => ((System.Collections.Generic.IList<int>)refused.SupportedFormatVersions!)[0] = 9);
        Assert.Equal(new[] { 1, 2 }, refused.SupportedFormatVersions);
    }

    /// <summary>Every member a document needs, with the text section only format 2 carries.</summary>
    static string CompleteDocument(int formatVersion)
    {
        string text = formatVersion == 1 ? string.Empty : ", \"text\": { \"languages\": [], \"values\": [] }";
        return FormattableString.Invariant(
            $"{{ \"formatVersion\": {formatVersion}, \"storeEpoch\": \"test\", \"sourceVersion\": 0, \"types\": [], \"families\": [], \"rows\": [], \"rules\": []{text} }}");
    }

    [Fact]
    public void FormatTwoReadsItsTextSectionAndFormatOneStaysTextFree()
    {
        const string json = """
            {
              "formatVersion": 2,
              "storeEpoch": "test",
              "sourceVersion": 0,
              "types": [],
              "families": [],
              "rows": [],
              "rules": [],
              "text": { "languages": [ { "language": "fr", "wireTag": "fr" } ], "values": [] },
            }
            """;

        Assert.Equal(ContentBundle.TextFormatVersion, ContentBundleJson.ReadFormatVersion(json));
        ContentBundle bundle = ContentBundleJson.Read(json);

        Assert.Equal(ContentBundle.TextFormatVersion, bundle.FormatVersion);
        Assert.Equal("fr", Assert.Single(bundle.TextState!.Languages).WireTag);
        Assert.Empty(bundle.TextState.Values);
        Assert.Equal(1, ContentBundle.CurrentFormatVersion);
    }
}
