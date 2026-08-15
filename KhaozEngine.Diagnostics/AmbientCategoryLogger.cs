using System;

namespace KhaozEngine.Diagnostics;

/// <summary>
/// An <see cref="ILogger"/> bound to one category and to the AMBIENT <see cref="Log"/> facade rather than to any
/// one <see cref="LogManager"/>. This is what <see cref="Log.For{T}"/> and <see cref="Log.Get(string)"/> hand
/// out, and it is the reason a logger cached in a <c>static readonly</c> field cannot go silent (#616): it holds
/// no manager to be left pointing at, and finds the configured one when it is asked to log.
///
/// <para><b>ONE VOLATILE READ PER CALL, AND NOTHING ELSE.</b> That read is <see cref="Log.Current"/>, and it
/// replaces the field read a manager-pinned logger did, so the write path costs the same and allocates nothing
/// it did not allocate before. The read happens ONCE per message, into a local, so an entry cannot be split
/// across a concurrent <see cref="Log.Configure(LoggerOptions)"/>: it is filtered, stamped and submitted against
/// a single manager, whichever one was configured when the call started. If that manager is shut down before the
/// submit lands, the entry is dropped rather than thrown, which is what <see cref="LogManager.Submit"/> already
/// guarantees for every other racing writer.</para>
///
/// <para>Contrast <see cref="CategoryLogger"/>, which is pinned to the manager that created it. That one is
/// correct for the injected path and stays.</para>
/// </summary>
internal sealed class AmbientCategoryLogger : ILogger
{
    /// <summary>
    /// The category stamped on every entry, or empty for "whatever the configured manager calls its default".
    /// Empty is resolved per call rather than at construction, so the default-category logger the
    /// <see cref="Log"/> convenience methods share follows a reconfigure that changes
    /// <see cref="LoggerOptions.DefaultCategory"/>.
    /// </summary>
    private readonly string category;

    internal AmbientCategoryLogger(string? category)
    {
        this.category = category ?? string.Empty;
    }

    /// <inheritdoc />
    public string Category
    {
        get
        {
            if (category.Length != 0) return category;
            return KhaozEngine.Diagnostics.Log.Current?.DefaultCategory ?? string.Empty;
        }
    }

    /// <inheritdoc />
    public bool IsEnabled(LogLevel level)
    {
        var m = KhaozEngine.Diagnostics.Log.Current;
        return m is not null && m.IsEnabled(level);
    }

    /// <inheritdoc />
    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        // Read the manager once. Every step below is against this one, so a reconfigure mid-call sends the whole
        // entry to the old manager or the whole entry to the new one, never half of each.
        var m = KhaozEngine.Diagnostics.Log.Current;
        if (m is null || !m.IsEnabled(level)) return;
        m.Submit(new LogEntry(m.Now, level, category.Length == 0 ? m.DefaultCategory : category,
            message ?? string.Empty, exception));
    }

    /// <inheritdoc />
    public void Trace(string message, Exception? exception = null) => Log(LogLevel.Trace, message, exception);
    /// <inheritdoc />
    public void Debug(string message, Exception? exception = null) => Log(LogLevel.Debug, message, exception);
    /// <inheritdoc />
    public void Info(string message, Exception? exception = null)  => Log(LogLevel.Info,  message, exception);
    /// <inheritdoc />
    public void Warn(string message, Exception? exception = null)  => Log(LogLevel.Warn,  message, exception);
    /// <inheritdoc />
    public void Error(string message, Exception? exception = null) => Log(LogLevel.Error, message, exception);
    /// <inheritdoc />
    public void Fatal(string message, Exception? exception = null) => Log(LogLevel.Fatal, message, exception);
}
