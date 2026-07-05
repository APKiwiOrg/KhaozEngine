using Microsoft.CodeAnalysis;

namespace KhaozEngine.Localization.Analyzers;

internal static class LocalizationDiagnostics
{
    public const string Category = "Localization";

    public static readonly DiagnosticDescriptor RawStringSink = new(
        id: "KELOC001",
        title: "Player-facing text passed as a raw string",
        messageFormat: "'{0}' takes player-facing text; pass a StringId or LocalizedText.Raw(...) instead of a raw string",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A raw string at a player-facing sink bypasses localization. Use a StringId (localizable) or LocalizedText.Raw(...) for non-localizable text.");

    public static readonly DiagnosticDescriptor RawOutsideExempt = new(
        id: "KELOC002",
        title: "LocalizedText.Raw outside exempt or debug code",
        messageFormat: "LocalizedText.Raw bypasses localization; confirm the text is intentionally non-localizable or mark the scope [LocalizationExempt]",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "LocalizedText.Raw is the non-localizable escape hatch. Outside [LocalizationExempt] scopes or DEBUG-conditional code its every use should be a deliberate, reviewed decision.");
}
