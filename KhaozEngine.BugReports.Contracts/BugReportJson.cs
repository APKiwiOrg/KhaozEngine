using System;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace KhaozEngine.BugReports.Contracts;

public static class BugReportJson
{
    static readonly BugReportJsonContext Context = CreateContext();

    public static JsonSerializerOptions Options => Context.Options;

    // The parse rules for one section entry: depth at most 64 and no repeated property name in any object.
    public static JsonDocumentOptions SectionDocumentOptions { get; } =
        new() { MaxDepth = BugReportLimits.MaxSectionDepth, AllowDuplicateProperties = false };

    public static byte[] SerializeManifest(BugReportManifest manifest)
        => JsonSerializer.SerializeToUtf8Bytes(manifest, Context.BugReportManifest);

    public static BugReportManifest DeserializeManifest(ReadOnlySpan<byte> json)
        => JsonSerializer.Deserialize(json, Context.BugReportManifest) ?? throw new JsonException("Expected a report manifest.");

    public static byte[] SerializeClipIndex(ClipIndex index)
        => JsonSerializer.SerializeToUtf8Bytes(index, Context.ClipIndex);

    public static ClipIndex DeserializeClipIndex(ReadOnlySpan<byte> json)
        => JsonSerializer.Deserialize(json, Context.ClipIndex) ?? throw new JsonException("Expected a clip index.");

    public static byte[] SerializeIntakeAccepted(IntakeAccepted value)
        => JsonSerializer.SerializeToUtf8Bytes(value, Context.IntakeAccepted);

    public static IntakeAccepted DeserializeIntakeAccepted(ReadOnlySpan<byte> json)
        => JsonSerializer.Deserialize(json, Context.IntakeAccepted) ?? throw new JsonException("Expected an intake acceptance.");

    public static byte[] SerializeRelayError(RelayError value)
        => JsonSerializer.SerializeToUtf8Bytes(value, Context.RelayError);

    public static RelayError DeserializeRelayError(ReadOnlySpan<byte> json)
        => JsonSerializer.Deserialize(json, Context.RelayError) ?? throw new JsonException("Expected a relay error.");

    public static byte[] SerializeServerSnapshot(ServerSnapshotWire value)
        => JsonSerializer.SerializeToUtf8Bytes(value, Context.ServerSnapshotWire);

    // Section values stay undecoded JsonElement values, so their strings are checked separately, at the same
    // depth limit the serializer just applied.
    public static ServerSnapshotWire DeserializeServerSnapshot(ReadOnlySpan<byte> json)
    {
        ServerSnapshotWire snapshot = JsonSerializer.Deserialize(json, Context.ServerSnapshotWire)
            ?? throw new JsonException("Expected a server snapshot.");
        if (!JsonStrings.AllDecode(json, Context.Options.MaxDepth))
            throw new JsonException("The server snapshot holds a string that does not decode.");
        return snapshot;
    }

    static BugReportJsonContext CreateContext()
    {
        var options = new JsonSerializerOptions(BugReportJsonContext.Default.Options)
        {
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
            AllowDuplicateProperties = false,
        };
        options.Converters.Add(new UtcDateTimeOffsetConverter());
        return new BugReportJsonContext(options);
    }

    sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.GetDateTimeOffset();

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToUniversalTime());
    }
}
