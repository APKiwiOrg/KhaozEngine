using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Windowing
{
    /// <summary>
    /// One call that registers the graphics backend this operating system runs on, so a game gets a device
    /// without naming a backend package in its own startup code.
    /// <para>
    /// WHY THIS EXISTS AT ALL, AND WHY IT IS HERE. <c>KhaozEngine.Gpu</c> builds no device of its own since
    /// 18.0.0: every backend arrives through <see cref="GpuBackendProviders"/>, and <c>KhaozEngine.Gpu</c>
    /// cannot reference a backend package without a dependency cycle. Something above the seam has to close
    /// that gap, and this package is the lowest thing above it that every windowed host and every in-repo
    /// headless host already has in its graph. The alternative shapes were a <c>[ModuleInitializer]</c> in each
    /// backend (runs on first type reference, so it fires on some machines and not others) and a reflection
    /// probe for the assembly (invisible to the compiler and to trimming), both of which the native-backend
    /// design rules out by name in its decision 4.1.
    /// </para>
    /// <para>
    /// REGISTERING IS NOT CHOOSING. This says a provider EXISTS, which is a fact about the app's wiring. Which
    /// backend actually runs is <see cref="GpuBackendSelector"/>'s answer, and whether this machine can run it
    /// is the separate functional probe behind <see cref="GpuBackendSelector.IsBackendSupported"/>. Keeping the
    /// three apart is decision I2 of the native-backend program, and it is what stops a forgotten registration
    /// from reading as an incapable machine.
    /// </para>
    /// <para>
    /// A CUSTOM HOST STILL CALLS <c>Register()</c> ITSELF. Nothing here is required: a host that wants exactly
    /// one backend, or a backend other than this platform's, references that package and calls its own
    /// <c>KhaozEngine&lt;Backend&gt;.Register()</c>. Registration is idempotent and last-writer-wins per kind,
    /// so the two mix freely and in any order.
    /// </para>
    /// </summary>
    public static class GpuBackends
    {
        /// <summary>
        /// Register the native backend for the running operating system: Metal on macOS, Direct3D 11 on
        /// Windows, Vulkan on Linux and everything else. Returns the kind that was registered, or null on an
        /// operating system none of the three supports.
        /// <para>
        /// Idempotent and thread-safe, and <c>AppWindow</c> calls it at boot, so an ordinary windowed game
        /// needs no startup line of its own. A headless host (a snapshot or bake tool, a server-side renderer)
        /// calls it once itself, because nothing else in a headless process will.
        /// </para>
        /// <para>
        /// It registers ONE provider rather than all three, deliberately. Loading the other two is harmless
        /// (each package is platform-guarded and its interop sits behind a <c>NoInlining</c> body the JIT never
        /// compiles off its platform), but registering them is not: a machine with a registered provider
        /// answers <see cref="GpuBackendSelector.IsBackendSupported"/> through that provider's own functional
        /// probe, which is what a settings screen offers the player, and offering a Direct3D 11 row on a Mac is
        /// a choice that cannot boot. Use <see cref="RegisterAll"/> when a process really does mean to reach
        /// every backend it can, which in practice is a test harness.
        /// </para>
        /// </summary>
        public static GpuBackendKind? RegisterPlatformDefault()
        {
            if (OperatingSystem.IsMacOS())
            {
                Gpu.Metal.KhaozEngineMetal.Register();
                return GpuBackendKind.MetalNative;
            }

            if (OperatingSystem.IsWindows())
            {
                Gpu.D3D11.KhaozEngineD3D11.Register();
                return GpuBackendKind.Direct3D11Native;
            }

            // Vulkan is the catch-all rather than a Linux special case, matching GpuBackendSelector.ProbeOS: an
            // operating system the probe does not recognize resolves to VulkanNative, so the registration that
            // answers for it has to be reachable on the same arm.
            Gpu.Vulkan.KhaozEngineVulkan.Register();
            return GpuBackendKind.VulkanNative;
        }

        /// <summary>
        /// Register all three native backends, whatever the operating system. Safe everywhere: each package
        /// carries its own platform guard, so a foreign one registers a provider whose functional probe answers
        /// false and whose creation refuses, without loading a single foreign interop symbol.
        /// <para>
        /// For a process that means to reach every backend it can, which is a test harness or a tool comparing
        /// two implementations in one process. A GAME wants <see cref="RegisterPlatformDefault"/>, because
        /// <see cref="GpuBackendSelector.SupportedBackends"/> offers the player every registered kind that
        /// probes true, and a foreign kind that somehow probed true would be a row that cannot boot.
        /// </para>
        /// </summary>
        public static void RegisterAll()
        {
            Gpu.Metal.KhaozEngineMetal.Register();
            Gpu.D3D11.KhaozEngineD3D11.Register();
            Gpu.Vulkan.KhaozEngineVulkan.Register();
        }

        /// <summary>
        /// <see cref="RegisterPlatformDefault"/> unless a provider is already registered for the backend
        /// <see cref="GpuBackendSelector"/> would resolve to, in which case nothing happens and the answer is
        /// null. This is what <c>AppWindow</c> calls at boot.
        /// <para>
        /// The guard is what keeps an explicit host registration authoritative. A host that registered its own
        /// provider for this platform's kind (a wrapper that counts allocations, a fake in a test) would
        /// otherwise have it replaced by the stock one at window creation, silently, because registration is
        /// last-writer-wins and <c>AppWindow</c> writes late.
        /// </para>
        /// </summary>
        public static GpuBackendKind? RegisterPlatformDefaultIfUnregistered()
        {
            GpuBackendKind resolved = GpuBackendSelector.Select();
            return GpuBackendProviders.IsRegistered(resolved) ? null : RegisterPlatformDefault();
        }
    }
}
