using System;

namespace KhaozEngine.BugReports;

/// <summary>
/// A finished section from <see cref="BugReportSectionSet.Finish"/>: redacted, truncated to its budget and inside
/// the 1 MiB section total. Only finished sections reach a draft or a bundle.
/// </summary>
/// <param name="Key">The section key.</param>
/// <param name="Json">One compact UTF-8 JSON object.</param>
public sealed record CapturedSection(string Key, ReadOnlyMemory<byte> Json);
