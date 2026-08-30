using System;
using System.IO;
using System.Text.Json;
using KhaozEngine.Social;

namespace KhaozEngine.Social.Discord.Internal;

/// <summary>
/// Builds the JSON command bodies the Discord IPC socket expects and parses the dispatches it sends
/// back. Uses System.Text.Json only (no third-party JSON). All parsing is defensive: malformed or
/// unexpected payloads return false rather than throwing.
/// </summary>
internal static class DiscordIpcPayloads
{
    public static string Handshake(string clientId)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 1);
            w.WriteString("client_id", clientId ?? string.Empty);
            w.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string Subscribe(string evt, string nonce)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("cmd", "SUBSCRIBE");
            w.WriteString("evt", evt);
            w.WriteString("nonce", nonce);
            w.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string SetActivity(int pid, in RichPresence presence, string nonce)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("cmd", "SET_ACTIVITY");
            w.WriteString("nonce", nonce);

            w.WriteStartObject("args");
            w.WriteNumber("pid", pid);

            w.WriteStartObject("activity");
            WriteIfPresent(w, "details", presence.Details);
            WriteIfPresent(w, "state", presence.State);

            if (presence.StartTimestampUtc is not null || presence.EndTimestampUtc is not null)
            {
                w.WriteStartObject("timestamps");
                if (presence.StartTimestampUtc is { } s)
                {
                    w.WriteNumber("start", ToUnixSeconds(s));
                }

                if (presence.EndTimestampUtc is { } e)
                {
                    w.WriteNumber("end", ToUnixSeconds(e));
                }

                w.WriteEndObject();
            }

            if (!string.IsNullOrEmpty(presence.Party.Id) || presence.Party.Max > 0)
            {
                w.WriteStartObject("party");
                WriteIfPresent(w, "id", presence.Party.Id);
                if (presence.Party.Max > 0)
                {
                    w.WriteStartArray("size");
                    w.WriteNumberValue(presence.Party.Size);
                    w.WriteNumberValue(presence.Party.Max);
                    w.WriteEndArray();
                }

                w.WriteEndObject();
            }

            if (HasImage(presence.LargeImage) || HasImage(presence.SmallImage))
            {
                w.WriteStartObject("assets");
                WriteIfPresent(w, "large_image", presence.LargeImage.Key);
                WriteIfPresent(w, "large_text", presence.LargeImage.Text);
                WriteIfPresent(w, "small_image", presence.SmallImage.Key);
                WriteIfPresent(w, "small_text", presence.SmallImage.Text);
                w.WriteEndObject();
            }

            if (!string.IsNullOrEmpty(presence.JoinSecret) || !string.IsNullOrEmpty(presence.SpectateSecret))
            {
                w.WriteStartObject("secrets");
                WriteIfPresent(w, "join", presence.JoinSecret);
                WriteIfPresent(w, "spectate", presence.SpectateSecret);
                w.WriteEndObject();
            }

            if (presence.Buttons is { Count: > 0 } buttons)
            {
                w.WriteStartArray("buttons");
                int count = 0;
                foreach (PresenceButton b in buttons)
                {
                    if (count++ == 2)
                    {
                        break; // Discord allows at most two.
                    }

                    w.WriteStartObject();
                    w.WriteString("label", b.Label);
                    w.WriteString("url", b.Url);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
            }

            w.WriteEndObject(); // activity
            w.WriteEndObject(); // args
            w.WriteEndObject(); // root
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Build a SET_ACTIVITY that CLEARS presence by sending a null activity. Discord clears on a null
    /// (or omitted) activity; an empty activity object does not reliably clear, so this is distinct from
    /// <see cref="SetActivity"/> with a default presence.
    /// </summary>
    public static string ClearActivity(int pid, string nonce)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("cmd", "SET_ACTIVITY");
            w.WriteString("nonce", nonce);
            w.WriteStartObject("args");
            w.WriteNumber("pid", pid);
            w.WriteNull("activity");
            w.WriteEndObject(); // args
            w.WriteEndObject(); // root
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Build the ACCEPT answer to an inbound ask-to-join: Discord invites the requester into the local
    /// player's activity. Note there is NO pid in the args, unlike the SET_ACTIVITY family - the reply
    /// names only the user whose request is being answered.
    /// </summary>
    public static string AcceptJoinRequest(string userId, string nonce)
        => JoinRequestReply("SEND_ACTIVITY_JOIN_INVITE", userId, nonce);

    /// <summary>
    /// Build the REJECT answer to an inbound ask-to-join, which closes the request on Discord's side so
    /// the asking friend stops waiting. Same envelope as <see cref="AcceptJoinRequest"/>, different cmd.
    /// </summary>
    public static string RejectJoinRequest(string userId, string nonce)
        => JoinRequestReply("CLOSE_ACTIVITY_REQUEST", userId, nonce);

    private static string JoinRequestReply(string cmd, string userId, string nonce)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("cmd", cmd);
            w.WriteString("nonce", nonce);
            w.WriteStartObject("args");
            w.WriteString("user_id", userId ?? string.Empty);
            w.WriteEndObject(); // args
            w.WriteEndObject(); // root
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public static bool TryParseDispatch(string json, out string eventName, out string dataJson)
    {
        eventName = string.Empty;
        dataJson = string.Empty;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("evt", out JsonElement evt) || evt.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            eventName = evt.GetString() ?? string.Empty;
            dataJson = root.TryGetProperty("data", out JsonElement data) ? data.GetRawText() : "{}";
            return eventName.Length > 0;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    public static bool TryParseReadyUser(string json, out SocialUser user)
    {
        user = default;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("user", out JsonElement u))
            {
                return TryReadUser(u, out user);
            }

            return false;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    public static bool TryParseJoinRequestUser(string dataJson, out SocialUser user)
    {
        user = default;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(dataJson);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("user", out JsonElement u))
            {
                return TryReadUser(u, out user);
            }

            return false;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    public static bool TryParseJoinSecret(string dataJson, out string secret)
    {
        secret = string.Empty;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(dataJson);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("secret", out JsonElement s) && s.ValueKind == JsonValueKind.String)
            {
                secret = s.GetString() ?? string.Empty;
                return secret.Length > 0;
            }

            return false;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryReadUser(JsonElement u, out SocialUser user)
    {
        user = default;
        if (u.ValueKind != JsonValueKind.Object
            || !u.TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.String
            || !u.TryGetProperty("username", out JsonElement name) || name.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string? global = u.TryGetProperty("global_name", out JsonElement g) && g.ValueKind == JsonValueKind.String
            ? g.GetString()
            : null;
        user = new SocialUser(id.GetString() ?? string.Empty, name.GetString() ?? string.Empty, global);
        return user.Id.Length > 0 && user.Username.Length > 0;
    }

    private static void WriteIfPresent(Utf8JsonWriter w, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            w.WriteString(name, value);
        }
    }

    private static bool HasImage(PresenceImage image) =>
        !string.IsNullOrEmpty(image.Key) || !string.IsNullOrEmpty(image.Text);

    private static long ToUnixSeconds(DateTime value)
    {
        DateTime utc = value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();
        return ((DateTimeOffset)utc).ToUnixTimeSeconds();
    }
}
