using System;

namespace KhaozEngine.CodeHealth.Analyzers
{
    /// <summary>
    /// Path rules shared by the baseline-driven analyzers. Callers normalize baseline-root separators,
    /// then both operands collapse separators and dot segments before comparison. A source path is checked
    /// relative to its baseline directory. Generated and vendored paths keep their existing exclusions.
    /// The exclusions mirror
    /// is_excluded in check-file-size.sh, the semantic authority for KESIZE, so both ratchets agree on which
    /// files are ours to shrink.
    /// </summary>
    internal static class AnalyzerPathPolicy
    {
        public static string NormalizeSeparators(string path) => path.Replace('\\', '/');

        /// <summary>
        /// Gives the path of <paramref name="path"/> relative to <paramref name="root"/>, and true, when the
        /// file lies under that root and is not excluded. Both operands are normalised before comparison,
        /// preserving prior results for excluded inputs and trailing-separator inputs newly deemed generated.
        /// </summary>
        public static bool TryGetCheckedRelativePath(string path, string root, out string relative)
        {
            relative = string.Empty;
            if (string.IsNullOrEmpty(path)) return false;

            // Keep the pre-normalization result when an excluded or unsupported input stays refused.
            // The analyzer callers already normalize the baseline root's separators.
            var original = NormalizeSeparators(path);
            var originalPrefix = root + "/";
            var originallyUnderRoot = original.StartsWith(originalPrefix, StringComparison.OrdinalIgnoreCase);
            if (originallyUnderRoot) relative = original.Substring(originalPrefix.Length);
            var originallyChecked = originallyUnderRoot && !IsExcluded(relative);
            if (root.IndexOf('\\') >= 0) return false;
            if (!TryNormalizePath(path, out var full) || !TryNormalizePath(root, out var normalizedRoot)) return false;

            // An empty root historically denotes the leading separator. A relative root such as "."
            // instead resolves to its own base and must retain an empty, relative prefix.
            if (root.Length == 0) normalizedRoot = "/";
            if (full.StartsWith("/", StringComparison.Ordinal) != normalizedRoot.StartsWith("/", StringComparison.Ordinal))
                return false;
            var prefix = normalizedRoot.Length == 0 || normalizedRoot.EndsWith("/", StringComparison.Ordinal)
                ? normalizedRoot : normalizedRoot + "/";
            string candidate;
            if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                candidate = full.Substring(prefix.Length);
            else if (originallyUnderRoot && string.Equals(full, normalizedRoot, StringComparison.OrdinalIgnoreCase))
                candidate = string.Empty;
            else
                return false;

            if (candidate == ".." || candidate.StartsWith("../", StringComparison.Ordinal)) return false;
            // Collapsing a trailing separator must not newly exclude an input previously checked.
            if (IsExcluded(candidate)) return originallyChecked;

            relative = candidate;
            return true;
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
                    if (segmentCount > 0 && segments[segmentCount - 1] != "..") segmentCount--;
                    else if (prefixLength != 0) return false;
                    else segments[segmentCount++] = segment;
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
