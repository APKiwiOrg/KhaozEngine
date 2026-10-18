using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.BugReports.Relay;

/// <summary>Renders the public report viewer without authenticated reporter identity.</summary>
internal sealed class ReportViewerWriter(IReportMediaStore media, ReportRevocation? revocation = null)
{
    readonly ReportFolderReader reader = new(media);

    public async Task<bool> WriteAsync(ReportRecord record, ReadOnlyMemory<byte>? serverSnapshotJson, CancellationToken ct)
    {
        if (revocation is not null && await revocation.IsRevokedAsync(record.ReportId, ct, record.Folder).ConfigureAwait(false)) return false;
        ReportFolderContents contents = await reader.ReadAsync(record, ct).ConfigureAwait(false);
        string html = BugReportViewerPage.Render(new BugReportViewerModel(record.Manifest, contents.Clip, contents.Sections,
            Reporter: null, serverSnapshotJson, contents.LogTail));
        string path = ReportLayout.Entry(record.Folder, ReportLayout.ViewerFile);
        if (revocation is not null && await revocation.IsRevokedAsync(record.ReportId, ct, record.Folder).ConfigureAwait(false)) return false;
        await media.PutAsync(ReportArea.Public, path, Encoding.UTF8.GetBytes(html), ReportLayout.HeadersFor(path),
            overwrite: true, ct).ConfigureAwait(false);
        return true;
    }
}
