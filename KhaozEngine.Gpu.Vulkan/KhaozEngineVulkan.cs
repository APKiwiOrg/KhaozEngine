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
    /// THIS BACKEND IS THE LINUX DEFAULT SINCE 17.40.0. <c>GpuBackendSelector.ProbeOS</c> maps Linux, and the
    /// unrecognized-OS catch-all, to <see cref="GpuBackendKind.VulkanNative"/>, so a game that references this
    /// package and calls <see cref="Register"/> runs on it without naming anything, and a game that does
    /// neither falls back to <see cref="GpuBackendKind.Vulkan"/> with a WARN naming the missing registration
    /// rather than failing to boot. The flip was taken by DECISION on 2026-08-22, ahead of two of the five
    /// rollout gates of section 17 (https://github.com/APKiwiOrg/KhaozEngine/issues/529): gate 3's <c>sync</c>
    /// validation job and gate 5's human windowed pass are still open and still carry an instrument. The
    /// Veldrid Vulkan backend stays selectable by <c>KE_GRAPHICS_BACKEND=vulkan</c> for ONE release (decision
    /// V-RO2), so a regression here is one environment variable away from an A/B on the same build.
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
        /// provider's own functional probe. Decision V-I4 keeps those two apart on purpose: a request that
        /// fails on a machine falls back to the platform default and reports
        /// <see cref="GpuBackendSource.FallbackAfterFailure"/>, which in a log line looks a great deal like a
        /// forgotten registration, and a forgotten registration throws. Telling the two apart is what the whole
        /// soak measurement rests on.
        /// </para>
        /// <para>
        /// A WINDOWED GAME NEEDS NO CALL TO THIS since 18.0.0. <c>AppWindow</c> registers the running platform's
        /// backend at boot through <c>GpuBackends.RegisterResolvedIfUnregistered(preference)</c>, which also seats the
        /// preference or override the process resolves to, and this package ships in the
        /// <c>KhaozEngine.Game2D</c> and <c>KhaozEngine.Game3D</c> umbrellas. This member is for a custom or
        /// headless host, and for a process that wants a backend other than its own platform's.
        /// </para>
        /// </summary>
        public static void Register() => GpuBackendProviders.Register(GpuBackendKind.VulkanNative, _provider);
    }
}
