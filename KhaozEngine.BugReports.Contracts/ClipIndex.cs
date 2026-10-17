using System.Collections.Generic;

namespace KhaozEngine.BugReports.Contracts;

public sealed record ClipIndexFrame(double OffsetMilliseconds, double RenderMilliseconds, int Width, int Height, string Entry);

public sealed record ClipIndex(IReadOnlyList<ClipIndexFrame> Frames);
