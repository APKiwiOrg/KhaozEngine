using System;
using System.Collections.Generic;
using System.Text.Json;

namespace KhaozEngine.BugReports.Client;

/// <summary>
/// The <c>graphics</c> section: <c>{backend, adapter, swapchain:{width,height}, presentMode, softwareAdapter,
/// deviceLossReason, injectedModules, capabilities:{...}}</c>. A fact the backend does not report is written as null, and
/// an empty <c>injectedModules</c> array means the scan ran and found nothing.
/// </summary>
/// <param name="facts">Returns the graphics facts. Called in the trigger frame, so it must do no I/O.</param>
public sealed class GraphicsSection(Func<GraphicsFacts> facts) : IBugReportSection
{
    /// <inheritdoc />
    public string Key => "graphics";

    /// <inheritdoc />
    public int MaxBytes => 8 * 1024;

    /// <inheritdoc />
    public void Write(Utf8JsonWriter writer, BugReportContext context)
    {
        GraphicsFacts graphics = facts();
        writer.WriteStartObject();
        writer.WriteString("backend", graphics.Backend);
        writer.WriteString("adapter", graphics.Adapter);
        PlatformSection.WriteSize(writer, "swapchain", graphics.SwapchainWidth, graphics.SwapchainHeight);
        writer.WriteString("presentMode", graphics.PresentMode);
        if (graphics.SoftwareAdapter is bool software) writer.WriteBoolean("softwareAdapter", software);
        else writer.WriteNull("softwareAdapter");
        writer.WriteString("deviceLossReason", graphics.DeviceLossReason);
        if (graphics.InjectedModules is null)
        {
            writer.WriteNull("injectedModules");
        }
        else
        {
            writer.WriteStartArray("injectedModules");
            foreach (string module in graphics.InjectedModules)
                writer.WriteStringValue(module);
            writer.WriteEndArray();
        }
        writer.WriteStartObject("capabilities");
        foreach (KeyValuePair<string, string> capability in graphics.Capabilities)
            writer.WriteString(capability.Key, capability.Value);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}
