using Microsoft.CodeAnalysis;

namespace KhaozEngine.Localization.Analyzers;

internal static class StringIdGeneratorDiagnostics
{
    public static readonly DiagnosticDescriptor InvalidConfiguration = new(
        id: "KELOC004",
        title: "StringId generator configuration is invalid",
        messageFormat: "Cannot generate StringId keys from '{0}': {1}",
        category: LocalizationDiagnostics.Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "An opted resx file must name a valid target type and use internal or public accessibility.");

    public static readonly DiagnosticDescriptor InvalidResx = new(
        id: "KELOC005",
        title: "StringId resource file is invalid",
        messageFormat: "Cannot generate StringId keys from '{0}': {1}",
        category: LocalizationDiagnostics.Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "An opted resx file must be well-formed and its string keys must produce usable StringId members.");

    public static readonly DiagnosticDescriptor MemberCollision = new(
        id: "KELOC006",
        title: "Resource keys produce the same StringId member",
        messageFormat: "Resource keys '{0}' and '{1}' both produce member '{2}' in '{3}'",
        category: LocalizationDiagnostics.Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Every resource key must map to a unique generated C# member name.");

    public static readonly DiagnosticDescriptor TargetCollision = new(
        id: "KELOC007",
        title: "StringId target type has a collision",
        messageFormat: "Cannot generate target '{0}' from '{1}' because {2}",
        category: LocalizationDiagnostics.Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "An opted resx file must target a type not already declared by source or another opted resource.");
}
