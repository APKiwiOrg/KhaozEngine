using System;
using System.Text;
using System.Text.Json;
using KhaozEngine.BugReports;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Core;

public sealed class SectionJsonTests
{
    static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void Truncate_with_tiny_budget_yields_bare_marker()
    {
        byte[] json = Utf8("{\"lines\":[\"aaaaaaaaaaaaaaaaaaaa\"]}");

        Assert.Equal("{\"truncated\":true}", Encoding.UTF8.GetString(SectionJson.Truncate(json, 18)));
    }

    [Fact]
    public void Truncate_returns_a_fitting_section_unchanged()
    {
        byte[] json = Utf8("{\"a\":1}");

        Assert.Same(json, SectionJson.Truncate(json, json.Length));
    }

    [Fact]
    public void Truncate_closes_every_open_container_after_the_last_kept_leaf()
    {
        byte[] json = Utf8("{\"a\":{\"b\":[1,2,3],\"c\":null},\"d\":\"e\"}");
        const string Expected = "{\"truncated\":true,\"a\":{\"b\":[1,2]}}";

        Assert.Equal(Expected, Encoding.UTF8.GetString(SectionJson.Truncate(json, Expected.Length)));
    }

    [Fact]
    public void Cut_never_splits_a_surrogate_pair()
    {
        string text = "ab\U0001F600cd";

        Assert.Equal("ab", SectionJson.Cut(text, 3));
        Assert.Equal("ab\U0001F600", SectionJson.Cut(text, 4));
        Assert.Same(text, SectionJson.Cut(text, 6));
    }

    [Fact]
    public void ErrorObject_survives_a_lone_surrogate_in_the_message()
    {
        byte[] json = SectionJson.ErrorObject(new InvalidOperationException("bad \uD800 text"), 8192);

        using JsonDocument document = JsonDocument.Parse(json);
        Assert.StartsWith("System.InvalidOperationException: bad ", document.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
    }
}
