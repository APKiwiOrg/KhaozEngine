using System.Collections.Generic;

namespace KhaozEngine.Diagnostics;

/// <summary>Construction-time configuration for a <see cref="LogManager"/>.</summary>
public sealed class LoggerOptions
{
    /// <summary>Entries below this level are dropped. Runtime-adjustable via <see cref="LogManager.MinimumLevel"/>.</summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    /// <summary>When true, writes happen inline on the calling thread (deterministic; used by tests). When false, a background writer thread drains a queue.</summary>
    public bool Synchronous { get; set; }

    /// <summary>Bounded async queue capacity. When full, entries are dropped (counted in <see cref="LogManager.DroppedCount"/>).</summary>
    public int QueueCapacity { get; set; } = 10_000;

    /// <summary>Clock used for entry timestamps.</summary>
    public IClock Clock { get; set; } = SystemClock.Instance;

    /// <summary>Category used by the convenience Log facade methods.</summary>
    public string DefaultCategory { get; set; } = "App";

    /// <summary>Sinks attached at construction. More can be added later via <see cref="LogManager.AddSink"/>.</summary>
    public IList<ILogSink> Sinks { get; } = new List<ILogSink>();
}
