using System.Runtime.CompilerServices;
using System.Reflection;

[assembly: InternalsVisibleTo("Renderer.Perf.Tests")]

[assembly: InternalsVisibleTo("Renderer.Perf.Verify")]

namespace KhaozEngine.Tests.Gpu;

// The offline executable references this assembly for the single shared verifier. It must not
// acquire the test host's provider-registration side effects when this module initializes.
internal static class RendererPerfAssemblyAccess
{
    internal static bool OfflineVerifier => Assembly.GetEntryAssembly()?.GetName().Name == "Renderer.Perf.Verify";
}
