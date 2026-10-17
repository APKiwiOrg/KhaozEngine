using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.BugReports.Relay;

/// <summary>Uses create-only epochs to give one worker the right to perform a GitHub side effect.</summary>
internal sealed class WorkClaim(IReportMediaStore media, TimeProvider time)
{
    /// <summary>A claim holder has ten minutes before another delivery can take over.</summary>
    public const int LeaseMinutes = 10;
    const int MaxEpochs = 64;
    const int MaxClaimBytes = 1024;

    public async Task<int?> TryAcquireAsync(string kind, string reportId, CancellationToken ct)
    {
        string prefix = Prefix(kind, reportId);
        int epoch = 0;
        while (epoch < MaxEpochs && await media.ExistsAsync(ReportArea.Pending, Path(prefix, epoch), ct).ConfigureAwait(false))
            epoch++;
        if (epoch == MaxEpochs) return null;
        DateTimeOffset now = time.GetUtcNow();
        if (epoch > 0 && !await media.ExistsAsync(ReportArea.Pending, Path(prefix, epoch - 1, ".released"), ct).ConfigureAwait(false))
        {
            ReadOnlyMemory<byte> json = await media.GetAsync(ReportArea.Pending, Path(prefix, epoch - 1), MaxClaimBytes, ct)
                .ConfigureAwait(false) ?? throw new InvalidDataException("The latest work claim is missing.");
            Claim latest = JsonSerializer.Deserialize<Claim>(json.Span, RelayJson.Options)
                ?? throw new JsonException("The work claim is null.");
            if (now - latest.ClaimedAt < TimeSpan.FromMinutes(LeaseMinutes)) return null;
        }
        string path = Path(prefix, epoch);
        bool acquired = await media.PutAsync(ReportArea.Pending, path, JsonSerializer.SerializeToUtf8Bytes(new Claim(now), RelayJson.Options),
            ReportLayout.HeadersFor(path), overwrite: false, ct).ConfigureAwait(false);
        return acquired ? epoch : null;
    }

    public async Task ReleaseAsync(string kind, string reportId, int epoch)
    {
        string path = Path(Prefix(kind, reportId), epoch, ".released");
        // Cleanup must survive cancellation of the work that held this claim.
        await media.PutAsync(ReportArea.Pending, path, ReadOnlyMemory<byte>.Empty, new ReportBlobHeaders("application/octet-stream", "no-store"),
            overwrite: false, CancellationToken.None).ConfigureAwait(false);
    }

    static string Prefix(string kind, string reportId) => "claims/" + kind + "-" + reportId + "-";
    static string Path(string prefix, int epoch, string extension = ".json") => prefix + epoch.ToString(CultureInfo.InvariantCulture) + extension;

    sealed record Claim(DateTimeOffset ClaimedAt);
}
