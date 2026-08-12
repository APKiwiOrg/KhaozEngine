using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// Serial collection for tests that touch the process-global static <c>Log</c> facade. Classes in this
/// collection run sequentially and never in parallel with any other collection, so a test that swaps the
/// ambient logger and restores it cannot leave a window in which another class reads the other value.
///
/// Per-assembly copy: xUnit collection definitions do not cross assemblies, so this is the same definition
/// <c>KhaozEngine.Foundation.Tests</c> carries under the same name, following the per-assembly copy note on
/// <see cref="AllocSensitiveCollection"/>. Reference it by name with <c>[Collection("LoggingSerial")]</c>.
/// </summary>
[CollectionDefinition("LoggingSerial", DisableParallelization = true)]
public sealed class LoggingSerialCollection { }
