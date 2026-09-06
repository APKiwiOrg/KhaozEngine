using System;
using System.Collections.Generic;

namespace KhaozEngine.Gui;

/// <summary>One label/value line in a <see cref="DiagnosticsOverlay"/> section (e.g. <c>("ping", "48 ms")</c>).</summary>
public readonly record struct OverlayRow(string Label, string Value);

/// <summary>
/// A titled group of <see cref="OverlayRow"/>s rendered as a block in the <see cref="DiagnosticsOverlay"/>
/// panel. The game assembles these each frame (the metric catalog stays game-owned); the engine ships the
/// <see cref="DiagnosticsOverlay.PerformanceSection"/> / <c>DiagnosticsOverlay.NetworkSection</c>
/// convenience populators for the common cases.
/// </summary>
public sealed class OverlaySection
{
    /// <summary>The section heading (e.g. "Network").</summary>
    public string Title { get; }

    /// <summary>The rows under the heading, in display order.</summary>
    public IReadOnlyList<OverlayRow> Rows { get; }

    public OverlaySection(string title, IReadOnlyList<OverlayRow> rows)
    {
        Title = title ?? string.Empty;
        Rows = rows ?? Array.Empty<OverlayRow>();
    }
}
