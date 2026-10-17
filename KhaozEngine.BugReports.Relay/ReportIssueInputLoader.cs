using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Contracts;

namespace KhaozEngine.BugReports.Relay;

/// <summary>Loads the shared folder inputs for private issue composition.</summary>
internal sealed class ReportIssueInputLoader(IReportMediaStore media)
{
    readonly ReportFolderReader reader = new(media);

    public async Task<IssueInput> LoadAsync(ReportRecord record, ServerSnapshotWire? snapshot, CancellationToken ct)
    {
        ReportFolderContents contents = await reader.ReadAsync(record, ct).ConfigureAwait(false);
        return new IssueInput(record, contents.Sections, contents.Clip, contents.LogTail, snapshot);
    }
}
