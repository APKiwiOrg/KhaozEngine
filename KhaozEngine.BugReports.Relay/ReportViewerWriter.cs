using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.BugReports.Relay;

/// <summary>Renders the public report viewer without authenticated reporter identity.</summary>
internal sealed class ReportViewerWriter(IReportMediaStore media)
{
    readonly ReportFolderReader reader = new(media);

    public async Task WriteAsync(ReportRecord record, ReadOnlyMemory<byte>? serverSnapshotJson, CancellationToken ct)
    {
        ReportFolderContents contents = await reader.ReadAsync(record, ct).ConfigureAwait(false);
        string html = BugReportViewerPage.Render(new BugReportViewerModel(record.Manifest, contents.Clip, contents.Sections,
            Reporter: null, serverSnapshotJson, contents.LogTail));
        string path = ReportLayout.Entry(record.Folder, ReportLayout.ViewerFile);
        await media.PutAsync(ReportArea.Public, path, Encoding.UTF8.GetBytes(html), ReportLayout.HeadersFor(path),
            overwrite: true, ct).ConfigureAwait(false);
    }
}
