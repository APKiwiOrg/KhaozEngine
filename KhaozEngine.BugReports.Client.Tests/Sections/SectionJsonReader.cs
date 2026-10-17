using System;
using System.Buffers;
using System.Text.Json;
using KhaozEngine.BugReports;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Client.Sections;

/// <summary>Runs one section's <c>Write</c> into a buffer and parses what it wrote.</summary>
static class SectionJsonReader
{
    static readonly BugReportContext Context = new("0192f1c2a3b47c4d8e9f0a1b2c3d4e5f", DateTimeOffset.UnixEpoch);

    public static JsonElement Write(IBugReportSection section)
    {
        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output))
            section.Write(writer, Context);
        using JsonDocument document = JsonDocument.Parse(output.WrittenMemory);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        return document.RootElement.Clone();
    }
}
