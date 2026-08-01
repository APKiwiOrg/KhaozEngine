using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace KhaozEngine.Diagnostics;

/// <summary>
/// Builds the self-identifying FIRST line of a telemetry recording: which engine, which build, which backend,
/// which adapter, what was hooked into the process, which <c>KE_</c> levers were set, and whatever durable
/// values the game handed in. Without it a captured JSONL file is a column of numbers with no provenance, and
/// an analyst has to take the reporter's word for what produced it.
/// <para>
/// The header is a <c>session</c> envelope carrying a <c>v</c> schema integer, and it deliberately has NO
/// <c>t</c> field, so telling it apart from a per-frame row is a single key test in either direction:
/// <c>"session" in row</c> or <c>"t" in row</c>. New fields may be appended within the envelope without moving
/// <c>v</c>. <c>v</c> moves only when an existing field changes meaning or goes away.
/// </para>
/// <para>
/// Pure apart from <see cref="ReadProcessEnvironment"/> and the one-time assembly-version read, so the whole
/// header shape is headless-testable through the <see cref="Build(TelemetrySessionInfo, IReadOnlyList{TelemetryHeaderValue})"/>
/// overload without touching the real environment.
/// </para>
/// </summary>
public static class TelemetrySessionHeader
{
    /// <summary>
    /// The schema version written as <c>session.v</c>. A reader should accept anything it knows and refuse
    /// what it does not, rather than parsing a header it has never seen.
    /// </summary>
    public const int SchemaVersion = 1;

    /// <summary>
    /// The prefix that marks an environment variable as an engine lever. ONLY variables starting with this
    /// are recorded, so a capture carries the levers that shaped the run and nothing else from the machine.
    /// </summary>
    public const string EnvironmentPrefix = "KE_";

    /// <summary>
    /// The engine version written into every header: the engine assembly's informational version (which
    /// carries the SourceLink commit suffix, so a capture names the exact build), falling back to the
    /// three-part assembly version and then to <c>"unknown"</c>.
    /// </summary>
    public static string EngineVersion { get; } = ResolveEngineVersion();

    /// <summary>
    /// Build the header line for <paramref name="session"/>, reading the live process environment for the
    /// <c>KE_</c> levers. Pass null for <paramref name="session"/> to get the engine-only header a bare
    /// <see cref="TelemetryRecorder.Start(string)"/> writes.
    /// </summary>
    public static string Build(TelemetrySessionInfo? session) => Build(session, ReadProcessEnvironment());

    /// <summary>
    /// Build the header line from <paramref name="session"/> plus an explicit set of environment levers. The
    /// overload that takes the environment is what makes the header testable on any machine.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="environment"/> is null.</exception>
    public static string Build(TelemetrySessionInfo? session, IReadOnlyList<TelemetryHeaderValue> environment)
    {
        if (environment is null) throw new ArgumentNullException(nameof(environment));

        var sb = new StringBuilder(512);
        sb.Append("{\"session\":{\"v\":").Append(SchemaVersion.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"engine\":");
        TelemetryJson.AppendString(sb, EngineVersion);
        AppendApp(sb, session);
        AppendGpu(sb, session);
        sb.Append(",\"env\":");
        AppendPairs(sb, environment);
        sb.Append(",\"game\":");
        AppendPairs(sb, session?.GameValues);
        sb.Append("}}");
        return sb.ToString();
    }

    /// <summary>
    /// The <c>KE_</c>-prefixed variables set in this process, name and value, sorted by name. The impure half
    /// of the pair: it reads the real environment, then hands it to <see cref="SelectEngineVariables"/>.
    /// </summary>
    public static IReadOnlyList<TelemetryHeaderValue> ReadProcessEnvironment()
    {
        var all = new List<KeyValuePair<string, string?>>();
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            all.Add(new KeyValuePair<string, string?>(entry.Key?.ToString() ?? string.Empty, entry.Value?.ToString()));
        return SelectEngineVariables(all);
    }

    /// <summary>
    /// The <c>KE_</c>-prefixed entries of <paramref name="variables"/>, values included, sorted by name
    /// (ordinal) so two captures of the same run compare cleanly. Pure, so a game can screen any set on any
    /// OS. The prefix match is case-insensitive, which catches a lever typed in the wrong case on a host
    /// where that still resolves, and nothing without the prefix is ever read or recorded.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="variables"/> is null.</exception>
    public static IReadOnlyList<TelemetryHeaderValue> SelectEngineVariables(
        IEnumerable<KeyValuePair<string, string?>> variables)
    {
        if (variables is null) throw new ArgumentNullException(nameof(variables));

        var levers = new List<TelemetryHeaderValue>();
        foreach (KeyValuePair<string, string?> variable in variables)
        {
            string name = variable.Key;
            if (string.IsNullOrEmpty(name)) continue;
            if (!name.StartsWith(EnvironmentPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            levers.Add(new TelemetryHeaderValue(name, variable.Value ?? string.Empty));
        }

        levers.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        return levers;
    }

    static void AppendApp(StringBuilder sb, TelemetrySessionInfo? session)
    {
        sb.Append(",\"app\":{\"name\":");
        TelemetryJson.AppendString(sb, Trimmed(session?.AppName));
        sb.Append(",\"version\":");
        TelemetryJson.AppendString(sb, Trimmed(session?.AppVersion));
        sb.Append(",\"build\":");
        TelemetryJson.AppendString(sb, Trimmed(session?.BuildName));
        sb.Append('}');
    }

    static void AppendGpu(StringBuilder sb, TelemetrySessionInfo? session)
    {
        sb.Append(",\"gpu\":{\"backend\":");
        TelemetryJson.AppendString(sb, Trimmed(session?.GpuBackend));
        sb.Append(",\"backendSource\":");
        TelemetryJson.AppendString(sb, Trimmed(session?.GpuBackendSource));
        sb.Append(",\"adapter\":");
        TelemetryJson.AppendString(sb, Trimmed(session?.AdapterDescription));

        // null (never scanned) and [] (scanned, clean) are opposite facts, so the header keeps them apart.
        sb.Append(",\"injectedModules\":");
        IReadOnlyList<string>? modules = session?.InjectedModules;
        if (modules is null)
        {
            sb.Append("null");
        }
        else
        {
            sb.Append('[');
            for (int i = 0; i < modules.Count; i++)
            {
                if (i > 0) sb.Append(',');
                TelemetryJson.AppendString(sb, modules[i] ?? string.Empty);
            }
            sb.Append(']');
        }

        // Direct3D11 only: null on every other backend, off Windows, and when the query failed.
        sb.Append(",\"threading\":");
        if (session is null || !session.HasThreadingCaps)
        {
            sb.Append("null");
        }
        else
        {
            sb.Append("{\"driverCommandLists\":");
            TelemetryJson.AppendBool(sb, session.DriverCommandLists);
            sb.Append(",\"driverConcurrentCreates\":");
            TelemetryJson.AppendBool(sb, session.DriverConcurrentCreates);
            sb.Append('}');
        }

        sb.Append('}');
    }

    static void AppendPairs(StringBuilder sb, IReadOnlyList<TelemetryHeaderValue>? pairs)
    {
        sb.Append('{');
        if (pairs != null)
        {
            for (int i = 0; i < pairs.Count; i++)
            {
                if (i > 0) sb.Append(',');
                TelemetryJson.AppendKey(sb, pairs[i].Key);
                TelemetryJson.AppendString(sb, pairs[i].Value ?? string.Empty);
            }
        }
        sb.Append('}');
    }

    // Blank and unset are the same fact to a reader, so both become JSON null rather than an empty string.
    static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    static string ResolveEngineVersion()
    {
        Assembly engine = typeof(TelemetrySessionHeader).Assembly;
        string? informational = engine.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational)) return informational;
        return engine.GetName().Version?.ToString(3) ?? "unknown";
    }
}
