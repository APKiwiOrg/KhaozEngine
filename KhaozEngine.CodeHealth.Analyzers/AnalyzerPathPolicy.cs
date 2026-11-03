using System;

namespace KhaozEngine.CodeHealth.Analyzers
{
    /// <summary>
    /// Path rules shared by the baseline-driven analyzers. Separators normalise to '/', a source path is
    /// checked relative to the directory that holds its baseline file, and generated or vendored code is
    /// never checked. The exclusions mirror is_excluded in check-file-size.sh, the semantic authority for
    /// KESIZE, so both ratchets agree on which files are ours to shrink.
    /// </summary>
    internal static class AnalyzerPathPolicy
    {
        public static string NormalizeSeparators(string path) => path.Replace('\\', '/');

        /// <summary>
        /// Gives the path of <paramref name="path"/> relative to <paramref name="root"/>, and true, when the
        /// file lies under that root and is not excluded. <paramref name="root"/> is already normalised.
        /// </summary>
        public static bool TryGetCheckedRelativePath(string path, string root, out string relative)
        {
            relative = string.Empty;
            if (string.IsNullOrEmpty(path)) return false;

            var full = NormalizeSeparators(path);
            var prefixLength = 0;
            while (prefixLength < full.Length && full[prefixLength] == '/') prefixLength++;

            var segments = full.Substring(prefixLength).Split('/');
            var segmentCount = 0;
            foreach (var segment in segments)
            {
                if (segment.Length == 0 || segment == ".") continue;
                if (segment == "..")
                {
                    if (segmentCount == 0) return false;
                    segmentCount--;
                }
                else
                {
                    segments[segmentCount++] = segment;
                }
            }
            full = full.Substring(0, prefixLength) + string.Join("/", segments, 0, segmentCount);
            if (!full.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)) return false;

            relative = full.Substring(root.Length + 1);
            return !IsExcluded(relative);
        }

        private static bool IsExcluded(string relative)
        {
            if (relative.StartsWith("obj/", StringComparison.Ordinal) ||
                relative.StartsWith("bin/", StringComparison.Ordinal) ||
                relative.StartsWith("vendor/", StringComparison.Ordinal) ||
                relative.Contains("/obj/") ||
                relative.Contains("/bin/") ||
                relative.Contains("/vendor/"))
            {
                return true;
            }
            return relative.EndsWith(".Designer.cs", StringComparison.Ordinal) ||
                   relative.EndsWith(".g.cs", StringComparison.Ordinal) ||
                   relative.EndsWith(".generated.cs", StringComparison.Ordinal) ||
                   relative.EndsWith(".AssemblyInfo.cs", StringComparison.Ordinal);
        }
    }
}
