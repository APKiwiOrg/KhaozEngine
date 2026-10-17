using System;
using KhaozEngine.BugReports.Relay;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay;

public sealed class ReportWorkItemJsonTests
{
    const string Id = "0192f3a4b5c67d8e9f0a1b2c3d4e5f60";

    [Fact]
    public void Serialize_IsCamelCase()
    {
        string json = ReportWorkItemJson.Serialize(new ReportWorkItem(ReportWorkKind.AppendServerSnapshot, Id, 2));

        Assert.Equal("{\"kind\":\"appendServerSnapshot\",\"reportId\":\"" + Id + "\",\"requeues\":2}", json);
    }

    [Fact]
    public void Parse_RoundTrips()
    {
        var created = new ReportWorkItem(ReportWorkKind.CreateIssue, Id);
        var appended = new ReportWorkItem(ReportWorkKind.AppendServerSnapshot, Id, 3);

        Assert.Equal(created, ReportWorkItemJson.Parse(ReportWorkItemJson.Serialize(created)));
        Assert.Equal(appended, ReportWorkItemJson.Parse(ReportWorkItemJson.Serialize(appended)));
    }

    [Fact]
    public void Parse_MissingRequeues_IsZero()
    {
        ReportWorkItem item = ReportWorkItemJson.Parse("{\"kind\":\"createIssue\",\"reportId\":\"" + Id + "\"}");

        Assert.Equal(new ReportWorkItem(ReportWorkKind.CreateIssue, Id, 0), item);
    }

    [Fact]
    public void Parse_QuotedRequeues_Rejects()
    {
        string json = "{\"kind\":\"createIssue\",\"reportId\":\"" + Id + "\",\"requeues\":\"2\"}";

        Assert.Throws<FormatException>(() => ReportWorkItemJson.Parse(json));
    }

    [Theory]
    [InlineData("{\"kind\":\"other\",\"reportId\":\"" + Id + "\"}")]
    [InlineData("{\"kind\":\"createIssue\",\"reportId\":\"xyz\"}")]
    [InlineData("{\"kind\":\"createIssue\",\"reportId\":\"" + Id + "\",\"requeues\":-1}")]
    [InlineData("{\"kind\":1,\"reportId\":\"" + Id + "\"}")]
    [InlineData("{\"reportId\":\"" + Id + "\"}")]
    [InlineData("null")]
    [InlineData("not json")]
    public void Parse_Rejects(string json)
    {
        Assert.Throws<FormatException>(() => ReportWorkItemJson.Parse(json));
    }
}
