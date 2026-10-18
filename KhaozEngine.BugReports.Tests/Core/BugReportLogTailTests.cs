using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using KhaozEngine.BugReports;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Core;

public sealed class BugReportLogTailTests
{
    [Fact]
    public void FromLogSection_KeepsNewestFiftyLines_InOrder()
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new { lines = Enumerable.Range(0, 60).Select(i => "line " + i) });
        Assert.Equal(Enumerable.Range(10, 50).Select(i => "line " + i), BugReportLogTail.FromLogSection(json));
    }

    [Theory]
    [InlineData(0, new string[0])]
    [InlineData(1, new[] { "last" })]
    [InlineData(5, new[] { "first", "last" })]
    public void FromLogSection_UsesRequestedLimit(int limit, string[] expected)
        => Assert.Equal(expected, BugReportLogTail.FromLogSection("{\"lines\":[\"first\",\"last\"]}"u8.ToArray(), limit));

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"lines\":\"not a list\"}")]
    [InlineData("{\"other\":[\"x\"]}")]
    [InlineData("{\"lines\":[1,{\"a\":\"b\"},null]}")]
    [InlineData("{\"lines\":[\"a\"],\"lines\":[\"b\"]}")]
    [InlineData("{\"lines\":[\"\\uD800\"]}")]
    public void FromLogSection_MalformedOrAbsentLines_ReturnsEmpty(string json)
        => Assert.Empty(BugReportLogTail.FromLogSection(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void FromLogSection_MixedLines_KeepsOnlyStrings()
        => Assert.Equal(new[] { "first", "last" },
            BugReportLogTail.FromLogSection("{\"lines\":[\"first\",1,null,\"last\"]}"u8.ToArray()));

    [Fact]
    public void FromLogSection_NegativeLimit_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => BugReportLogTail.FromLogSection("{}"u8.ToArray(), -1));
}
