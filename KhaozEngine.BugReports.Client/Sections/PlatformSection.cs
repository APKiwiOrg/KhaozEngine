using System;
using System.Text.Json;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.BugReports.Client;

/// <summary>
/// The <c>platform</c> section: <c>{os, osArchitecture, processArchitecture, runtime, cpuCount, totalMemoryBytes, locale,
/// window:{width,height}, framebuffer:{width,height}, contentScale, env:{NAME:value}}</c>. <c>env</c> holds exactly the
/// variables the facts carry, so admission and masking stay with <see cref="ReportRedactor.SelectEnvironment"/>.
/// </summary>
/// <param name="facts">Returns the platform facts. Called in the trigger frame, so it must do no I/O.</param>
public sealed class PlatformSection(Func<PlatformFacts> facts) : IBugReportSection
{
    /// <inheritdoc />
    public string Key => "platform";

    /// <inheritdoc />
    public int MaxBytes => 8 * 1024;

    /// <inheritdoc />
    public void Write(Utf8JsonWriter writer, BugReportContext context)
    {
        PlatformFacts platform = facts();
        writer.WriteStartObject();
        writer.WriteString("os", platform.Os);
        writer.WriteString("osArchitecture", platform.OsArchitecture);
        writer.WriteString("processArchitecture", platform.ProcessArchitecture);
        writer.WriteString("runtime", platform.Runtime);
        writer.WriteNumber("cpuCount", platform.CpuCount);
        writer.WriteNumber("totalMemoryBytes", platform.TotalMemoryBytes);
        writer.WriteString("locale", platform.Locale);
        WriteSize(writer, "window", platform.WindowWidth, platform.WindowHeight);
        WriteSize(writer, "framebuffer", platform.FramebufferWidth, platform.FramebufferHeight);
        if (float.IsFinite(platform.ContentScale)) writer.WriteNumber("contentScale", platform.ContentScale);
        else writer.WriteNull("contentScale");
        writer.WriteStartObject("env");
        foreach (TelemetryHeaderValue variable in platform.Environment)
            writer.WriteString(variable.Key, variable.Value);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    /// <summary>Writes <c>"name":{"width":w,"height":h}</c>.</summary>
    internal static void WriteSize(Utf8JsonWriter writer, string name, int width, int height)
    {
        writer.WriteStartObject(name);
        writer.WriteNumber("width", width);
        writer.WriteNumber("height", height);
        writer.WriteEndObject();
    }
}
