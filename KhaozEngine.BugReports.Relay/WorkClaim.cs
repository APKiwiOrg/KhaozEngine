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
    const int MaxEpochs = 16;
    const int MaxClaimBytes = 1024;

    public async Task<bool> TryAcquireAsync(string kind, string reportId, CancellationToken ct)
    {
        string prefix = "claims/" + kind + "-" + reportId + "-";
        int epoch = 0;
        while (epoch < MaxEpochs && await media.ExistsAsync(ReportArea.Pending, Path(prefix, epoch), ct).ConfigureAwait(false))
            epoch++;
        if (epoch == MaxEpochs) return false;
        DateTimeOffset now = time.GetUtcNow();
        if (epoch > 0)
        {
            ReadOnlyMemory<byte> json = await media.GetAsync(ReportArea.Pending, Path(prefix, epoch - 1), MaxClaimBytes, ct)
                .ConfigureAwait(false) ?? throw new InvalidDataException("The latest work claim is missing.");
            Claim latest = JsonSerializer.Deserialize<Claim>(json.Span, RelayJson.Options)
                ?? throw new JsonException("The work claim is null.");
            if (now - latest.ClaimedAt < TimeSpan.FromMinutes(LeaseMinutes)) return false;
        }
        string path = Path(prefix, epoch);
        return await media.PutAsync(ReportArea.Pending, path, JsonSerializer.SerializeToUtf8Bytes(new Claim(now), RelayJson.Options),
            ReportLayout.HeadersFor(path), overwrite: false, ct).ConfigureAwait(false);
    }

    static string Path(string prefix, int epoch) => prefix + epoch.ToString(CultureInfo.InvariantCulture) + ".json";

    sealed record Claim(DateTimeOffset ClaimedAt);
}
