using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.BugReports.Relay;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay.Support;

internal static class WorkerFixtures
{
    public static ServerSnapshotWire Snapshot(string subject = "discord:42", string key = "server", int tick = 42)
        => IssueFixtures.Snapshot(new Dictionary<string, JsonElement>
        {
            [key] = JsonSerializer.SerializeToElement(new { tick }),
        }) with { Subject = subject };

    public static RelayRequest Request(byte[]? bytes = null, string? serviceKey = "ssssssssssssssssssssssssssssssss",
        Stream? body = null, long? length = null, string header = RelayHeaders.ServiceKey)
    {
        var headers = new Dictionary<string, string>();
        if (serviceKey is not null) headers.Add(header, serviceKey);
        return new RelayRequest(headers, body ?? new MemoryStream(bytes ?? BugReportJson.SerializeServerSnapshot(Snapshot())), length);
    }

    public static Task<RelayResponse> SendAsync(RelayHarness harness, ServerSnapshotWire? snapshot = null)
        => harness.Relay.ServerSnapshotIntakeAsync(RelayHarness.Id,
            Request(BugReportJson.SerializeServerSnapshot(snapshot ?? Snapshot())), CancellationToken.None);

    public static Task ProcessAsync(RelayHarness harness, ReportWorkKind kind = ReportWorkKind.CreateIssue)
        => harness.Relay.ProcessWorkAsync(new ReportWorkItem(kind, RelayHarness.Id), CancellationToken.None);

    public static async Task<ReportRecord> AcceptAsync(RelayHarness harness)
    {
        Assert.Equal(202, (await harness.IntakeAsync()).StatusCode);
        Assert.True(harness.Work.TryDequeue(out _));
        return await RecordAsync(harness);
    }

    public static async Task<ReportRecord> RecordAsync(RelayHarness harness)
        => Assert.IsType<ReportRecord>(await harness.Records.LoadReportAsync(RelayHarness.Id, CancellationToken.None));

    public static Task<bool> SeedClaimAsync(RelayHarness harness, string kind, int epoch, DateTimeOffset claimedAt)
    {
        string path = "claims/" + kind + "-" + RelayHarness.Id + "-" + epoch + ".json";
        return harness.Media.PutAsync(ReportArea.Pending, path, JsonSerializer.SerializeToUtf8Bytes(new { claimedAt }),
            ReportLayout.HeadersFor(path), false, CancellationToken.None);
    }

    public static async Task DrainAsync(RelayHarness harness)
    {
        while (harness.Work.TryDequeue(out ReportWorkItem? item))
            await harness.Relay.ProcessWorkAsync(item, CancellationToken.None);
    }

    public static async Task<string> PublicTextAsync(RelayHarness harness, string name)
    {
        ReportRecord record = await RecordAsync(harness);
        ReadOnlyMemory<byte>? bytes = await harness.Media.GetAsync(ReportArea.Public, record.Folder + "/" + name,
            (int)BugReportLimits.MaxBundleBytes, CancellationToken.None);
        Assert.NotNull(bytes);
        return Encoding.UTF8.GetString(bytes.Value.Span);
    }

    public static async Task<JsonDocument> ViewerAsync(RelayHarness harness)
    {
        string html = await PublicTextAsync(harness, "viewer.html");
        const string opening = "<script type=\"application/json\" id=\"report-data\">";
        int start = html.IndexOf(opening, StringComparison.Ordinal);
        Assert.True(start >= 0);
        start += opening.Length;
        return JsonDocument.Parse(html[start..html.IndexOf("</script>", start, StringComparison.Ordinal)]);
    }

    public static void AssertError(RelayResponse response, int status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("no-store", response.Headers["Cache-Control"]);
        Assert.Equal(code, BugReportJson.DeserializeRelayError(Encoding.UTF8.GetBytes(response.JsonBody!)).Error);
    }
}
