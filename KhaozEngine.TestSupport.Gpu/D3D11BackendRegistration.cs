using System;
using KhaozEngine.Gpu.D3D11;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Registers the real native Direct3D 11 backend for the whole test process, once, on demand.
    /// <para>
    /// It remains in the shared support project as the target of the Render.Tests module initializer belt. The
    /// packable <c>KhaozEngine.Gpu.TestKit</c> owns registration for <c>[GpuFact]</c>, <c>[GpuTheory]</c> and
    /// direct consumers. This helper keeps filtered plain <c>[Fact]</c> registration tests independent of the
    /// attribute gate.
    /// </para>
    /// <para>
    /// This project is a library, so it cannot carry the module initializer itself under CA2255. The test
    /// assembly owns that initializer and calls <see cref="EnsureRegistered"/> here. Attribute construction goes
    /// through <c>KhaozEngine.Gpu.TestKit.GpuTestGate</c>, which registers all three native backends directly.
    /// </para>
    /// <para>
    /// The one thing that hook does NOT cover is a registry test with no <c>[GpuFact]</c> anywhere near it, which
    /// <c>KhaozEngine.Render.Tests</c> has (the plain <c>[Fact]</c>s asserting the process really does have the
    /// real provider registered). Those had blanket coverage while the initializer lived in that assembly, so the
    /// belt stayed: that project keeps a thin <c>[ModuleInitializer]</c> of its own calling in here. A TEST project
    /// is application code, so CA2255 does not fire there, and the load guarantee a library cannot make is one a
    /// test assembly makes by definition.
    /// </para>
    /// <para>
    /// It is process-wide state, so tests that need the native kind UNREGISTERED say so explicitly with
    /// <c>BackendProviderScope(kind, provider: null)</c> and belong in the non-parallel
    /// <c>GraphicsBackendGlobalState</c> collection. Pinning the unregistered behaviour that way is deliberately
    /// stronger than relying on nothing being registered: it holds whatever the ambient registration happens to
    /// be, including on the day a second backend package registers here too.
    /// </para>
    /// </summary>
    public static class D3D11BackendRegistration
    {
        // A Lazy rather than a bare flag, and for a reason a flag would get wrong: this has to be safe to call
        // from several threads AND has to have FINISHED registering by the time any of them returns. An
        // Interlocked one-shot gives the first property and not the second, since the loser of the race walks
        // straight past a registration still in flight. Lazy's default mode blocks every other caller until the
        // factory has run, which is the same once-per-process shape GpuFactAttribute's two device probes use.
        static readonly Lazy<bool> Registration = new(() =>
        {
            KhaozEngineD3D11.Register();
            return true;
        });

        /// <summary>
        /// Register the native Direct3D 11 backend if this process has not already, and return once it is
        /// registered. Idempotent and thread-safe, so every caller that wants the guarantee can simply ask for it
        /// rather than reasoning about who asked first.
        /// </summary>
        public static void EnsureRegistered() => _ = Registration.Value;
    }
}
