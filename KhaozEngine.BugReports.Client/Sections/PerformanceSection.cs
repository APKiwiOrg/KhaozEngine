using System;
using System.Text.Json;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.BugReports.Client;

/// <summary>
/// The <c>performance</c> section: <c>{fps, frameMsAvg, frameMsMin, frameMsMax, managedBytes,
/// passes:[{name, avgMs, minMs, maxMs}], frameTimesMs:[...]}</c>. Every frame time is its own number, oldest first, and
/// <c>passes</c> is empty when there is no pass meter.
/// </summary>
/// <param name="stats">The rolling frame meter.</param>
/// <param name="frames">The recent frame times.</param>
/// <param name="passes">Returns the pass meter, or null when the game keeps none.</param>
public sealed class PerformanceSection(FrameStats stats, FrameTimeRing frames, Func<PassTimings?> passes) : IBugReportSection
{
    /// <inheritdoc />
    public string Key => "performance";

    /// <inheritdoc />
    public int MaxBytes => 32 * 1024;

    /// <inheritdoc />
    public void Write(Utf8JsonWriter writer, BugReportContext context)
    {
        writer.WriteStartObject();
        writer.WriteNumber("fps", stats.Fps);
        writer.WriteNumber("frameMsAvg", stats.FrameMsAvg);
        writer.WriteNumber("frameMsMin", stats.FrameMsMin);
        writer.WriteNumber("frameMsMax", stats.FrameMsMax);
        writer.WriteNumber("managedBytes", stats.ManagedBytes);
        writer.WriteStartArray("passes");
        PassTimings? timings = passes();
        if (timings is not null)
        {
            foreach (string name in timings.PassNames)
            {
                writer.WriteStartObject();
                writer.WriteString("name", name);
                writer.WriteNumber("avgMs", timings.AvgMs(name));
                writer.WriteNumber("minMs", timings.MinMs(name));
                writer.WriteNumber("maxMs", timings.MaxMs(name));
                writer.WriteEndObject();
            }
        }
        writer.WriteEndArray();
        writer.WriteStartArray("frameTimesMs");
        foreach (float milliseconds in frames.Snapshot())
            writer.WriteNumberValue(milliseconds);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
