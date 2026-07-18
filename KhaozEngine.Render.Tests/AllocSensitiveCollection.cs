using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// Groups the zero-allocation assertion tests (which read <c>GC.GetAllocatedBytesForCurrentThread()</c>) together
/// with the allocation-heavy parallel-ForEach tests. Tests in one xUnit collection never run in parallel with each
/// other, so the per-thread allocation measurement is never taken while the parallel tests are churning the GC on
/// other threads (which otherwise flakes the zero-alloc assertion via concurrent gen-0 reconciliation).
/// <c>DisableParallelization</c> also keeps the whole group off the parallel pool. Reference it by name with
/// <c>[Collection("AllocSensitive")]</c>.
///
/// Per-assembly copy: xUnit collection definitions do not cross assemblies, and the Render3D tests here use this
/// collection, so each split assembly that references it carries its own identical definition.
/// </summary>
[CollectionDefinition("AllocSensitive", DisableParallelization = true)]
public sealed class AllocSensitiveCollection { }
