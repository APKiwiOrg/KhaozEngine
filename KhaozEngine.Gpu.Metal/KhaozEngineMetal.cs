using System;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.Metal
{
    /// <summary>
    /// The public surface of the engine-owned native Metal backend, and for now the whole of it.
    /// <para>
    /// This package is a SKELETON. Row 1 of the work breakdown in
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c> creates the assembly, its guard rows and the
    /// three verification spikes, and nothing else. There is deliberately no <c>Register()</c> here yet:
    /// registration, the <c>IGpuBackendProvider</c> and the functional probe are row 2, and a registration call
    /// that existed before the provider it registers would be a public method that lies about what the package
    /// can do. Until then no consumer can reach a Metal device through this package, and
    /// <see cref="GpuBackendKind"/> has no native Metal member at all (that append is row 3).
    /// </para>
    /// <para>
    /// UNLIKE THE VULKAN PACKAGE, THIS ONE CARRIES THE PLATFORM GUARD, and that is decision M-P1 rather than a
    /// copy made by analogy. Vulkan is not an OS-specific API, so <c>KhaozEngine.Gpu.Vulkan</c> needs no
    /// <c>[SupportedOSPlatformGuard]</c> and has none. Metal IS one, so the apparatus
    /// <c>KhaozEngine.Gpu.D3D11</c> carries applies here verbatim: <see cref="IsPlatformSupported"/> is the one
    /// guard the whole package hangs off, every Objective-C-touching body sits behind it inside a
    /// <c>NoInlining</c> method, and CA1416 enforces that boundary at compile time under warnings as errors.
    /// The package still targets <c>net10.0</c> rather than <c>net10.0-macos</c>, which is what lets the
    /// assembly compile and its device-free tests run on the Linux and Windows legs.
    /// </para>
    /// </summary>
    public static class KhaozEngineMetal
    {
        /// <summary>
        /// Whether this operating system can run the native Metal backend at all. False everywhere except macOS,
        /// and false is not a fault: it is the same "nothing to ask" answer the Direct3D 11 package's guard gives
        /// off Windows.
        /// <para>
        /// This is the platform GUARD the whole package hangs off. Marked
        /// <see cref="SupportedOSPlatformGuardAttribute"/> so the platform-compatibility analyzer treats a false
        /// return as ruling macOS out, which is what lets the Objective-C bodies keep their
        /// <c>[SupportedOSPlatform("macos")]</c> contract with ONE check rather than a drift-prone copy at every
        /// call site.
        /// </para>
        /// <para>
        /// It answers a question about the OPERATING SYSTEM and nothing else. Whether this machine has a Metal
        /// device the backend can actually use is the separate question row 2's functional probe answers, and
        /// keeping the two apart is what stops a missing registration reading as an incapable machine.
        /// </para>
        /// </summary>
        [SupportedOSPlatformGuard("macos")]
        public static bool IsPlatformSupported => OperatingSystem.IsMacOS();
    }
}
