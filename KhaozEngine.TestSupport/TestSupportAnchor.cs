namespace KhaozEngine.Tests
{
    /// <summary>
    /// Anchor for the shared test-support assembly. Every split test project references
    /// <c>KhaozEngine.TestSupport</c>, so its dependency set is intersected into the affected-set of all
    /// of them: it may reference nothing beyond <c>KhaozEngine.Primitives</c> (plus xunit). Cross-cluster
    /// helpers that fit that ceiling live here as they are promoted; package-coupled fakes live in
    /// <c>KhaozEngine.TestSupport.Services</c> instead. In wave 1 no helper qualified (GpuFactAttribute
    /// needs Gpu, DictionaryCatalog needs App, and the collection markers are per-assembly), so this
    /// anchor keeps the assembly non-empty until later waves populate it.
    /// </summary>
    internal static class TestSupportAnchor
    {
    }
}
