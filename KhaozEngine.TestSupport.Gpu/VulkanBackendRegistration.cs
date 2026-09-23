using System;
using KhaozEngine.Gpu.Vulkan;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Registers the real native Vulkan backend for the whole test process, once, on demand. The sibling of
    /// <see cref="D3D11BackendRegistration"/>, in the SAME project and for the same reason.
    /// <para>
    /// It remains in the shared support project as the target of the Render.Tests module initializer belt. The
    /// packable <c>KhaozEngine.Gpu.TestKit</c> owns registration for <c>[GpuFact]</c>, <c>[GpuTheory]</c> and
    /// direct consumers. This helper keeps filtered plain <c>[Fact]</c> registration tests independent of the
    /// attribute gate.
    /// </para>
    /// <para>
    /// The test assembly module initializer calls <see cref="EnsureRegistered"/>. Attribute construction goes
    /// through <c>KhaozEngine.Gpu.TestKit.GpuTestGate</c>, which registers all three native backends directly.
    /// </para>
    /// <para>
    /// REGISTERING IS ALL THIS DOES, and it now decides whether a whole CI leg has a backend at all. The provider
    /// it registers answers a real functional probe (a Vulkan loader, a throwaway instance at the 1.3 floor,
    /// every physical device read against the design's requirements) and builds a real device behind it, so the
    /// <c>vulkan-native</c> leg (https://github.com/APKiwiOrg/KhaozEngine/issues/529) runs the whole GPU suite
    /// through this line. The seat was taken a row before the device existed, deliberately: the row that builds
    /// the device is the row that must NOT also have to discover where the registration goes. What it registers
    /// under is <c>GpuBackendKind.VulkanNative</c>, which arrived a row later than this seat did and replaced the
    /// pinned ordinal the seat was first written against.
    /// </para>
    /// <para>
    /// It is process-wide state, so a test that needs the native kind UNREGISTERED says so explicitly with
    /// <c>BackendProviderScope(kind, provider: null)</c> and belongs in the non-parallel
    /// <c>GraphicsBackendGlobalState</c> collection, exactly as the Direct3D 11 rows do.
    /// </para>
    /// </summary>
    public static class VulkanBackendRegistration
    {
        // A Lazy rather than a bare flag, for the reason spelled out on the Direct3D 11 sibling: this has to be
        // safe to call from several threads AND has to have FINISHED registering by the time any of them returns,
        // which an Interlocked one-shot gives only the first half of.
        static readonly Lazy<bool> Registration = new(() =>
        {
            KhaozEngineVulkan.Register();
            return true;
        });

        /// <summary>
        /// Register the native Vulkan backend if this process has not already, and return once it is registered.
        /// Idempotent and thread-safe, so every caller that wants the guarantee can simply ask for it rather than
        /// reasoning about who asked first.
        /// </summary>
        public static void EnsureRegistered() => _ = Registration.Value;
    }
}
