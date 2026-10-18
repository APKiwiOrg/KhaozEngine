using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.BugReports.Relay;
using KhaozEngine.Tests.BugReports.Relay.Support;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay;

public sealed class PublicSnapshotPrivacyTests
{
    const string Id = RelayHarness.Id;
    const string Subject = "discord:42";
    static readonly CancellationToken Ct = CancellationToken.None;

    [Theory]
    [InlineData(ReportWorkKind.CreateIssue)]
    [InlineData(ReportWorkKind.AppendServerSnapshot)]
    public async Task Worker_PublicSnapshotDocuments_NeverCarryTheSubject(ReportWorkKind kind)
    {
        var harness = new RelayHarness();
        ReportRecord record = await WorkerFixtures.AcceptAsync(harness);
        Assert.Equal(Subject, record.Reporter.Subject);
        if (kind == ReportWorkKind.AppendServerSnapshot)
            await harness.Records.SaveReportAsync(record with { IssueNumber = 7 }, Ct);
        await harness.Records.PutPendingSnapshotAsync(Id, BugReportJson.SerializeServerSnapshot(WorkerFixtures.Snapshot(Subject)), Ct);

        await WorkerFixtures.ProcessAsync(harness, kind);

        ServerSnapshotState expected = kind == ReportWorkKind.CreateIssue ? ServerSnapshotState.InBody : ServerSnapshotState.Commented;
        Assert.Equal(expected, (await WorkerFixtures.RecordAsync(harness)).ServerSnapshot);
        string server = await WorkerFixtures.PublicTextAsync(harness, "server.json");
        string viewer = await WorkerFixtures.PublicTextAsync(harness, "viewer.html");
        Assert.DoesNotContain(Subject, server, StringComparison.Ordinal);
        Assert.DoesNotContain(Subject, viewer, StringComparison.Ordinal);
        Assert.True((await PublicBytesAsync(harness, record, "server.json")).AsSpan().IndexOf(Encoding.UTF8.GetBytes(Subject)) < 0);
        Assert.True((await PublicBytesAsync(harness, record, "viewer.html")).AsSpan().IndexOf(Encoding.UTF8.GetBytes(Subject)) < 0);
        using JsonDocument viewerData = await WorkerFixtures.ViewerAsync(harness);
        Assert.Contains("tick", viewerData.RootElement.GetProperty("server").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Worker_PublicSnapshot_HoldsOnlyReportIdCapturedAtAndSections()
    {
        var harness = new RelayHarness();
        await WorkerFixtures.AcceptAsync(harness);
        ServerSnapshotWire wire = WorkerFixtures.Snapshot(Subject);
        await harness.Records.PutPendingSnapshotAsync(Id, BugReportJson.SerializeServerSnapshot(wire), Ct);

        await WorkerFixtures.ProcessAsync(harness);

        using JsonDocument server = JsonDocument.Parse(await WorkerFixtures.PublicTextAsync(harness, "server.json"));
        JsonElement root = server.RootElement;
        Assert.Equal(new[] { "reportId", "capturedAt", "sections" }, root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(Id, root.GetProperty("reportId").GetString());
        Assert.Equal(wire.CapturedAt, root.GetProperty("capturedAt").GetDateTimeOffset());
        Assert.Equal(42, root.GetProperty("sections").GetProperty("server").GetProperty("tick").GetInt32());
    }

    static async Task<byte[]> PublicBytesAsync(RelayHarness harness, ReportRecord record, string name)
    {
        ReadOnlyMemory<byte>? bytes = await harness.Media.GetAsync(ReportArea.Public, record.Folder + "/" + name,
            (int)BugReportLimits.MaxBundleBytes, Ct);
        Assert.NotNull(bytes);
        return bytes.Value.ToArray();
    }
}
