using Microsoft.CodeAnalysis;

namespace KhaozEngine.CodeHealth.Analyzers
{
    /// <summary>
    /// The floating point descriptor. Remediation is part of the message so build logs carry it.
    /// </summary>
    public static class SubnormalFloatDiagnostics
    {
        private const string Category = "KhaozEngine.CodeHealth";
        private const string HelpLink =
            "https://github.com/APKiwiOrg/KhaozEngine/blob/main/KhaozEngine.CodeHealth.Analyzers/README.md#subnormal-float-constants";

        /// <summary>
        /// The MSBuild property that turns the rule on: <c>on</c> checks every compilation that does not reference
        /// xunit, <c>all</c> checks test compilations too. Any other value, or none, leaves the rule silent.
        /// </summary>
        public const string ScopeProperty = "KhaozSubnormalFloatRule";

        // Reported from the compilation end action as well, for a static readonly field read as a float.
        public static readonly DiagnosticDescriptor SubnormalFloatConstant = new DiagnosticDescriptor(
            id: "KEFP001",
            title: "JIT-visible subnormal float constant",
            messageFormat: "{0}. The JIT folds a subnormal float constant through the flush mode of the thread that " +
                           "compiles the method, and that machine code then serves every thread. Step with FloatUlp.Down " +
                           "or FloatUlp.Up (an engine project sets KhaozFloatUlp to true to compile the helper in), and " +
                           "read any other subnormal value from a mutable static the JIT cannot fold.",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            helpLinkUri: HelpLink,
            customTags: WellKnownDiagnosticTags.CompilationEnd);
    }
}
