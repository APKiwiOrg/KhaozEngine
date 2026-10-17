using System;
using System.Text.Json;

namespace KhaozEngine.BugReports.Client;

/// <summary>The <c>input</c> section: <c>{devices:[...], trigger:[...]}</c>, one string per device and per trigger binding.</summary>
/// <param name="facts">Returns the input facts. Called in the trigger frame, so it must do no I/O.</param>
public sealed class InputSection(Func<InputFacts> facts) : IBugReportSection
{
    /// <inheritdoc />
    public string Key => "input";

    /// <inheritdoc />
    public int MaxBytes => 4 * 1024;

    /// <inheritdoc />
    public void Write(Utf8JsonWriter writer, BugReportContext context)
    {
        InputFacts input = facts();
        writer.WriteStartObject();
        writer.WriteStartArray("devices");
        foreach (string device in input.Devices)
            writer.WriteStringValue(device);
        writer.WriteEndArray();
        writer.WriteStartArray("trigger");
        foreach (string binding in input.TriggerBindings)
            writer.WriteStringValue(binding);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
