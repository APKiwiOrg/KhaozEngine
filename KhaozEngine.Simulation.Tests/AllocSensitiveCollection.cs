using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// Per-assembly copy of the AllocSensitive collection marker (xUnit collection definitions are per-assembly).
/// Groups the zero-allocation assertion tests (which read <c>GC.GetAllocatedBytesForCurrentThread()</c>)
/// together with the allocation-heavy parallel-ForEach tests so they never run in parallel with each other,
/// keeping the per-thread allocation measurement from being taken while the parallel tests are churning the
/// GC on other threads. See <c>KhaozEngine.Tests.AllocSensitiveCollection</c> for the canonical doc comment;
/// this copy exists because Ecs moved to this assembly in the test-monolith split.
/// </summary>
[CollectionDefinition("AllocSensitive", DisableParallelization = true)]
public sealed class AllocSensitiveCollection { }
