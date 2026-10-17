using System.Text.Json;
using KhaozEngine.BugReports.Relay;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay;

public sealed class RelayJsonTests
{
    [Fact]
    public void Enum_WritesCamelCaseName()
    {
        Assert.Equal("\"appendServerSnapshot\"", JsonSerializer.Serialize(ReportWorkKind.AppendServerSnapshot, RelayJson.Options));
    }

    [Fact]
    public void Enum_ReadsExactCamelCaseName()
    {
        Assert.Equal(ReportWorkKind.CreateIssue, JsonSerializer.Deserialize<ReportWorkKind>("\"createIssue\"", RelayJson.Options));
    }

    [Theory]
    [InlineData("\"CREATEISSUE\"")]
    [InlineData("\"CreateIssue\"")]
    [InlineData("\"createissue\"")]
    [InlineData("\"0\"")]
    [InlineData("0")]
    [InlineData("\"createIssue, appendServerSnapshot\"")]
    public void Enum_RefusesAnyOtherSpelling(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ReportWorkKind>(json, RelayJson.Options));
    }
}
