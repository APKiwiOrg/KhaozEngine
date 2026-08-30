using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// Groups the zero-allocation assertion tests (which read <c>GC.GetAllocatedBytesForCurrentThread()</c>) so the
/// per-thread measurement is never taken while other tests in this assembly churn the GC on other threads, which
/// otherwise flakes the assertion via concurrent gen-0 reconciliation. <c>DisableParallelization</c> also keeps the
/// whole group off the parallel pool. Reference it by name with <c>[Collection("AllocSensitive")]</c>.
///
/// Per-assembly copy: xUnit collection definitions do not cross assemblies, so each split assembly that uses this
/// collection carries its own identical definition.
/// </summary>
[CollectionDefinition("AllocSensitive", DisableParallelization = true)]
public sealed class AllocSensitiveCollection { }
