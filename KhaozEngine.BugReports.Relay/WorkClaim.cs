using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.BugReports.Relay;

/// <summary>
/// Uses create-only epochs to give one worker the right to perform a GitHub side effect. A holder that fails
/// before its GitHub call succeeds writes a release marker. A holder whose GitHub call succeeds writes a result
/// marker before it saves the record, so a redelivery after a failed save reconciles from the marker instead of
/// repeating the effect.
/// </summary>
internal sealed class WorkClaim(IReportMediaStore media, TimeProvider time)
{
    /// <summary>A claim holder has ten minutes before another delivery can take over.</summary>
    public const int LeaseMinutes = 10;
    const int MaxEpochs = 64;
    const int MaxClaimBytes = 1024;
    static readonly ReportBlobHeaders MarkerHeaders = new("application/octet-stream", "no-store");

    public async Task<int?> TryAcquireAsync(ReportWorkKind kind, string reportId, CancellationToken ct)
    {
        string prefix = Prefix(kind, reportId);
        int epoch = 0;
        while (epoch < MaxEpochs && await media.ExistsAsync(ReportArea.Pending, Path(prefix, epoch), ct).ConfigureAwait(false))
            epoch++;
        DateTimeOffset now = time.GetUtcNow();
        // An active lease on the latest epoch is busy at every epoch, the last one included, because its holder is
        // still working. Exhaustion means a new epoch is needed and the cap leaves none to create.
        if (epoch > 0 && !await media.ExistsAsync(ReportArea.Pending, Path(prefix, epoch - 1, ".released"), ct).ConfigureAwait(false))
        {
            ReadOnlyMemory<byte> json = await media.GetAsync(ReportArea.Pending, Path(prefix, epoch - 1), MaxClaimBytes, ct)
                .ConfigureAwait(false) ?? throw new InvalidDataException("The latest work claim is missing.");
            Claim latest = JsonSerializer.Deserialize<Claim>(json.Span, RelayJson.Options)
                ?? throw new JsonException("The work claim is null.");
            if (now - latest.ClaimedAt < TimeSpan.FromMinutes(LeaseMinutes)) return null;
        }
        if (epoch == MaxEpochs) throw new ReportClaimExhaustedException(reportId, kind);
        string path = Path(prefix, epoch);
        bool acquired = await media.PutAsync(ReportArea.Pending, path, JsonSerializer.SerializeToUtf8Bytes(new Claim(now), RelayJson.Options),
            ReportLayout.HeadersFor(path), overwrite: false, ct).ConfigureAwait(false);
        return acquired ? epoch : null;
    }

    /// <summary>Returns the recorded result of a completed GitHub effect, or null when no epoch completed.</summary>
    public async Task<string?> FindResultAsync(ReportWorkKind kind, string reportId, CancellationToken ct)
    {
        string prefix = Prefix(kind, reportId);
        for (int epoch = 0; epoch < MaxEpochs; epoch++)
        {
            if (!await media.ExistsAsync(ReportArea.Pending, Path(prefix, epoch), ct).ConfigureAwait(false)) return null;
            ReadOnlyMemory<byte>? result = await media.GetAsync(ReportArea.Pending, Path(prefix, epoch, ".done"), MaxClaimBytes, ct)
                .ConfigureAwait(false);
            if (result is not null) return Encoding.UTF8.GetString(result.Value.Span);
        }
        return null;
    }

    public async Task CompleteAsync(ReportWorkKind kind, string reportId, int epoch, string result)
    {
        string path = Path(Prefix(kind, reportId), epoch, ".done");
        // The GitHub effect has already happened, so recording it must survive cancellation of the work.
        await media.PutAsync(ReportArea.Pending, path, Encoding.UTF8.GetBytes(result), MarkerHeaders,
            overwrite: false, CancellationToken.None).ConfigureAwait(false);
    }

    public async Task ReleaseAsync(ReportWorkKind kind, string reportId, int epoch)
    {
        string path = Path(Prefix(kind, reportId), epoch, ".released");
        // Cleanup must survive cancellation of the work that held this claim.
        await media.PutAsync(ReportArea.Pending, path, ReadOnlyMemory<byte>.Empty, MarkerHeaders,
            overwrite: false, CancellationToken.None).ConfigureAwait(false);
    }

    static string Prefix(ReportWorkKind kind, string reportId) => "claims/" + KindName(kind) + "-" + reportId + "-";
    static string Path(string prefix, int epoch, string extension = ".json") => prefix + epoch.ToString(CultureInfo.InvariantCulture) + extension;

    static string KindName(ReportWorkKind kind) => kind switch
    {
        ReportWorkKind.CreateIssue => "issue",
        ReportWorkKind.AppendServerSnapshot => "comment",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown report work kind."),
    };

    sealed record Claim(DateTimeOffset ClaimedAt);
}
