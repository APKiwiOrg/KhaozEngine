using Microsoft.CodeAnalysis;

namespace KhaozEngine.CodeHealth.Analyzers
{
    /// <summary>
    /// Test-health ratchet descriptors. Remediation is part of each message so build logs carry it.
    /// </summary>
    public static class TestHealthDiagnostics
    {
        private const string Category = "KhaozEngine.CodeHealth";
        private const string HelpLink =
            "https://github.com/APKiwiOrg/KhaozEngine/blob/main/KhaozEngine.CodeHealth.Analyzers/README.md#test-health-ratchet";

        // The quoted path and count prefix is consumed by the baseline update tooling.
        public static readonly DiagnosticDescriptor ExceptionMessageAssertion = new DiagnosticDescriptor(
            id: "KETEST001",
            title: "Test asserts exception message wording",
            messageFormat: "'{0}' has {1} exception message assertion(s), .testhealth-baseline allows {2}. " +
                           "Assert the exception type and a structured member (parameter name, error code, status) " +
                           "instead of message wording. Absence checks such as DoesNotContain are allowed. " +
                           "If the wording itself is the contract, stop and ask the user to exempt the file in .testhealth-baseline.",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor NonPublicReflection = new DiagnosticDescriptor(
            id: "KETEST002",
            title: "Test accesses non-public members through reflection",
            messageFormat: "'{0}' has {1} non-public reflection access(es), .testhealth-baseline allows {2}. " +
                           "Test through the public API, or an internal seam with InternalsVisibleTo. " +
                           "A structural guard that must reflect over non-public members needs the user to exempt the file in .testhealth-baseline.",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor LoopOnlyAssertion = new DiagnosticDescriptor(
            id: "KETEST003",
            title: "Test assertions only run inside a loop that may be empty",
            messageFormat: "'{0}' has {1} test(s) whose assertions all run inside a loop that may be empty, " +
                           ".testhealth-baseline allows {2}. " +
                           "Assert the expected count or non-emptiness before the loop so an empty result fails.",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            helpLinkUri: HelpLink);
    }
}
