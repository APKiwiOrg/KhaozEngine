using Microsoft.CodeAnalysis;

namespace KhaozEngine.CodeHealth.Analyzers
{
    /// <summary>
    /// Diagnostic descriptors for the file-size ratchet. Semantics mirror the fleet's
    /// scripts/check-file-size.sh: a baselined file may shrink but never grow, an unlisted file
    /// stays under the cap, and a path on an "exempt" baseline line is not checked at all.
    /// Errors by default: the ratchet is not silenceable by ordinary config. The guidance an agent
    /// needs is folded into messageFormat, because MSBuild prints the message and not the description.
    /// </summary>
    public static class FileSizeDiagnostics
    {
        private const string Category = "KhaozEngine.CodeHealth";

        private const string HelpLink =
            "https://github.com/APKiwiOrg/KhaozEngine/blob/main/docs/design/FILESIZE-ANALYZER-DESIGN-2026-07-20.md";

        // IDE-only surface. Everything load-bearing now lives in the messages, so this carries only
        // what they do not already say verbatim.
        private const string RatchetGuidance =
            "This is a ratchet: existing debt is frozen, it just may not get worse. Blessing " +
            "deliberate growth is a hand-edit of .filesize-baseline, visible in review.";

        public static readonly DiagnosticDescriptor FileGrewPastBaseline = new DiagnosticDescriptor(
            id: "KESIZE001",
            title: "Source file grew past its .filesize-baseline entry",
            messageFormat: "'{0}' is {1} lines, baseline is {2} (this file may shrink, not grow). " +
                           "Put the new code in its own type. Do NOT split this file at an arbitrary " +
                           "line to satisfy the check: two god halves are worse than one. If the " +
                           "growth is legitimate, stop and ask the user to bless it in .filesize-baseline.",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: RatchetGuidance,
            helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor FileOverCap = new DiagnosticDescriptor(
            id: "KESIZE002",
            title: "Source file is over the size cap and not in .filesize-baseline",
            messageFormat: "'{0}' is {1} lines, over the {2}-line cap and not in .filesize-baseline. " +
                           "Split it along a responsibility boundary, not at an arbitrary line. If " +
                           "this file is legitimately large because its size is content rather than " +
                           "structure, stop and ask the user to exempt it in .filesize-baseline.",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: RatchetGuidance,
            helpLinkUri: HelpLink);
    }
}
