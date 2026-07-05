using System;

namespace KhaozEngine.App;

/// <summary>
/// Marks an assembly, type, or member as exempt from the localization analyzer's raw-text warning
/// (KELOC002): <see cref="LocalizedText.Raw"/> used anywhere inside the marked scope is intentional and the
/// analyzer stays silent. Use on debug overlays, tools, and sample chrome that legitimately are not localized.
/// </summary>
[AttributeUsage(
    AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Struct |
    AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Property |
    AttributeTargets.Field,
    Inherited = false, AllowMultiple = false)]
public sealed class LocalizationExemptAttribute : Attribute
{
}
