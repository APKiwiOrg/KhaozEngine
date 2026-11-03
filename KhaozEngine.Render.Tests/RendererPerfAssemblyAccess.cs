using System.Reflection;

namespace KhaozEngine.Tests.Gpu;

// Retained for direct offline loading of the engine test assembly. Renderer tooling now owns
// the verifier and has no provider-registration module initializers.
internal static class RendererPerfAssemblyAccess
{
    internal static bool OfflineVerifier => Assembly.GetEntryAssembly()?.GetName().Name == "Renderer.Perf.Verify";
}
