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
        /// Windows, Vulkan on Linux and everything else. Returns the kind it registered, and never null:
        /// <see cref="GpuBackendSelector.ProbeOS"/> has a Vulkan CATCH-ALL arm rather than a Linux arm, so an
        /// operating system none of the three recognizes still resolves to (and registers) Vulkan. The nullable
        /// return is kept because it is the same shape
        /// <see cref="RegisterResolvedIfUnregistered(GpuBackendKind?)"/> answers with, where null is a real
        /// answer, and collapsing one of the pair to a plain <see cref="GpuBackendKind"/> would leave two
        /// registration entry points a caller has to read differently.
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
        /// Register what this process actually needs to boot, without disturbing anything a host registered
        /// itself: a provider for the backend <see cref="GpuBackendSelector"/> RESOLVES to with
        /// <paramref name="userPreference"/> in the chain, and a provider for this platform's own kind, which is
        /// where a failed request falls back to. Returns the resolved kind when this call registered a provider
        /// for it, otherwise the platform kind when it registered that one, and null when both were already
        /// registered. This is what <c>AppWindow</c> calls at boot.
        /// <para>
        /// IT REGISTERS FOR THE RESOLVED KIND, not for the platform's, and that is the whole point of the
        /// member. A stored preference and <c>KE_GRAPHICS_BACKEND</c> both outrank the OS probe, so the kind a
        /// game is about to ask for is routinely NOT this platform's default: a Windows player who chose
        /// <see cref="GpuBackendKind.VulkanNative"/> from a settings screen, or a developer who pinned
        /// <c>vulkan-native</c> on a Mac. Registering only the platform's own left both of those asking for a
        /// backend whose package the umbrella already ships, and being told to add a package reference.
        /// </para>
        /// <para>
        /// AND IT REGISTERS FOR THE PLATFORM'S KIND AS WELL, always, because that is where the fallback lands. A
        /// resolved kind that fails on this machine is only survivable if the thing it falls back to has a
        /// provider, and a boot that registered the requested backend alone would turn one refusal into two.
        /// </para>
        /// <para>
        /// A PACKAGE THAT CANNOT RUN ON THIS OS IS NOT REGISTERED (see <see cref="CanRegisterHere"/>), so asking
        /// for <c>d3d11-native</c> on a Mac leaves the Direct3D 11 kind unregistered rather than seating a
        /// provider that answers no to everything. What that request gets is the ordinary missing-provider
        /// throw, which is the honest answer: there is no Direct3D 11 on this machine to refuse it.
        /// </para>
        /// <para>
        /// The per-kind guard is what keeps an explicit host registration authoritative. A host that registered
        /// its own provider (a wrapper that counts allocations, a fake in a test) would otherwise have it
        /// replaced by the stock one at window creation, silently, because registration is last-writer-wins and
        /// <c>AppWindow</c> writes late.
        /// </para>
        /// </summary>
        public static GpuBackendKind? RegisterResolvedIfUnregistered(GpuBackendKind? userPreference = null)
        {
            GpuBackendKind platform = GpuBackendSelector.ProbeOS(GpuBackendSelector.DetectOS());
            GpuBackendKind resolved = GpuBackendSelector.Resolve(userPreference).Backend;

            GpuBackendKind? registered = RegisterUnlessTaken(platform);
            if (resolved != platform && RegisterUnlessTaken(resolved) is GpuBackendKind own) registered = own;
            return registered;
        }

        /// <summary>
        /// Whether this package can register a provider for <paramref name="backend"/> on the operating system
        /// it is running on. True for <see cref="GpuBackendKind.VulkanNative"/> everywhere, since Vulkan is not
        /// an OS-specific API and its package carries no platform guard, and for the OS-specific pair only on
        /// their own platform. False for everything else, including the four kinds retired in 18.0.0.
        /// <para>
        /// It reads each package's own <c>IsPlatformSupported</c> rather than restating the mapping, so there is
        /// one statement per package of which OS it belongs to and this cannot drift from it.
        /// </para>
        /// </summary>
        internal static bool CanRegisterHere(GpuBackendKind backend) => backend switch
        {
            GpuBackendKind.MetalNative => Gpu.Metal.KhaozEngineMetal.IsPlatformSupported,
            GpuBackendKind.Direct3D11Native => Gpu.D3D11.KhaozEngineD3D11.IsPlatformSupported,
            GpuBackendKind.VulkanNative => true,
            _ => false,
        };

        // Registers the stock provider for one kind, unless something is already registered for it or this
        // operating system cannot run it. Returns the kind when it registered, null when it did not, so the
        // caller can report which of its two candidates it actually seated.
        static GpuBackendKind? RegisterUnlessTaken(GpuBackendKind backend)
        {
            if (GpuBackendProviders.IsRegistered(backend) || !CanRegisterHere(backend)) return null;

            switch (backend)
            {
                case GpuBackendKind.MetalNative: Gpu.Metal.KhaozEngineMetal.Register(); return backend;
                case GpuBackendKind.Direct3D11Native: Gpu.D3D11.KhaozEngineD3D11.Register(); return backend;
                case GpuBackendKind.VulkanNative: Gpu.Vulkan.KhaozEngineVulkan.Register(); return backend;
                // Unreachable through CanRegisterHere, which answers false for every other kind. Stated rather
                // than left to a discard arm that would silently start registering nothing if a kind is appended
                // to CanRegisterHere and forgotten here.
                default: return null;
            }
        }
    }
}
