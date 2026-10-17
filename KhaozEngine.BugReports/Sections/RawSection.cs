using System;

namespace KhaozEngine.BugReports;

/// <summary>
/// A section exactly as <see cref="BugReportSectionSet.CaptureAll"/> captured it, before redaction and truncation.
/// Pass it to <see cref="BugReportSectionSet.Finish"/> before anything leaves the machine.
/// </summary>
/// <param name="Key">The section key.</param>
/// <param name="MaxBytes">The section's own byte budget.</param>
/// <param name="Json">The unredacted UTF-8 JSON object the section wrote, or its error object.</param>
/// <param name="IsError">True when the section failed and <paramref name="Json"/> is <c>{"error":"..."}</c>.</param>
public sealed record RawSection(string Key, int MaxBytes, ReadOnlyMemory<byte> Json, bool IsError);
