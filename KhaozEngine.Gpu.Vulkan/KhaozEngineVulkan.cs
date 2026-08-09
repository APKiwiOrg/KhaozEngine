using KhaozEngine.Gpu.Vulkan.Internal;

namespace KhaozEngine.Gpu.Vulkan
{
    /// <summary>
    /// The public surface of the engine-owned native Vulkan backend, and for now the whole of it: one call that
    /// registers the backend with <see cref="GpuBackendProviders"/>.
    /// <para>
    /// WHAT THIS PACKAGE CAN DO TODAY: RENDER, HEADLESS OR WINDOWED, WITH NOTHING LEFT REFUSING. Registering makes
    /// the provider reachable, and <see cref="GpuBackendSelector.IsBackendSupported"/> answers for this machine by
    /// resolving a Vulkan loader, creating a throwaway instance at the 1.3 floor and reading every physical device
    /// against section 5.2's requirements.
    /// <c>GpuDeviceContext.CreateHeadless(GpuBackendKind.VulkanNative)</c> builds a real <c>VkDevice</c> on the one
    /// refcounted process instance, with its features enabled by name and its device-loss latch armed, and
    /// <c>CreateForWindow</c> reaches a real windowed device with a platform surface, a swapchain and a present
    /// boundary that acquires, resizes, recreates and presents. Between those, every member of
    /// <c>IGpuResourceFactory</c> and every member of <c>IGpuCommandList</c> is built: the timeline and its fences,
    /// the block suballocator, the uniform ring, buffers, textures, samplers and staging, descriptors and the bind
    /// flush, dynamic rendering, the shader path, both pipelines, the layout tracker, and the draw, dispatch and
    /// transfer paths.
    /// </para>
    /// <para>
    /// WHAT IS NOT DONE IS THE ROLLOUT, WHICH IS A DIFFERENT SENTENCE FROM "NOT BUILT". Nothing selects this
    /// backend for anyone: <c>ProbeOS</c> still maps Linux to <see cref="GpuBackendKind.Vulkan"/>, the headless
    /// default is unchanged, and this backend is reached only by an explicit kind or the
    /// <c>KE_GRAPHICS_BACKEND=vulkan-native</c> token. The default flip waits on the five rollout gates of
    /// section 17 (https://github.com/APKiwiOrg/KhaozEngine/issues/529), two of which are a field session and a
    /// human windowed pass that no CI leg can stand in for. The Veldrid Vulkan backend stays selectable
    /// indefinitely either way (decision V-RO2), so a regression here is one environment variable away from an
    /// A/B on the same build.
    /// </para>
    /// <para>
    /// THIS PARAGRAPH IS A LEDGER, AND A STALE ONE IS WORSE THAN NONE, because it is the first thing a consumer
    /// reads about the package. It went nine work-breakdown rows out of date once already
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/560), still promising that a windowed device throws and
    /// that a headless one cannot record, submit or make a resource, long after all of that was live. Anything
    /// that changes what this package can DO changes this paragraph in the same commit.
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
