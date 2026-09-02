using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace KhaozEngine.Automation
{
    /// <summary>
    /// One parsed request line. The wire form is a single JSON object per line:
    /// <c>{"id":7,"token":"...","cmd":"input","x":100,"y":200,"button":"left","action":"press","holdFrames":3}</c>.
    /// <para>
    /// <c>id</c> and <c>cmd</c> are the only universal fields. Everything else is command specific and is read off
    /// <see cref="Payload"/>, which is the whole request object detached from the parsing document, so it stays
    /// valid after the line has been read.
    /// </para>
    /// </summary>
    public sealed class AutomationRequest
    {
        /// <summary>The caller's correlation id, echoed on the reply. Zero when the line carried none.</summary>
        public long Id { get; }

        /// <summary>The command name, lower-cased: <c>input</c>, <c>step</c>, <c>state</c>, <c>call</c>, <c>quit</c>, <c>ping</c>.</summary>
        public string Command { get; }

        /// <summary>The per-run token the caller presented, or null when the line carried none.</summary>
        public string? Token { get; }

        /// <summary>The whole request object, detached from the parsing document. Command arguments are read from here.</summary>
        public JsonElement Payload { get; }

        AutomationRequest(long id, string command, string? token, JsonElement payload)
        {
            Id = id;
            Command = command;
            Token = token;
            Payload = payload;
        }

        /// <summary>
        /// Build a request directly, for an in-process caller driving <see cref="AutomationHost.Submit"/> without a
        /// socket (the headless tests do exactly this). <paramref name="payload"/> supplies the command arguments and
        /// may be <c>default</c> when the command takes none.
        /// </summary>
        public static AutomationRequest Create(long id, string command, JsonElement payload = default) =>
            new(id, command, null, payload);

        /// <summary>
        /// Parse one wire line. Returns false with a human-readable <paramref name="error"/> for anything that is not
        /// a JSON object carrying a non-empty string <c>cmd</c>. A false here is a MALFORMED request, which the
        /// connection answers with an error reply and stays open for. It is not an authentication failure.
        /// </summary>
        public static bool TryParse(
            string line, [NotNullWhen(true)] out AutomationRequest? request, out string? error)
        {
            request = null;
            error = null;
            if (string.IsNullOrWhiteSpace(line)) { error = "empty request line"; return false; }

            JsonElement payload;
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    error = "request must be a JSON object";
                    return false;
                }
                payload = document.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                error = "malformed JSON: " + ex.Message;
                return false;
            }

            if (!payload.TryGetProperty("cmd", out JsonElement command) || command.ValueKind != JsonValueKind.String)
            {
                error = "request is missing a string 'cmd'";
                return false;
            }
            string name = command.GetString() ?? "";
            if (name.Length == 0) { error = "request 'cmd' is empty"; return false; }

            long id = 0;
            if (payload.TryGetProperty("id", out JsonElement idElement) && idElement.ValueKind != JsonValueKind.Null &&
                (idElement.ValueKind != JsonValueKind.Number || !idElement.TryGetInt64(out id)))
            {
                error = "request 'id' is not an integer";
                return false;
            }

            string? token = payload.TryGetProperty("token", out JsonElement tokenElement) &&
                            tokenElement.ValueKind == JsonValueKind.String
                ? tokenElement.GetString()
                : null;

            request = new AutomationRequest(id, name.ToLowerInvariant(), token, payload);
            return true;
        }

        /// <summary>The named property, or <c>default</c> (a <see cref="JsonValueKind.Undefined"/> element) when absent.</summary>
        public JsonElement Argument(string name) =>
            Payload.ValueKind == JsonValueKind.Object && Payload.TryGetProperty(name, out JsonElement value)
                ? value
                : default;

        /// <summary>Read an optional integer argument. Returns false only when the property is present and not an integer.</summary>
        public bool TryReadInt(string name, out int value, out string? error)
        {
            value = 0;
            error = null;
            JsonElement element = Argument(name);
            if (element.ValueKind == JsonValueKind.Undefined || element.ValueKind == JsonValueKind.Null) return true;
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out value))
            {
                error = "'" + name + "' is not an integer";
                return false;
            }
            return true;
        }

        /// <summary>Read an optional number argument as a float. Returns false only when the property is present and not a number.</summary>
        public bool TryReadFloat(string name, out float value, out string? error)
        {
            value = 0f;
            error = null;
            JsonElement element = Argument(name);
            if (element.ValueKind == JsonValueKind.Undefined || element.ValueKind == JsonValueKind.Null) return true;
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetSingle(out value))
            {
                error = "'" + name + "' is not a number";
                return false;
            }
            return true;
        }

        /// <summary>Read an optional string argument. Returns null when absent, null-valued, or not a string.</summary>
        public string? ReadString(string name)
        {
            JsonElement element = Argument(name);
            return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        }

        /// <summary>True when the named argument is present and is JSON <c>true</c>.</summary>
        public bool ReadFlag(string name) => Argument(name).ValueKind == JsonValueKind.True;
    }
}
