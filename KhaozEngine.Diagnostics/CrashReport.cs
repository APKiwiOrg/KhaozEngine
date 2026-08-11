using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Diagnostics;

/// <summary>
/// THE LAST-CHANCE CRASH FILE: an unhandled exception written to its own file, in the place a tester already
/// looks for crashes, so a one-off is never lost with the terminal it printed to.
///
/// <para><b>WHY IT IS NOT THE SAME THING AS THE SESSION LOG.</b> <see cref="CrashHandler"/> routes a crash into
/// a configured <see cref="LogManager"/>, which is the richer record and the one to read when it exists. It can
/// only do that when the game configured logging, and it writes where the game's log sinks write. This writes
/// with no configuration at all, into an OS location, and is armed by <c>GameApp</c> for every game head whether
/// or not that head logs anything. The engine's own showcase is the worked example of the gap: it configures no
/// logging, so a managed exception escaping at the end of shader warming reached the terminal and nothing else,
/// and the operating system's crash report named only coreclr's dispatch frames
/// (https://github.com/APKiwiOrg/KhaozEngine/issues/607).</para>
///
/// <para><b>BESIDE THE OS CRASH REPORT, DELIBERATELY.</b> The default directory is the per-user log location for
/// the platform: <c>~/Library/Logs/KhaozEngine</c> on macOS, which is the same tree the <c>.ips</c> report lands
/// in under <c>DiagnosticReports</c>, <c>%LOCALAPPDATA%\KhaozEngine\crash</c> on Windows, and
/// <c>$XDG_STATE_HOME/KhaozEngine/crash</c> (else <c>~/.local/state/...</c>) elsewhere. Whoever collects the
/// system report finds the managed half next to it, which is the whole point: the native report carries the
/// signal and the thread, and this carries the exception type, message and stack.</para>
///
/// <para><b>IT RUNS ON THE RUNTIME'S CRASH PATH, so it never throws</b>, does its work in one pass, and takes no
/// lock it holds across I/O. A failure to write is swallowed and reported as a null path rather than as a second
/// exception on top of the first.</para>
///
/// <para><b>THE AMBIENT NOTES ARE FOR THE CONTEXT THIS PACKAGE CANNOT REACH.</b> The engine version and the OS
/// are read here. The graphics backend, the scene, the build channel, whatever else is worth having in hand at
/// the moment of a crash, is pushed in with <see cref="Note"/> by whoever knows it: <c>GameApp</c> notes the
/// backend as soon as its window exists, which is exactly the fact a boot-time GPU crash is about.</para>
/// </summary>
public static class CrashReport
{
    /// <summary>Default number of crash files kept per process label.</summary>
    public const int DefaultMaxRetainedReports = 20;

    static readonly object gate = new();
    static readonly List<KeyValuePair<string, string>> notes = new();
    static CrashReportOptions? armed;
    static UnhandledExceptionEventHandler? domainHandler;
    static EventHandler<UnobservedTaskExceptionEventArgs>? taskHandler;
    static string? cachedDefaultDirectory;

    /// <summary>
    /// The OS location crash files go to when <see cref="CrashReportOptions.Directory"/> is not set. Resolved
    /// once per process. Never throws: an environment that answers nothing lands the reports under the temp
    /// directory rather than failing the arming call.
    /// </summary>
    public static string DefaultDirectory
    {
        get
        {
            string? resolved = Volatile.Read(ref cachedDefaultDirectory);
            if (resolved is not null) return resolved;

            resolved = ResolveDefaultDirectory(
                isMacOS: OperatingSystem.IsMacOS(),
                isWindows: OperatingSystem.IsWindows(),
                home: SafeEnvironment("HOME") ?? SafeFolder(Environment.SpecialFolder.UserProfile),
                xdgStateHome: SafeEnvironment("XDG_STATE_HOME"),
                localAppData: SafeFolder(Environment.SpecialFolder.LocalApplicationData),
                tempDirectory: SafeTemp());
            Volatile.Write(ref cachedDefaultDirectory, resolved);
            return resolved;
        }
    }

    /// <summary>
    /// Arms the crash file: an <see cref="AppDomain.UnhandledException"/> (and, per
    /// <see cref="CrashReportOptions.IncludeUnobservedTaskExceptions"/>, a
    /// <see cref="TaskScheduler.UnobservedTaskException"/>) writes one report into
    /// <see cref="CrashReportOptions.Directory"/>. Idempotent: a second call replaces the first arming rather
    /// than adding a second handler.
    /// </summary>
    /// <param name="options">Where the file goes and what names it. Null is ignored.</param>
    public static void Install(CrashReportOptions options)
    {
        if (options is null) return;
        lock (gate)
        {
            UninstallCore();
            armed = options;
            domainHandler = (_, e) => OnCrash(
                e.IsTerminating ? "Unhandled exception (terminating)" : "Unhandled exception",
                e.ExceptionObject as Exception,
                e.ExceptionObject);
            AppDomain.CurrentDomain.UnhandledException += domainHandler;

            if (options.IncludeUnobservedTaskExceptions)
            {
                // Deliberately NOT marked observed: whether an unobserved task exception is fatal stays the
                // game's decision (CrashHandler is the type that answers it), and this hook only records.
                taskHandler = (_, e) => OnCrash("Unobserved task exception", e.Exception, e.Exception);
                TaskScheduler.UnobservedTaskException += taskHandler;
            }
        }
    }

    /// <summary>Arms the crash file for <paramref name="processLabel"/> with every other knob defaulted.</summary>
    /// <param name="processLabel">Name of the crashing process, used in the file name and the header.</param>
    public static void Install(string processLabel)
        => Install(new CrashReportOptions { ProcessLabel = processLabel });

    /// <summary>Removes the handlers this type installed. Leaves any notes in place.</summary>
    public static void Uninstall()
    {
        lock (gate) { UninstallCore(); }
    }

    /// <summary>
    /// Records one fact to include in any report written from here on: the graphics backend, the current
    /// scene, a build channel. A repeated <paramref name="key"/> replaces its previous value, a null or
    /// whitespace <paramref name="value"/> removes it, and both are flattened onto one line so a note can
    /// never break the file's shape. Cheap and never throws, so it is safe to call from a frame path.
    /// </summary>
    /// <param name="key">The fact's name, as it appears in the report.</param>
    /// <param name="value">The fact's value, or null to drop the note.</param>
    public static void Note(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        string flatKey = Flatten(key);
        lock (gate)
        {
            for (int i = 0; i < notes.Count; i++)
            {
                if (!string.Equals(notes[i].Key, flatKey, StringComparison.Ordinal)) continue;
                if (string.IsNullOrWhiteSpace(value)) notes.RemoveAt(i);
                else notes[i] = new KeyValuePair<string, string>(flatKey, Flatten(value));
                return;
            }

            if (!string.IsNullOrWhiteSpace(value))
                notes.Add(new KeyValuePair<string, string>(flatKey, Flatten(value)));
        }
    }

    /// <summary>Drops every note recorded by <see cref="Note"/>.</summary>
    public static void ClearNotes()
    {
        lock (gate) { notes.Clear(); }
    }

    /// <summary>
    /// Renders one crash report: a header of <c>key: value</c> lines (timestamp, process, engine version,
    /// runtime, OS, context, exception type and message, plus every <see cref="Note"/>), then the exception's
    /// full text under a <c>--- stack ---</c> marker. Pure, so the shape is testable without writing a file.
    /// </summary>
    /// <param name="processLabel">Name of the crashing process.</param>
    /// <param name="context">What kind of crash this is, e.g. <c>Unhandled exception (terminating)</c>.</param>
    /// <param name="exception">The exception, when the crash carried one.</param>
    /// <param name="raw">The raw thrown object, used when <paramref name="exception"/> is null.</param>
    /// <param name="timestamp">When the crash happened.</param>
    /// <returns>The rendered report.</returns>
    public static string Format(string processLabel, string context, Exception? exception, object? raw,
        DateTimeOffset timestamp)
    {
        var text = new StringBuilder(1024);
        text.Append("KhaozEngine crash report").Append('\n');
        Line(text, "timestamp", timestamp.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        Line(text, "process", string.IsNullOrWhiteSpace(processLabel) ? "game" : processLabel);
        Line(text, "engine", EngineVersion());
        Line(text, "runtime", RuntimeInformation.FrameworkDescription);
        Line(text, "os", $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        Line(text, "context", context);

        foreach (KeyValuePair<string, string> note in NotesSnapshot())
            Line(text, note.Key, note.Value);

        Line(text, "exception", exception?.GetType().FullName ?? raw?.GetType().FullName ?? "unknown");
        Line(text, "message", exception?.Message ?? raw?.ToString() ?? "none");

        text.Append("--- stack ---").Append('\n');
        text.Append(exception?.ToString() ?? raw?.ToString() ?? "No exception object was available.")
            .Append('\n');
        return text.ToString();
    }

    /// <summary>
    /// Writes one report per <paramref name="options"/> and returns its full path, or null when nothing could
    /// be written. Never throws.
    /// </summary>
    /// <param name="options">Where the file goes and what names it. Null returns null.</param>
    /// <param name="context">What kind of crash this is.</param>
    /// <param name="exception">The exception, when the crash carried one.</param>
    /// <param name="raw">The raw thrown object, used when <paramref name="exception"/> is null.</param>
    /// <returns>The crash file's full path, or null.</returns>
    public static string? Write(CrashReportOptions options, string context, Exception? exception, object? raw)
    {
        if (options is null) return null;
        try
        {
            string directory = string.IsNullOrWhiteSpace(options.Directory)
                ? DefaultDirectory
                : options.Directory!;
            Directory.CreateDirectory(directory);

            string prefix = FileNamePrefix(options.ProcessLabel);
            LogFilePruner.KeepNewest(directory, options.MaxRetainedReports, prefix);

            DateTimeOffset now = DateTimeOffset.UtcNow;
            string path = Path.Combine(directory,
                $"{prefix}-{now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)}.log");
            File.WriteAllText(path, Format(options.ProcessLabel, context, exception, raw, now));
            return path;
        }
        catch (Exception)
        {
            // Crash path: a report that cannot be written is not worth a second exception on top of the first.
            return null;
        }
    }

    /// <summary>
    /// What the installed handlers call. Internal so a test can drive the whole armed path without an actual
    /// process-killing exception, which is the same seam <see cref="CrashHandler.Report"/> uses.
    /// </summary>
    internal static string? OnCrash(string context, Exception? exception, object? raw)
    {
        CrashReportOptions? options;
        lock (gate) { options = armed; }
        return options is null ? null : Write(options, context, exception, raw);
    }

    /// <summary>
    /// The default-directory decision, with the environment passed in so every branch is testable on any host.
    /// macOS lands in the user's own <c>Library/Logs</c>, the tree the system's crash reports live in, and the
    /// other platforms take their conventional per-user state location.
    /// </summary>
    internal static string ResolveDefaultDirectory(bool isMacOS, bool isWindows, string? home,
        string? xdgStateHome, string? localAppData, string tempDirectory)
    {
        if (isMacOS && !string.IsNullOrWhiteSpace(home))
            return Path.Combine(home!, "Library", "Logs", "KhaozEngine");

        if (isWindows && !string.IsNullOrWhiteSpace(localAppData))
            return Path.Combine(localAppData!, "KhaozEngine", "crash");

        if (!isWindows && !isMacOS)
        {
            if (!string.IsNullOrWhiteSpace(xdgStateHome))
                return Path.Combine(xdgStateHome!, "KhaozEngine", "crash");
            if (!string.IsNullOrWhiteSpace(home))
                return Path.Combine(home!, ".local", "state", "KhaozEngine", "crash");
        }

        if (!string.IsNullOrWhiteSpace(localAppData))
            return Path.Combine(localAppData!, "KhaozEngine", "crash");

        return Path.Combine(tempDirectory, "KhaozEngine", "crash");
    }

    /// <summary>The file-name stem for one process label, which is also the prune pattern's prefix.</summary>
    internal static string FileNamePrefix(string? processLabel)
    {
        string name = string.IsNullOrWhiteSpace(processLabel) ? "game" : processLabel!;
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Replace(' ', '-') + "-crash";
    }

    static void UninstallCore()
    {
        if (domainHandler is not null) AppDomain.CurrentDomain.UnhandledException -= domainHandler;
        if (taskHandler is not null) TaskScheduler.UnobservedTaskException -= taskHandler;
        domainHandler = null;
        taskHandler = null;
        armed = null;
    }

    static KeyValuePair<string, string>[] NotesSnapshot()
    {
        lock (gate) { return notes.ToArray(); }
    }

    static void Line(StringBuilder text, string key, string value)
        => text.Append(key).Append(": ").Append(Flatten(value)).Append('\n');

    // One note or one message can never break the file into two records, so every value is one line.
    static string Flatten(string? value)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : value!.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');

    static string EngineVersion()
        => typeof(CrashReport).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    static string? SafeEnvironment(string name)
    {
        try { return Environment.GetEnvironmentVariable(name); }
        catch (System.Security.SecurityException) { return null; }
    }

    static string? SafeFolder(Environment.SpecialFolder folder)
    {
        try { return Environment.GetFolderPath(folder); }
        catch (ArgumentException) { return null; }
    }

    static string SafeTemp()
    {
        try { return Path.GetTempPath(); }
        catch (Exception ex) when (ex is IOException or System.Security.SecurityException) { return "."; }
    }
}
