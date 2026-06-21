using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KhaozEngine.Serialization;

/// <summary>
/// The engine-wide JSONC (JSON with <c>//</c> and <c>/* */</c> comments and trailing commas) read policy.
/// This is the single source of truth every KhaozEngine package routes hand-authored JSON loads through,
/// so config, content manifests, settings, and saves can all carry inline comments and trailing commas.
///
/// <para>JSONC is a <b>read-time</b> convenience. System.Text.Json cannot emit comments, so the engine never
/// writes JSONC: generated files (settings, saves, signed manifests) are plain JSON, while hand-authored files
/// keep their comments because the engine only reads them, never rewrites them in place. Use
/// <see cref="JsonDefaults.IndentedWrite"/> for the human-readable write side.</para>
///
/// <para>The three accessors mirror the three System.Text.Json entry points, which each take a different options
/// type: <see cref="Options"/> for <see cref="JsonSerializer"/>, <see cref="DocumentOptions"/> for
/// <see cref="JsonDocument"/>, and <see cref="NodeOptions"/> for <see cref="JsonNode"/>. Each returns a single
/// shared, effectively read-only instance.</para>
/// </summary>
public static class Jsonc
{
    /// <summary>JSONC policy for <see cref="JsonSerializer"/>: case-insensitive property names, <c>//</c> and
    /// <c>/* */</c> comments skipped, and trailing commas allowed. <see cref="JsonDefaults.TolerantRead"/> is the
    /// same instance.</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>JSONC policy for <see cref="JsonDocument.Parse(string, JsonDocumentOptions)"/>: comments skipped,
    /// trailing commas allowed.</summary>
    public static JsonDocumentOptions DocumentOptions { get; } = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>JSONC policy for <see cref="JsonNode"/> reads (case-insensitive property lookups). Comment and
    /// trailing-comma handling on a <see cref="JsonNode.Parse(string, JsonNodeOptions?, JsonDocumentOptions)"/>
    /// call comes from the <see cref="JsonDocumentOptions"/> argument, so pass <see cref="DocumentOptions"/>
    /// alongside this; <see cref="ParseNode"/> wires both for you.</summary>
    public static JsonNodeOptions NodeOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Deserializes <typeparamref name="T"/> from a JSONC string using <see cref="Options"/>.</summary>
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>Reads <paramref name="path"/> from disk and deserializes <typeparamref name="T"/> as JSONC.</summary>
    public static T? DeserializeFile<T>(string path) => Deserialize<T>(File.ReadAllText(path));

    /// <summary>Parses a JSONC string into a <see cref="JsonDocument"/> using <see cref="DocumentOptions"/>.
    /// The caller owns the returned document and must dispose it.</summary>
    public static JsonDocument ParseDocument(string json) => JsonDocument.Parse(json, DocumentOptions);

    /// <summary>Parses a JSONC string into a <see cref="JsonNode"/> tree, wiring both <see cref="NodeOptions"/> and
    /// <see cref="DocumentOptions"/> so comments and trailing commas are accepted. Returns null for the literal
    /// <c>null</c> document.</summary>
    public static JsonNode? ParseNode(string json) => JsonNode.Parse(json, NodeOptions, DocumentOptions);
}
