using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gpu;
using KhaozEngine.Windowing;
using KhaozEngine.Windowing.Actions;

namespace KhaozEngine.BugReports.Client;

/// <summary>What the <c>build</c> section reports: which game, which build of it and which engine.</summary>
/// <param name="GameId">The game id the bundle carries, such as <c>grimhollow</c>.</param>
/// <param name="GameName">The game's display name.</param>
/// <param name="Version">The product version without any <c>+commit</c> suffix.</param>
/// <param name="BuildName">The minor series codename, when the game stamps one.</param>
/// <param name="EngineVersion">The engine version, from <see cref="TelemetrySessionHeader.EngineVersion"/>.</param>
/// <param name="Configuration">The build configuration, such as <c>Release</c>.</param>
/// <param name="Commit">The source commit after the version's <c>+</c>, when there is one.</param>
public sealed record BuildFacts(string GameId, string GameName, string Version, string? BuildName, string EngineVersion,
    string? Configuration, string? Commit);

/// <summary>What the <c>platform</c> section reports: the machine, the runtime, the window and the <c>KE_</c> levers.</summary>
/// <param name="Os">The operating system description.</param>
/// <param name="OsArchitecture">The operating system architecture.</param>
/// <param name="ProcessArchitecture">The architecture this process runs as.</param>
/// <param name="Runtime">The .NET runtime description.</param>
/// <param name="CpuCount">Logical processors available to the process.</param>
/// <param name="TotalMemoryBytes">Physical memory the runtime may use.</param>
/// <param name="Locale">The current culture name.</param>
/// <param name="WindowWidth">Window width in window coordinates.</param>
/// <param name="WindowHeight">Window height in window coordinates.</param>
/// <param name="FramebufferWidth">Framebuffer width in pixels.</param>
/// <param name="FramebufferHeight">Framebuffer height in pixels.</param>
/// <param name="ContentScale">Framebuffer pixels per logical point.</param>
/// <param name="Environment">The <c>KE_</c> variables, already passed through <see cref="ReportRedactor.SelectEnvironment"/>.</param>
public sealed record PlatformFacts(string Os, string OsArchitecture, string ProcessArchitecture, string Runtime, int CpuCount,
    long TotalMemoryBytes, string Locale, int WindowWidth, int WindowHeight, int FramebufferWidth, int FramebufferHeight,
    float ContentScale, IReadOnlyList<TelemetryHeaderValue> Environment);

/// <summary>What the <c>graphics</c> section reports: the backend, the adapter and the live device state.</summary>
/// <param name="Backend">The graphics backend name.</param>
/// <param name="Adapter">The adapter description, empty when the backend reports none.</param>
/// <param name="SwapchainWidth">Swapchain width in pixels.</param>
/// <param name="SwapchainHeight">Swapchain height in pixels.</param>
/// <param name="PresentMode">The present mode name.</param>
/// <param name="SoftwareAdapter">Whether the device is a software rasterizer, null when the backend does not say.</param>
/// <param name="DeviceLossReason">Why the device was lost, null while it is healthy or when the backend does not say.</param>
/// <param name="InjectedModules">Overlay software hooked into the process, null when nothing was scanned.</param>
/// <param name="Capabilities">The device capabilities by property name, see <see cref="ClientFacts.DescribeCapabilities"/>.</param>
public sealed record GraphicsFacts(string Backend, string Adapter, int SwapchainWidth, int SwapchainHeight, string PresentMode,
    bool? SoftwareAdapter, string? DeviceLossReason, IReadOnlyList<string>? InjectedModules,
    IReadOnlyDictionary<string, string> Capabilities);

/// <summary>What the <c>session</c> section reports: how long the game has run and what the last run left behind.</summary>
/// <param name="Uptime">Time since the process started.</param>
/// <param name="SessionLogName">The session log path or name. The section writes only its file name.</param>
/// <param name="PreviousCrashFile">The newest crash file from an earlier run. The section writes only its file name.</param>
/// <param name="PreviousCrashAt">When that crash file was last written.</param>
public sealed record SessionFacts(TimeSpan Uptime, string? SessionLogName, string? PreviousCrashFile, DateTimeOffset? PreviousCrashAt);

/// <summary>What the <c>input</c> section reports: the connected devices and what triggers a report.</summary>
/// <param name="Devices">One entry per device, see <see cref="ClientFacts.Devices"/>.</param>
/// <param name="TriggerBindings">The trigger's bindings, see <see cref="ClientFacts.DescribeBindings"/>.</param>
public sealed record InputFacts(IReadOnlyList<string> Devices, IReadOnlyList<string> TriggerBindings);

/// <summary>
/// Gathers the facts records the built-in sections write. Everything here except <see cref="FindPreviousCrash"/> reads
/// memory, the runtime or the window and does no file, directory or network I/O, so it may run in the trigger frame.
/// </summary>
internal static class ClientFacts
{
    const string Unknown = "unknown";
    const string BuildNameKey = "BuildName";
    const string CrashStem = "-crash";

    static readonly PropertyInfo[] CapabilityProperties = typeof(GpuCapabilities)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(property => property.GetIndexParameters().Length == 0
            && (property.PropertyType == typeof(bool) || property.PropertyType == typeof(int)
                || property.PropertyType == typeof(string)))
        .ToArray();

    /// <summary>
    /// The build facts of <paramref name="entry"/>, or of the process entry assembly when null. The version is the
    /// informational version before any <c>+</c>, falling back to the three part assembly version and then to
    /// <c>unknown</c>. The build name is the <see cref="AssemblyMetadataAttribute"/> keyed <c>BuildName</c>, or else
    /// the first keyed <c>&lt;prefix&gt;.BuildName</c> as the game template stamps it.
    /// </summary>
    internal static BuildFacts Build(Assembly? entry, string gameId, string gameName)
    {
        Assembly? assembly = entry ?? Assembly.GetEntryAssembly();
        string? informational = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        string? version = null;
        string? commit = null;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            int plus = informational.IndexOf('+');
            version = plus < 0 ? informational : informational[..plus];
            commit = plus < 0 || plus == informational.Length - 1 ? null : informational[(plus + 1)..];
        }
        if (string.IsNullOrWhiteSpace(version))
            version = assembly?.GetName().Version?.ToString(3) ?? Unknown;
        string? configuration = assembly?.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration;
        return new BuildFacts(gameId, gameName, version, ReadBuildName(assembly), TelemetrySessionHeader.EngineVersion,
            string.IsNullOrWhiteSpace(configuration) ? null : configuration, commit);
    }

    /// <summary>
    /// The newest file in <paramref name="directory"/> whose name contains <c>-crash</c> and that was last written
    /// before <paramref name="processStart"/>, with its write time in UTC. Both null when there is none, the
    /// directory is missing or it cannot be read. This scans a directory, so the host calls it once at startup and
    /// hands the cached result to its <see cref="SessionFacts"/> delegate: it must never run in the trigger frame.
    /// </summary>
    internal static (string? File, DateTimeOffset? At) FindPreviousCrash(string directory, DateTimeOffset processStart)
    {
        try
        {
            if (!Directory.Exists(directory)) return (null, null);
            string? newest = null;
            DateTime newestAt = DateTime.MinValue;
            DateTime before = processStart.UtcDateTime;
            foreach (string path in Directory.EnumerateFiles(directory))
            {
                if (!Path.GetFileName(path).Contains(CrashStem, StringComparison.Ordinal)) continue;
                DateTime writtenAt = File.GetLastWriteTimeUtc(path);
                if (writtenAt >= before || (newest is not null && writtenAt <= newestAt)) continue;
                newest = path;
                newestAt = writtenAt;
            }
            return newest is null ? (null, null) : (newest, new DateTimeOffset(newestAt, TimeSpan.Zero));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// One <c>Kind:payload</c> string per binding of <paramref name="actionId"/>, such as <c>Key:F10</c>,
    /// <c>GamepadButton:Back</c>, <c>GamepadStickAxis:Left.X</c> or <c>KeyAxis2D:A,D,S,W</c> (left, right, down, up).
    /// Unset sources are skipped and an undeclared action has none.
    /// </summary>
    internal static IReadOnlyList<string> DescribeBindings(ActionMap map, string actionId)
    {
        if (!map.HasAction(actionId)) return [];
        var described = new List<string>();
        foreach (InputBinding binding in map.BindingsOf(actionId))
        {
            InputSource source = binding.Source;
            string? payload = source.Kind switch
            {
                InputSourceKind.Key => source.Key.ToString(),
                InputSourceKind.MouseButton => source.MouseButton.ToString(),
                InputSourceKind.GamepadButton => source.GamepadButton.ToString(),
                InputSourceKind.GamepadStickAxis => source.Stick + "." + source.StickComponent,
                InputSourceKind.GamepadTrigger => source.TriggerSide.ToString(),
                InputSourceKind.KeyAxis1D => source.Key2 + "," + source.Key,
                InputSourceKind.KeyAxis2D => source.Key2 + "," + source.Key + "," + source.Key4 + "," + source.Key3,
                _ => null,
            };
            if (payload is not null) described.Add(source.Kind + ":" + payload);
        }
        return described.AsReadOnly();
    }

    /// <summary>
    /// The platform facts of this process and <paramref name="window"/>. Read on the window thread, since the window
    /// sizes query GLFW. The environment holds only what <paramref name="redactor"/> admits.
    /// </summary>
    internal static PlatformFacts Platform(AppWindow window, ReportRedactor redactor) => new(
        RuntimeInformation.OSDescription,
        RuntimeInformation.OSArchitecture.ToString(),
        RuntimeInformation.ProcessArchitecture.ToString(),
        RuntimeInformation.FrameworkDescription,
        Environment.ProcessorCount,
        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
        CultureInfo.CurrentCulture.Name,
        window.WindowWidth,
        window.WindowHeight,
        window.FramebufferWidth,
        window.FramebufferHeight,
        ContentScale(window.FramebufferWidth, window.LogicalWidth),
        redactor.SelectEnvironment(ReadEnvironment()));

    /// <summary>The live graphics facts of <paramref name="window"/>. Device diagnostics are read through, not cached.</summary>
    internal static GraphicsFacts Graphics(AppWindow window)
    {
        GpuDeviceDiagnostics diagnostics = window.Diagnostics;
        return new GraphicsFacts(window.Backend.ToString(), window.AdapterDescription, window.FramebufferWidth,
            window.FramebufferHeight, window.PresentMode.ToString(), diagnostics.SoftwareAdapter,
            diagnostics.DeviceLossReason, window.InjectedModules, DescribeCapabilities(window.Capabilities));
    }

    /// <summary><c>keyboard</c>, <c>mouse</c>, then <c>gamepad &lt;index&gt;</c> for each connected pad.</summary>
    internal static IReadOnlyList<string> Devices(InputState input)
    {
        var devices = new List<string> { "keyboard", "mouse" };
        foreach (GamepadState pad in input.Gamepads)
        {
            if (pad.IsConnected) devices.Add("gamepad " + pad.Index.ToString(CultureInfo.InvariantCulture));
        }
        return devices.AsReadOnly();
    }

    /// <summary>
    /// Every public <see cref="bool"/>, <see cref="int"/> and <see cref="string"/> instance property of
    /// <paramref name="capabilities"/>, ordinal by name, so a new capability is reported with no upkeep here. Booleans
    /// read <c>true</c> or <c>false</c> and integers are invariant.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> DescribeCapabilities(GpuCapabilities capabilities)
    {
        var described = new SortedDictionary<string, string>(StringComparer.Ordinal);
        object boxed = capabilities;
        foreach (PropertyInfo property in CapabilityProperties)
        {
            object? value = property.GetValue(boxed);
            described[property.Name] = value switch
            {
                bool flag => flag ? "true" : "false",
                int number => number.ToString(CultureInfo.InvariantCulture),
                string text => text,
                _ => "",
            };
        }
        return described;
    }

    /// <summary>Framebuffer pixels per logical point, 1 when the logical width is unknown.</summary>
    internal static float ContentScale(int framebufferWidth, int logicalWidth)
        => logicalWidth > 0 ? (float)framebufferWidth / logicalWidth : 1f;

    static string? ReadBuildName(Assembly? assembly)
    {
        if (assembly is null) return null;
        string? suffixed = null;
        foreach (AssemblyMetadataAttribute metadata in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (string.IsNullOrWhiteSpace(metadata.Value)) continue;
            if (string.Equals(metadata.Key, BuildNameKey, StringComparison.Ordinal)) return metadata.Value;
            if (suffixed is null && metadata.Key.EndsWith("." + BuildNameKey, StringComparison.Ordinal)) suffixed = metadata.Value;
        }
        return suffixed;
    }

    static IEnumerable<KeyValuePair<string, string?>> ReadEnvironment()
    {
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name) yield return new KeyValuePair<string, string?>(name, entry.Value as string);
        }
    }
}
