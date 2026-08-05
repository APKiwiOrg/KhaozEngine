using System;
using KhaozEngine.Gpu.D3D11;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Registers the real native Direct3D 11 backend for the whole test process, once, on demand.
    /// <para>
    /// It lives in the SHARED support project rather than in one test assembly because the gate that decides
    /// whether a GPU test runs is not per-assembly. Every assembly with a <c>[GpuFact]</c> in it references this
    /// project by definition (that is where the attribute lives), so putting the registration here means no test
    /// project can be wired for GPU tests and still be missing the backend. It was in
    /// <c>KhaozEngine.Render.Tests</c> first, and <c>KhaozEngine.MapEditor.Tests</c> paid for it: on the native leg
    /// all four of its GPU tests threw <c>GpuBackendProviderMissingException</c>, because it takes
    /// <c>[GpuFact]</c> from here and never had a registration line of its own. A per-project line is a line every
    /// future test project must remember, and the failure for forgetting it is a red leg that reads as a device
    /// problem.
    /// </para>
    /// <para>
    /// WHAT PULLS THE TRIGGER, and why it is not a <c>[ModuleInitializer]</c> any more. This project is a LIBRARY,
    /// and CA2255 is an error under the repo's warnings-as-errors, correctly: a module initializer in a library
    /// runs when the CLR happens to load the assembly, which is the same lazy, machine-dependent property that got
    /// the mechanism rejected in the backend package itself (section 4.1 of
    /// <c>docs/design/D3D11-NATIVE-BACKEND-DESIGN-2026-08-02.md</c>). The suppression route was available and is
    /// the wrong trade here, because the trigger this needs is not "assembly load" at all. It is "an assembly that
    /// runs GPU tests". So <see cref="GpuFactAttribute"/> carries a static constructor that calls
    /// <see cref="EnsureRegistered"/>, and the CLR runs that the first time the attribute type is touched, which
    /// is during xUnit's discovery pass in any assembly with a <c>[GpuFact]</c> or a <c>[GpuTheory]</c> in it, well
    /// before any test body. The registration therefore follows the ATTRIBUTE rather than the assembly, which is
    /// the property the shared home was moved here to get.
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
