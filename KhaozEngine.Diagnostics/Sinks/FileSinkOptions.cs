using System;

namespace KhaozEngine.Diagnostics;

/// <summary>Configuration for a <see cref="FileSink"/>.</summary>
public sealed class FileSinkOptions
{
    /// <summary>Active log file path (required).</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>When set, an existing active log is copied here on open (rotate-on-launch).</summary>
    public string? PreviousPath { get; set; }

    /// <summary>When set, the active file rotates to a numbered archive once it reaches this many bytes.</summary>
    public long? MaxBytes { get; set; }

    /// <summary>Maximum number of numbered archives to retain (oldest pruned). Defaults to 1 when size rotation is on.</summary>
    public int? MaxFiles { get; set; }

    /// <summary>Optional per-sink threshold; entries below it are skipped.</summary>
    public LogLevel? MinimumLevel { get; set; }

    /// <summary>Optional custom line formatter. Defaults to <see cref="LogFormatter.Format"/>.</summary>
    public Func<LogEntry, string>? Formatter { get; set; }
}
