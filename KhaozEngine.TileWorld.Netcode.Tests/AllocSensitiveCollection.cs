using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// Per-assembly copy of the AllocSensitive collection marker (xUnit collection definitions are per-assembly).
/// Groups the tests that read <c>GC.GetAllocatedBytesForCurrentThread()</c> so a per-thread allocation
/// measurement is never taken while another class in this assembly is churning the GC on another thread.
/// See <c>KhaozEngine.Tests.AllocSensitiveCollection</c> in the rump test project for the canonical doc comment.
/// </summary>
[CollectionDefinition("AllocSensitive", DisableParallelization = true)]
public sealed class AllocSensitiveCollection { }
