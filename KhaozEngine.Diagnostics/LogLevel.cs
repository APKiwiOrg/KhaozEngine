namespace KhaozEngine.Diagnostics;

/// <summary>Severity of a log entry, ordered from most verbose (<see cref="Trace"/>) to most severe (<see cref="Fatal"/>).</summary>
public enum LogLevel
{
    /// <summary>Very fine-grained diagnostic detail.</summary>
    Trace,
    /// <summary>Debugging detail useful during development.</summary>
    Debug,
    /// <summary>Normal operational message.</summary>
    Info,
    /// <summary>Something unexpected but recoverable.</summary>
    Warn,
    /// <summary>A failure that affected an operation.</summary>
    Error,
    /// <summary>An unrecoverable failure, typically a crash.</summary>
    Fatal
}
