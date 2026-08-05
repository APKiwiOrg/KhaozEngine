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
    /// <c>docs/design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md</c>.
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
        /// <summary>
        /// The <see cref="GpuBackendKind"/> this backend registers under, PINNED TO ITS ORDINAL because the named
        /// member does not exist yet.
        /// <para>
        /// Row 3 (https://github.com/APKiwiOrg/KhaozEngine/issues/513) appends
        /// <c>GpuBackendKind.VulkanNative = 5</c> with that explicit ordinal (decision V-I1), and its issue says
        /// it PARALLELISES with row 2 rather than preceding it, so this row may not depend on it. It may not do
        /// its work either: appending the member without row 3's <c>GoldenBackendToken</c> mapping would leave a
        /// kind that throws out of the golden filename path, which is precisely the silent-orphan failure that
        /// row's audit test exists to prevent.
        /// </para>
        /// <para>
        /// So the value is written as the ordinal the design pins, and the registry keys by VALUE, which makes
        /// registration correct TODAY rather than deferred: the provider is really registered, the probe really
        /// answers through <see cref="GpuBackendSelector.IsBackendSupported"/>, and the test-side seat really
        /// registers something. The alternative considered and rejected was a <see cref="Register"/> that throws
        /// until row 3 lands, which would be a public method that lies about what the package does and a
        /// registration seat with nothing to register.
        /// </para>
        /// <para>
        /// It does not get to outlive its reason. <c>VulkanBackendPinnedKindTests</c> in
        /// <c>KhaozEngine.Render.Tests</c> fails the moment ordinal 5 gains a name, and its message is the
        /// instruction: replace this cast with <c>GpuBackendKind.VulkanNative</c> and delete the test. A magic
        /// number nobody is forced to remove is how a temporary shim becomes permanent.
        /// </para>
        /// </summary>
        internal const GpuBackendKind VulkanNativeKind = (GpuBackendKind)5;

        // One provider instance for the process. It holds no device and no state of its own: the machine
        // capability answer it produces is cached by GpuBackendSelector, not here.
        static readonly VulkanBackendProvider _provider = new();

        /// <summary>
        /// Register this backend so the native Vulkan kind can be created. Call once at consumer startup, before
        /// any device is created. Idempotent (a repeated call replaces the same provider) and thread-safe.
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
        public static void Register() => GpuBackendProviders.Register(VulkanNativeKind, _provider);
    }
}
