using System.Runtime.CompilerServices;
using KhaozEngine.Gpu.D3D11;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Registers the real native Direct3D 11 backend for the whole test process, once, at assembly load.
    /// <para>
    /// This is the ONE place section 4.1 of <c>docs/design/D3D11-NATIVE-BACKEND-DESIGN-2026-08-02.md</c> allows a
    /// <c>[ModuleInitializer]</c>, and the reason it is allowed here and rejected in the backend package is the
    /// same fact in two settings. The CLR loads an assembly lazily on first type reference, so in a consuming app
    /// a package reference with no static type use gives no guarantee the initializer ever runs, which is a
    /// silent, machine-dependent failure in a rollout whose entire purpose is attributing measurements to a
    /// backend. A TEST assembly is always loaded, by definition, so the property the consumer case lacks is
    /// guaranteed and the initializer is simply the tidiest way to say "these tests run with the backend
    /// registered".
    /// </para>
    /// <para>
    /// It is process-wide state, so tests that need the native kind UNREGISTERED say so explicitly with
    /// <c>BackendProviderScope(kind, provider: null)</c> and belong in the non-parallel
    /// <c>GraphicsBackendGlobalState</c> collection. Pinning the unregistered behaviour that way is deliberately
    /// stronger than relying on nothing being registered: it holds whatever the ambient registration happens to
    /// be, including on the day a second backend package registers here too.
    /// </para>
    /// </summary>
    internal static class D3D11BackendRegistration
    {
        [ModuleInitializer]
        internal static void RegisterNativeD3D11() => KhaozEngineD3D11.Register();
    }
}
