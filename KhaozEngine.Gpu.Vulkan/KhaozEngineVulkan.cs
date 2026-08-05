using KhaozEngine.Gpu.Vulkan.Internal;

namespace KhaozEngine.Gpu.Vulkan
{
    /// <summary>
    /// The public surface of the engine-owned native Vulkan backend, and for now the whole of it: one call that
    /// registers the backend with <see cref="GpuBackendProviders"/>.
    /// <para>
    /// WHAT THIS PACKAGE CAN DO TODAY. Registration and the machine-capability probe are real: registering makes
    /// the provider reachable, and <see cref="GpuBackendSelector.IsBackendSupported"/> then answers for this
    /// machine by resolving a Vulkan loader, creating a throwaway instance at the 1.3 floor and reading every
    /// physical device against section 5.2's requirements. Creating a DEVICE is not built yet and throws a
    /// message naming the row that builds it
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/514). Work-breakdown row 2 of
    /// <c>docs/design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md</c>, plus row 3's
    /// <see cref="GpuBackendKind.VulkanNative"/> append, which is what gives the registration a NAME and makes
    /// the backend nameable through <c>KE_GRAPHICS_BACKEND=vulkan-native</c>. Naming it today reaches the
    /// refusal above, through the reported fallback rather than a crash.
    /// </para>
    /// <para>
    /// This type is safe to touch on ANY operating system, and unlike the Direct3D 11 package it needs no
    /// guard to make that true (decision V-P1). Vulkan is not a Windows API: the same managed code runs on
    /// Windows and Linux, the loader is resolved at runtime, and a machine without one answers the functional
    /// probe with a reason and routes through the existing fallback. The
    /// <c>[SupportedOSPlatformGuard]</c>-over-<c>NoInlining</c> apparatus <c>KhaozEngine.Gpu.D3D11</c> carries
    /// has no analogue here, and copying it across by analogy would add a boundary this backend does not have.
    /// </para>
    /// </summary>
    public static class KhaozEngineVulkan
    {
        // One provider instance for the process. It holds no device and no state of its own: the machine
        // capability answer it produces is cached by GpuBackendSelector, not here.
        static readonly VulkanBackendProvider _provider = new();

        /// <summary>
        /// Register this backend so <see cref="GpuBackendKind.VulkanNative"/> can be created. Call once at
        /// consumer startup, before any device is created. Idempotent (a repeated call replaces the same
        /// provider) and thread-safe.
        /// <para>
        /// Safe to call on every operating system, and meant to be called unconditionally: registering says a
        /// provider EXISTS, which is a fact about the app's wiring, while whether the machine can run it is a
        /// separate question answered by <see cref="GpuBackendSelector.IsBackendSupported"/> through the
        /// provider's own functional probe. Decision V-I4 keeps those two apart on purpose, and here the reason
        /// bites harder than it did on Direct3D 11: on Linux the OS probe already returns
        /// <see cref="GpuBackendKind.Vulkan"/>, so a native request that fails falls back to the incumbent Vulkan
        /// backend and reports <see cref="GpuBackendSource.FallbackAfterFailure"/>, which in a log line looks a
        /// great deal like a forgotten registration. A forgotten registration THROWS instead, and telling those
        /// two apart is what the whole soak measurement rests on.
        /// </para>
        /// </summary>
        public static void Register() => GpuBackendProviders.Register(GpuBackendKind.VulkanNative, _provider);
    }
}
