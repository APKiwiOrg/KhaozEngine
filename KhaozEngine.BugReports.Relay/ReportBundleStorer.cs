using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Contracts;

namespace KhaozEngine.BugReports.Relay;

/// <summary>Stores validated bundle entries and the viewer publicly, with account state kept private.</summary>
internal sealed class ReportBundleStorer(IReportMediaStore media, ReportViewerWriter viewer)
{
    public async Task<ReportRecord> StoreAsync(MemoryStream bundle, BugReportManifest manifest, ReporterIdentity reporter,
        DateTimeOffset receivedAt, CancellationToken ct)
    {
        string folder = ReportLayout.NewFolder(manifest.GameId, manifest.ReportId, receivedAt);
        bundle.Position = 0;
        using (var archive = new ZipArchive(bundle, ZipArchiveMode.Read, leaveOpen: true))
        {
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                using Stream source = entry.Open();
                using var bytes = new MemoryStream((int)entry.Length);
                await source.CopyToAsync(bytes, ct).ConfigureAwait(false);
                string path = ReportLayout.Entry(folder, entry.FullName);
                await media.PutAsync(ReportArea.Public, path, bytes.GetBuffer().AsMemory(0, (int)bytes.Length),
                    ReportLayout.HeadersFor(path), overwrite: false, ct).ConfigureAwait(false);
            }
        }
        var record = new ReportRecord(BugReportFormat.Version, manifest.ReportId, ReportIds.ReferenceCode(manifest.ReportId),
            folder, receivedAt, reporter, manifest, null, ServerSnapshotState.None);
        await viewer.WriteAsync(record, null, ct).ConfigureAwait(false);
        return record;
    }
}
