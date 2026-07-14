using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

#nullable enable

namespace KhaozEngine.ServerStatus;

/// <summary>
/// The versioned, tolerant-read status payload a game's out-of-band status endpoint serves and the
/// <see cref="ServerStatusClient"/> polls. This type IS the cross-repo wire contract: the game-template
/// Azure Function implements against it (see the package README for the canonical JSON example). Tolerant
/// read is deliberate so the endpoint can evolve ahead of shipped clients - unknown fields are ignored,
/// missing optional fields fall back to their defaults, and an unrecognized <see cref="Health"/> token
/// degrades to <see cref="ServerHealth.Unknown"/> instead of failing the parse.
/// </summary>
public sealed record ServerStatusReport
{
    /// <summary>Contract schema version. Bump only on a breaking shape change: additive fields do not bump it.</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Coarse service health the endpoint derived from heartbeat age + the deploy window.</summary>
    [JsonPropertyName("health")]
    public ServerHealth Health { get; init; } = ServerHealth.Unknown;

    /// <summary>Version of the currently deployed game server build (x.y.z), or empty if unknown.</summary>
    [JsonPropertyName("serverVersion")]
    public string ServerVersion { get; init; } = "";

    /// <summary>Lowest client version the server will accept (x.y.z). A client below this must update. Empty = no floor.</summary>
    [JsonPropertyName("minClientVersion")]
    public string MinClientVersion { get; init; } = "";

    /// <summary>Newest published client version (x.y.z). A client below this may update but can still play. Empty = unknown.</summary>
    [JsonPropertyName("latestClientVersion")]
    public string LatestClientVersion { get; init; } = "";

    /// <summary>UTC instant of the server's last liveness heartbeat, as the endpoint saw it in the status DB.</summary>
    [JsonPropertyName("lastHeartbeatUtc")]
    public DateTimeOffset LastHeartbeatUtc { get; init; }

    /// <summary>UTC instant CI/CD last wrote a deploy record for the current build.</summary>
    [JsonPropertyName("lastDeployUtc")]
    public DateTimeOffset LastDeployUtc { get; init; }

    /// <summary>When a restart is expected to finish, when known (drives the "back soon" countdown). Null otherwise.</summary>
    [JsonPropertyName("expectedBackUtc")]
    public DateTimeOffset? ExpectedBackUtc { get; init; }

    /// <summary>Optional operator message-of-the-day. The engine never renders it: a game localizes/echoes it as it sees fit.</summary>
    [JsonPropertyName("motd")]
    public string? Motd { get; init; }

    /// <summary>
    /// Shared serializer options for the contract: tolerant read (case-insensitive names, comments and
    /// trailing commas skipped) plus the <see cref="ServerHealth"/> converter. A single frozen instance,
    /// since System.Text.Json locks options on first use.
    /// </summary>
    private static readonly JsonSerializerOptions ContractJson = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = false,
        Converters = { new ServerHealthJsonConverter() },
    };

    private static readonly JsonSerializerOptions ContractJsonIndented = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new ServerHealthJsonConverter() },
    };

    /// <summary>Serializes this report to the contract JSON. <paramref name="indented"/> is for readable fixtures/logs.</summary>
    public string ToJson(bool indented = false) =>
        JsonSerializer.Serialize(this, indented ? ContractJsonIndented : ContractJson);

    /// <summary>
    /// Tolerantly parses a status report from UTF-8 JSON bytes. Returns null on malformed/garbage input or a
    /// null JSON literal (never throws), so a poller can treat "no valid report" uniformly with a transport
    /// failure. Unknown fields are ignored and missing optionals default.
    /// </summary>
    public static ServerStatusReport? TryParse(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            return JsonSerializer.Deserialize<ServerStatusReport>(utf8Json, ContractJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>String overload of <see cref="TryParse(ReadOnlySpan{byte})"/>.</summary>
    public static ServerStatusReport? TryParse(string json)
    {
        if (json is null)
        {
            return null;
        }

        return TryParse(Encoding.UTF8.GetBytes(json));
    }
}
