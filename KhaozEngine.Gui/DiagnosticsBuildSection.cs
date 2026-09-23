using System;
using System.Reflection;

namespace KhaozEngine.Gui;

/// <summary>The running app's name and build version as the diagnostics overlay shows them.</summary>
internal readonly record struct BuildIdentity(string Name, string Version);

/// <summary>
/// The <see cref="DiagnosticsHud"/>'s built-in Build section: one row whose label is the app's name and whose value
/// is its build version, under the localized <see cref="DiagnosticsOverlayStrings.BuildTitle"/>. It answers which
/// binary the person reading the overlay is running, which is the first question a bug report asks.
/// <para>
/// The identity is read from its source ONCE, on the first refresh that needs it, and kept for the life of the HUD,
/// because it cannot change while the process runs. A game's own display strings replace it through
/// <see cref="Override"/>, and then the source is never read at all. The title resolves on each refresh so a locale
/// switch shows on the next one, and the section object is rebuilt only when that title text changes, so a steady
/// panel allocates nothing here.
/// </para>
/// </summary>
internal sealed class DiagnosticsBuildSection
{
    // Shown only when an assembly yields neither the attribute nor a fallback, which a real game head never does.
    const string Missing = "?";

    readonly Func<BuildIdentity> _source;
    BuildIdentity? _identity;
    OverlaySection? _section;

    internal DiagnosticsBuildSection(Func<BuildIdentity> source) =>
        _source = source ?? throw new ArgumentNullException(nameof(source));

    /// <summary>Replace the identity with the game's own strings. Wins over the source whether or not it was read.</summary>
    internal void Override(string name, string version)
    {
        _identity = new BuildIdentity(name, version);
        _section = null;
    }

    /// <summary>The section for this refresh. Reads the source on the first call only.</summary>
    internal OverlaySection Section()
    {
        BuildIdentity identity = _identity ??= _source();
        string title = DiagnosticsOverlayStrings.Resolve(DiagnosticsOverlayStrings.BuildTitle);
        if (_section is { } cached && string.Equals(cached.Title, title, StringComparison.Ordinal)) return cached;
        return _section = new OverlaySection(title, new[] { new OverlayRow(identity.Name, identity.Version) });
    }

    /// <summary>The default source: the process entry assembly, the game head's own executable.</summary>
    internal static BuildIdentity FromEntryAssembly() => FromAssembly(Assembly.GetEntryAssembly());

    /// <summary>
    /// The product name and the informational version of <paramref name="assembly"/>, falling back to its simple
    /// name and its three-part assembly version. The informational version loses any <c>+</c> build metadata
    /// suffix (the commit SourceLink appends) through <see cref="StripBuildMetadata"/>.
    /// </summary>
    internal static BuildIdentity FromAssembly(Assembly? assembly)
    {
        if (assembly is null) return new BuildIdentity(Missing, Missing);

        AssemblyName assemblyName = assembly.GetName();
        string? product = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product;
        string name = !string.IsNullOrWhiteSpace(product) ? product : assemblyName.Name ?? Missing;

        string version = StripBuildMetadata(
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
        if (version.Length == 0) version = assemblyName.Version?.ToString(3) ?? Missing;

        return new BuildIdentity(name, version);
    }

    /// <summary>
    /// Drop a <c>+</c> build metadata suffix, so <c>1.4.0+3f2a9c1</c> reads <c>1.4.0</c>. The same convention as
    /// the showcase's player-visible engine version footer. Telemetry keeps the full stamped value instead.
    /// </summary>
    internal static string StripBuildMetadata(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational)) return string.Empty;
        int plus = informational.IndexOf('+');
        return plus >= 0 ? informational.Substring(0, plus) : informational;
    }
}
