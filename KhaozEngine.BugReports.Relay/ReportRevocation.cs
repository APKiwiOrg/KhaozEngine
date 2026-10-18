using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.BugReports.Relay;

/// <summary>Retains revocation independently of public media and private worker progress.</summary>
internal sealed class ReportRevocation(IReportMediaStore media, TimeProvider time, ILogger logger)
{
    public async Task RevokeAsync(string reportId, CancellationToken ct)
    {
        string path = ReportLayout.Revoked(reportId);
        await media.PutAsync(ReportArea.Pending, path,
            JsonSerializer.SerializeToUtf8Bytes(new { revokedAt = time.GetUtcNow() }, RelayJson.Options),
            ReportLayout.HeadersFor(path), overwrite: false, ct).ConfigureAwait(false);
    }

    public async Task<bool> IsRevokedAsync(string reportId, CancellationToken ct, string? folder = null)
    {
        bool revoked = await media.ExistsAsync(ReportArea.Pending, ReportLayout.Revoked(reportId), ct).ConfigureAwait(false);
        if (!revoked && folder is not null
            && !await media.ExistsAsync(ReportArea.Public, ReportLayout.Entry(folder, BundleEntryNames.Manifest), ct).ConfigureAwait(false))
        {
            await RevokeAsync(reportId, ct).ConfigureAwait(false);
            revoked = true;
        }
        if (revoked) logger.Info("Revoked report " + reportId + ".");
        return revoked;
    }
}
