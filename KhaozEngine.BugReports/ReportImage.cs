using System;

namespace KhaozEngine.BugReports;

/// <summary>An encoded still image attached to a report, such as the trigger frame's screenshot.</summary>
/// <param name="MediaType"><c>"image/png"</c> or <c>"image/jpeg"</c>.</param>
/// <param name="Width">Pixel width of the encoded image.</param>
/// <param name="Height">Pixel height of the encoded image.</param>
/// <param name="Bytes">The encoded file bytes.</param>
public sealed record ReportImage(string MediaType, int Width, int Height, ReadOnlyMemory<byte> Bytes);
