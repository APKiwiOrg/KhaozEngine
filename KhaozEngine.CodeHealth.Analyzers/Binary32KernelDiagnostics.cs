using Microsoft.CodeAnalysis;

namespace KhaozEngine.CodeHealth.Analyzers
{
    /// <summary>Compile-time restrictions on device-reproduced binary32 kernels.</summary>
    public static class Binary32KernelDiagnostics
    {
        private const string Category = "KhaozEngine.CodeHealth";
        private const string HelpLink =
            "https://github.com/APKiwiOrg/KhaozEngine/blob/main/docs/design/EQUIPMENT-COVERAGE-KERNEL-CONTRACT-2026-10-18.md";

        public static readonly DiagnosticDescriptor FloatingOperator = new DiagnosticDescriptor(
            id: "KEB32001",
            title: "Kernel contains a raw floating operator",
            messageFormat: "KEB32001: floating operator '{0}' is outside the binary32 inventory. " +
                           "Move it into the hand-reviewed Binary32Arithmetic home and call that member.",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor FloatingConversion = new DiagnosticDescriptor(
            id: "KEB32002",
            title: "Kernel contains a nonconstant floating conversion",
            messageFormat: "KEB32002: numeric conversion from '{0}' to '{1}' is outside the binary32 inventory. " +
                           "Move the conversion into the hand-reviewed Binary32Arithmetic home.",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor PlatformArithmetic = new DiagnosticDescriptor(
            id: "KEB32003",
            title: "Kernel calls a platform arithmetic API",
            messageFormat: "KEB32003: platform arithmetic API '{0}' is outside the binary32 inventory. " +
                           "Replace it with a call to the hand-reviewed Binary32Arithmetic home.",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor UnmarkedCall = new DiagnosticDescriptor(
            id: "KEB32004",
            title: "Kernel call graph is not statically closed",
            messageFormat: "KEB32004: call or computed-property access to '{0}' leaves the binary32 kernel call graph. " +
                           "Use a statically bound non-virtual Binary32Kernel callee, or move its arithmetic into " +
                           "the hand-reviewed Binary32Arithmetic home.",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor UnlistedOperation = new DiagnosticDescriptor(
            id: "KEB32005",
            title: "Operation not permitted in a binary32 kernel",
            messageFormat: "KEB32005: operation not permitted in a binary32 kernel: '{0}'. " +
                           "Use an explicitly permitted operation or move the work into " +
                           "the hand-reviewed Binary32Arithmetic home.",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            helpLinkUri: HelpLink);
    }
}
