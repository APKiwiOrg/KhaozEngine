using Microsoft.CodeAnalysis;

namespace KhaozEngine.CodeHealth.Analyzers
{
    /// <summary>
    /// Diagnostic descriptors for the file-size ratchet. Semantics mirror the fleet's
    /// scripts/check-file-size.sh: a baselined file may shrink but never grow, an unlisted file
    /// stays under the cap. Errors by default: the ratchet is not silenceable by ordinary config.
    /// </summary>
    public static class FileSizeDiagnostics
    {
        private const string Category = "KhaozEngine.CodeHealth";

        private const string RatchetGuidance =
            "This is a ratchet: existing debt is frozen, it just may not get worse. Put the new code " +
            "in its own type rather than growing the file. Do not split at an arbitrary line to " +
            "satisfy the check: two god halves are worse than one. Blessing deliberate growth is a " +
            "hand-edit of .filesize-baseline, visible in review.";

        public static readonly DiagnosticDescriptor FileGrewPastBaseline = new DiagnosticDescriptor(
            id: "KESIZE001",
            title: "Source file grew past its .filesize-baseline entry",
            messageFormat: "'{0}' is {1} lines, baseline is {2} (this file may shrink, not grow)",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: RatchetGuidance);

        public static readonly DiagnosticDescriptor FileOverCap = new DiagnosticDescriptor(
            id: "KESIZE002",
            title: "Source file is over the size cap and not in .filesize-baseline",
            messageFormat: "'{0}' is {1} lines, over the {2}-line cap and not in .filesize-baseline",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: RatchetGuidance);
    }
}
