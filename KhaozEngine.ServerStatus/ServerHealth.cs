using System;
using System.Text.Json;
using System.Text.Json.Serialization;

#nullable enable

namespace KhaozEngine.ServerStatus;

/// <summary>
/// Coarse health of the live game service, as reported by the out-of-band status endpoint. The endpoint
/// derives this from the server's last heartbeat age plus the deploy window (see the package README), so a
/// client learns whether to reconnect, wait, or update without touching the game server process.
/// </summary>
public enum ServerHealth
{
    /// <summary>Health could not be determined (endpoint said so, or the client has no fresh report).</summary>
    Unknown = 0,

    /// <summary>Server is up and accepting players.</summary>
    Healthy = 1,

    /// <summary>Server is mid-deploy / restarting inside a known downtime window (see ExpectedBackUtc).</summary>
    Restarting = 2,

    /// <summary>Server is down outside any planned window (heartbeat stale with no deploy in progress).</summary>
    Down = 3,
}

/// <summary>
/// Tolerant JSON converter for <see cref="ServerHealth"/>: reads the lowercase wire token
/// (<c>healthy</c> / <c>restarting</c> / <c>down</c> / <c>unknown</c>) case-insensitively and maps any
/// unrecognized, missing, or non-string token to <see cref="ServerHealth.Unknown"/> rather than throwing,
/// so a client built before a future health value never fails to parse a report. Writes the lowercase token.
/// </summary>
internal sealed class ServerHealthJsonConverter : JsonConverter<ServerHealth>
{
    public override ServerHealth Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Tolerant read: only a string token is meaningful; anything else degrades to Unknown (never throws).
        if (reader.TokenType != JsonTokenType.String)
        {
            return ServerHealth.Unknown;
        }

        string? token = reader.GetString();
        return token?.ToLowerInvariant() switch
        {
            "healthy" => ServerHealth.Healthy,
            "restarting" => ServerHealth.Restarting,
            "down" => ServerHealth.Down,
            _ => ServerHealth.Unknown,
        };
    }

    public override void Write(Utf8JsonWriter writer, ServerHealth value, JsonSerializerOptions options)
    {
        string token = value switch
        {
            ServerHealth.Healthy => "healthy",
            ServerHealth.Restarting => "restarting",
            ServerHealth.Down => "down",
            _ => "unknown",
        };
        writer.WriteStringValue(token);
    }
}
