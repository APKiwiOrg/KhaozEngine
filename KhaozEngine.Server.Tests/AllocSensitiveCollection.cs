using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// Per-assembly copy of the AllocSensitive collection marker (xUnit collection definitions are per-assembly).
/// Groups the zero-allocation assertion tests (which read <c>GC.GetAllocatedBytesForCurrentThread()</c>)
/// together with the allocation-heavy tests so they never run in parallel with each other, keeping the
/// per-thread allocation measurement from being taken while a parallel test is churning the GC on another
/// thread. See <c>KhaozEngine.Tests.AllocSensitiveCollection</c> for the canonical doc comment; this copy
/// exists because Replication and Benchmarks moved to this assembly in the test-monolith split.
/// </summary>
[CollectionDefinition("AllocSensitive", DisableParallelization = true)]
public sealed class AllocSensitiveCollection { }
