using System.Collections.Generic;

namespace KhaozEngine.BugReports;

/// <summary>The rolling clip frozen at report time, oldest frame first. An empty clip has an empty list, never null.</summary>
/// <param name="Frames">The frames. Each <see cref="ReportClipFrame.Offset"/> is measured from the first frame.</param>
public sealed record ReportClip(IReadOnlyList<ReportClipFrame> Frames);
