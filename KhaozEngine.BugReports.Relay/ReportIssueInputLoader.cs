using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Contracts;

namespace KhaozEngine.BugReports.Relay;

/// <summary>Loads the shared folder inputs for private issue composition.</summary>
internal sealed class ReportIssueInputLoader(IReportMediaStore media)
{
    readonly ReportFolderReader reader = new(media);

    public async Task<IssueInput> LoadAsync(ReportRecord record, ReadOnlyMemory<byte>? snapshotJson, CancellationToken ct)
    {
        ReportFolderContents contents = await reader.ReadAsync(record, ct).ConfigureAwait(false);
        ServerSnapshotWire? snapshot = snapshotJson is { } json ? BugReportJson.DeserializeServerSnapshot(json.Span) : null;
        return new IssueInput(record, contents.Sections, contents.Clip, contents.LogTail, snapshot);
    }
}
