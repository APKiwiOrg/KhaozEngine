using System;

namespace KhaozEngine.CodeHealth.Analyzers
{
    /// <summary>
    /// Path rules shared by the baseline-driven analyzers. Both source paths and baseline roots collapse
    /// separators and dot segments before comparison. A source path is checked relative to the directory
    /// that holds its baseline file, and generated or vendored code is never checked. The exclusions mirror
    /// is_excluded in check-file-size.sh, the semantic authority for KESIZE, so both ratchets agree on which
    /// files are ours to shrink.
    /// </summary>
    internal static class AnalyzerPathPolicy
    {
        public static string NormalizeSeparators(string path) => path.Replace('\\', '/');

        /// <summary>
        /// Gives the path of <paramref name="path"/> relative to <paramref name="root"/>, and true, when the
        /// file lies under that root and is not excluded. Both operands are normalised before comparison.
        /// </summary>
        public static bool TryGetCheckedRelativePath(string path, string root, out string relative)
        {
            relative = string.Empty;
            if (string.IsNullOrEmpty(path)) return false;
            if (!TryNormalizePath(path, out var full) || !TryNormalizePath(root, out var normalizedRoot)) return false;

            var prefix = normalizedRoot.EndsWith("/", StringComparison.Ordinal) ? normalizedRoot : normalizedRoot + "/";
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

            relative = full.Substring(prefix.Length);
            return !IsExcluded(relative);
        }

        private static bool TryNormalizePath(string path, out string normalized)
        {
            normalized = string.Empty;
            var full = NormalizeSeparators(path);
            var prefixLength = full.Length > 0 && full[0] == '/' ? 1 : 0;

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
            normalized = full.Substring(0, prefixLength) + string.Join("/", segments, 0, segmentCount);
            return true;
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
