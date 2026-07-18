using Xunit;

namespace KhaozEngine.Tests.Logging;

/// <summary>
/// Serial collection for logging tests that touch process-global state (the static <c>Log</c> facade,
/// <c>CrashHandler</c>, <see cref="System.Console"/> streams, <c>Trace.Listeners</c>). Classes in this
/// collection run sequentially and do not run in parallel with any other collection, so global-state
/// swaps cannot cross-talk between test classes.
/// </summary>
[CollectionDefinition("LoggingSerial", DisableParallelization = true)]
public sealed class LoggingSerialCollection { }
