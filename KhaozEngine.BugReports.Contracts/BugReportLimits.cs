namespace KhaozEngine.BugReports.Contracts;

public static class BugReportLimits
{
    public const long MaxBundleBytes = 16 * 1024 * 1024;
    public const int MaxEntries = 128;
    public const int MaxSectionBytesTotal = 1024 * 1024;
    public const int MaxDescriptionChars = 4000;
    public const int MaxTitleChars = 70;
    public const int MaxScreenshotLongEdge = 2560;
    public const int MaxServerSnapshotBytes = 1024 * 1024;
    public const int MaxPngScreenshotBytes = 4 * 1024 * 1024;
}
