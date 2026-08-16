using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The shared process-global state is <c>SpirvCompileCache.Shared</c>'s counters. A class in here asserts that
    /// some span of work compiled NOTHING, which is a delta on a counter every other GPU class in the assembly
    /// also moves, so a class reading it while another compiles a shader on the parallel pool would fail for a
    /// reason that has nothing to do with what it is testing.
    /// <para>
    /// The attribute that does the work is the <c>DisableParallelization</c> on this definition, not the
    /// <c>[Collection]</c> on the class (see the rule in <c>AGENTS.md</c>): a collection name with no definition
    /// anywhere serializes its own classes against each other and leaves them running in parallel with everything
    /// else, which is exactly the window this exists to close.
    /// </para>
    /// </summary>
    [CollectionDefinition("SpirvCompileCacheSerial", DisableParallelization = true)]
    public sealed class SpirvCompileCacheSerialCollection { }
}
