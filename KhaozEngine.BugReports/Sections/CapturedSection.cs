using System;

namespace KhaozEngine.BugReports;

/// <summary>
/// A finished section from <see cref="BugReportSectionSet.Finish"/>: redacted, truncated to its budget and inside
/// the 1 MiB section total. Only finished sections reach a draft or a bundle.
/// <para>Only <see cref="BugReportSectionSet.Finish"/> redacts. The record is public so a viewer can hold sections it
/// read back from a bundle, and <see cref="BugReportBundleWriter"/> trusts a hand-built one to be finished output: it
/// checks the key, the JSON shape and the limits, and never redacts.</para>
/// </summary>
/// <param name="Key">The section key.</param>
/// <param name="Json">One compact UTF-8 JSON object.</param>
public sealed record CapturedSection(string Key, ReadOnlyMemory<byte> Json);
