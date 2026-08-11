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
    /// <para>
    /// HOSTILE-SAFE, because everything it reads off the exception is VIRTUAL. <c>Message</c> and
    /// <c>ToString</c> are both overridable and both run here on the runtime's crash path, so an override that
    /// throws (a hostile one, or simply one that dereferences state the crash already tore down) would
    /// otherwise take the whole report with it. Each is read on its own, and a throwing one contributes a line
    /// saying so instead of ending the render.
    /// </para>
    /// </summary>
    /// <param name="processLabel">Name of the crashing process.</param>
    /// <param name="context">What kind of crash this is, e.g. <c>Unhandled exception (terminating)</c>.</param>
    /// <param name="exception">The exception, when the crash carried one.</param>
    /// <param name="raw">The raw thrown object, used when <paramref name="exception"/> is null.</param>
    /// <param name="timestamp">When the crash happened.</param>
    /// <returns>The rendered report.</returns>
    public static string Format(string processLabel, string context, Exception? exception, object? raw,
        DateTimeOffset timestamp)
        => FormatHeader(processLabel, context, exception, raw, timestamp) + FormatStack(exception, raw);

    /// <summary>
    /// The report's header, ending with the <c>--- stack ---</c> marker: everything that is known without
    /// asking the exception to render itself. This is what <see cref="Write"/> puts on disk FIRST, so a report
    /// exists before the one call that a hostile exception can still make fail.
    /// </summary>
    internal static string FormatHeader(string processLabel, string context, Exception? exception, object? raw,
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

        // The TYPE first and on its own line, read through GetType, which is the one fact here no override can
        // take away: it is not virtual, so it answers even for an exception whose every other member throws.
        Line(text, "exception", SafeTypeName(exception, raw));
        Line(text, "message", SafeMessage(exception, raw));

        text.Append("--- stack ---").Append('\n');
        return text.ToString();
    }

    /// <summary>
    /// The report's second half: the exception's own full text. Rendered separately from the header because
    /// this is the part a hostile <c>ToString</c> can refuse to produce, and a refusal here must cost the
    /// stack rather than the report.
    /// </summary>
    internal static string FormatStack(Exception? exception, object? raw)
    {
        object? subject = exception ?? raw;
        if (subject is null) return "No exception object was available.\n";

        try { return (subject.ToString() ?? "No exception object was available.") + "\n"; }
        catch (Exception ex)
        {
            return "The exception's own ToString threw " + TypeNameOf(ex)
                + ", so the header above is the whole report.\n";
        }
    }

    /// <summary>
    /// Writes one report per <paramref name="options"/> and returns its full path, or null when nothing could
    /// be written. Never throws.
    /// <para>
    /// THE HEADER IS WRITTEN BEFORE THE STACK IS ASKED FOR, AND THE PRUNE RUNS ONLY AFTER A FILE EXISTS. Both
    /// orderings are the same lesson: this runs where the thing being reported is already hostile. Rendering
    /// the whole report as one argument meant a throwing <c>Message</c> or <c>ToString</c> lost the header
    /// too, and pruning first meant that crash ALSO deleted the oldest report it kept, so the net effect of a
    /// hostile exception was one report destroyed and none written.
    /// </para>
    /// </summary>
    /// <param name="options">Where the file goes and what names it. Null returns null.</param>
    /// <param name="context">What kind of crash this is.</param>
    /// <param name="exception">The exception, when the crash carried one.</param>
    /// <param name="raw">The raw thrown object, used when <paramref name="exception"/> is null.</param>
    /// <returns>The crash file's full path, or null.</returns>
    public static string? Write(CrashReportOptions options, string context, Exception? exception, object? raw)
        => WriteAt(options, context, exception, raw, DateTimeOffset.UtcNow);

    /// <summary>
    /// <see cref="Write"/> with the clock passed in, so a test can name the file the writer is about to open
    /// and stage a failure on exactly that path. Internal for that reason and no other.
    /// </summary>
    internal static string? WriteAt(CrashReportOptions options, string context, Exception? exception, object? raw,
        DateTimeOffset timestamp)
    {
        if (options is null) return null;

        string directory;
        string prefix;
        string path;
        try
        {
            directory = string.IsNullOrWhiteSpace(options.Directory) ? DefaultDirectory : options.Directory!;
            prefix = FileNamePrefix(options.ProcessLabel);
            Directory.CreateDirectory(directory);

            path = Path.Combine(directory, FileName(prefix, timestamp));
            File.WriteAllText(path, FormatHeader(options.ProcessLabel, context, exception, raw, timestamp));
        }
        catch (Exception)
        {
            // Crash path: a report that cannot be written is not worth a second exception on top of the first.
            // Nothing was written, so nothing is pruned either.
            return null;
        }

        // Best-effort from here on: the file already carries the type, the message and the context, which is
        // what the investigation starts from. A stack that cannot be appended does not unwrite any of that.
        try { File.AppendAllText(path, FormatStack(exception, raw)); }
        catch (Exception) { }

        // AFTER the write, so the count includes the report just made and a failed write cannot delete one.
        LogFilePruner.KeepNewest(directory, Math.Max(1, options.MaxRetainedReports),
            name => name.StartsWith(prefix + "-", StringComparison.Ordinal)
                && name.EndsWith(".log", StringComparison.Ordinal));
        return path;
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

    /// <summary>The name of one report file: the stem, then the stamp that separates it from its siblings.</summary>
    internal static string FileName(string prefix, DateTimeOffset timestamp)
        => prefix + "-"
            + timestamp.ToUniversalTime().ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".log";

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

    // GetType is not virtual, so the TYPE is the one fact a hostile exception cannot withhold. FullName is null
    // only for a few open generic shapes, where the short name is still an answer.
    static string SafeTypeName(Exception? exception, object? raw)
    {
        object? subject = exception ?? raw;
        return subject is null ? "unknown" : TypeNameOf(subject);
    }

    static string TypeNameOf(object subject)
    {
        Type type = subject.GetType();
        return type.FullName ?? type.Name;
    }

    // Message is VIRTUAL, and so is the ToString the raw-object case falls back to. Each is read inside its own
    // try, so a throwing one costs its own line and nothing above it.
    static string SafeMessage(Exception? exception, object? raw)
    {
        if (exception is not null)
        {
            try { return exception.Message; }
            catch (Exception ex) { return "the exception's own Message threw " + TypeNameOf(ex); }
        }

        if (raw is null) return "none";
        try { return raw.ToString() ?? "none"; }
        catch (Exception ex) { return "the thrown object's own ToString threw " + TypeNameOf(ex); }
    }

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
