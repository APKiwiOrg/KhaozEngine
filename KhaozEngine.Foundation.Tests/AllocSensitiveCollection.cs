using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// Groups the zero-allocation assertion tests (which read <c>GC.GetAllocatedBytesForCurrentThread()</c>) into a
/// non-parallel collection. Tests in one xUnit collection never run in parallel with each other, and
/// <c>DisableParallelization</c> keeps the whole group off the parallel pool as well, so the per-thread
/// allocation measurement is never taken while the rest of the assembly churns the GC on other threads (which
/// otherwise flakes the assertion via concurrent gen-0 reconciliation). Reference it by name with
/// <c>[Collection("AllocSensitive")]</c>.
///
/// Per-assembly copy: xUnit collection definitions do not cross assemblies, so each split assembly that uses
/// this collection carries its own identical definition. The Render, Simulation, Server and Game test
/// assemblies each have one too.
/// </summary>
[CollectionDefinition("AllocSensitive", DisableParallelization = true)]
public sealed class AllocSensitiveCollection { }
