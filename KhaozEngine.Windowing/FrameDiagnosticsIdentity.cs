using System;
using System.Collections;
using System.Linq;
using System.Reflection;

namespace KhaozEngine.Windowing;

internal sealed record FrameDiagnosticsAssemblyIdentity(string Name, string? Version, string? InformationalVersion,
    Guid Mvid, string Location);
internal sealed record FrameDiagnosticsEnvironmentVariable(string Name, string? Value);
internal sealed record FrameDiagnosticsIdentity(int ProcessId, string? ProcessPath, string[] CommandLineArguments,
    FrameDiagnosticsAssemblyIdentity[] Assemblies, FrameDiagnosticsEnvironmentVariable[] Environment)
{
    // Called by the writer after sampling stops. No reflection or environment enumeration is on the measured path.
    internal static FrameDiagnosticsIdentity Collect() => new(System.Environment.ProcessId, System.Environment.ProcessPath,
        System.Environment.GetCommandLineArgs(), AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name is { } n && (n.StartsWith("KhaozEngine", StringComparison.Ordinal) || n.StartsWith("Grimhollow", StringComparison.Ordinal)))
            .Select(a => new FrameDiagnosticsAssemblyIdentity(a.GetName().Name!, a.GetName().Version?.ToString(),
                a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                a.ManifestModule.ModuleVersionId, a.IsDynamic ? "" : a.Location))
            .OrderBy(a => a.Name, StringComparer.Ordinal).ThenBy(a => a.Location, StringComparer.Ordinal).ToArray(),
        System.Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .Where(e => e.Key is string key && key.StartsWith("KE_", StringComparison.Ordinal))
            .Select(e => new FrameDiagnosticsEnvironmentVariable((string)e.Key, e.Value?.ToString()))
            .OrderBy(e => e.Name, StringComparer.Ordinal).ToArray());
}

internal readonly record struct FrameDiagnosticsCaptureState(int TapCount, bool SwapchainCopySourceRequested);
