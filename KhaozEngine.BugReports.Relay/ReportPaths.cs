namespace KhaozEngine.BugReports.Relay;

/// <summary>The path rule every <see cref="IReportMediaStore"/> implementation enforces inside a storage area.</summary>
public static class ReportPaths
{
    /// <summary>
    /// True when <paramref name="path"/> is one or more <c>/</c> separated segments of <c>[a-z0-9._-]</c>, with no
    /// leading or trailing <c>/</c>, no empty segment and no <c>.</c> or <c>..</c> segment.
    /// </summary>
    public static bool IsValid(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        foreach (string segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
                return false;
            foreach (char c in segment)
                if (c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-'))
                    return false;
        }

        return true;
    }
}
