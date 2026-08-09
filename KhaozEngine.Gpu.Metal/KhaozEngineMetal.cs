using System;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Metal.Internal;

namespace KhaozEngine.Gpu.Metal
{
    /// <summary>
    /// The public surface of the engine-owned native Metal backend, and for now the whole of it: the platform
    /// guard, and one call that registers the backend with <see cref="GpuBackendProviders"/>.
    /// <para>
    /// WHAT THIS PACKAGE CAN DO TODAY. Registration and the machine-capability probe are real: registering makes
    /// the provider reachable, and <see cref="GpuBackendSelector.IsBackendSupported"/> then answers for this
    /// machine by creating the system default <c>MTLDevice</c> and reading M-N4's four answers off it (a name,
    /// the <c>supportsFamily:</c> floor, a buffer-offset alignment the uniform ring's stride is a multiple of,
    /// and <c>supportsTextureSampleCount:</c> for 1). Creating a DEVICE is not built yet and refuses with a
    /// message naming the row that builds it (https://github.com/APKiwiOrg/KhaozEngine/issues/570). Since row 3
    /// <see cref="GpuBackendKind.MetalNative"/> exists as well, so the kind is nameable ahead of the device
    /// behind it, and selecting it without <c>Register()</c> throws the provider-missing exception rather than
    /// falling back to anything.
    /// Work-breakdown row 2 of <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>.
    /// </para>
    /// <para>
    /// THIS PARAGRAPH IS A LEDGER, AND A STALE ONE IS WORSE THAN NONE, because it is the first thing a consumer
    /// reads about the package. The Vulkan sibling's went nine work-breakdown rows out of date
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/560), still promising refusals that had been live code
    /// for releases. Anything that changes what this package can DO changes this paragraph in the same commit.
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
        /// device the backend can actually use is the separate question the functional probe behind
        /// <see cref="GpuBackendSelector.IsBackendSupported"/> answers, and keeping the two apart is what stops a
        /// missing registration reading as an incapable machine.
        /// </para>
        /// </summary>
        [SupportedOSPlatformGuard("macos")]
        public static bool IsPlatformSupported => OperatingSystem.IsMacOS();

        /// <summary>
        /// The <see cref="GpuBackendKind"/> this backend registers under, PINNED TO ITS ORDINAL because the
        /// named member does not exist yet.
        /// <para>
        /// Row 3 (https://github.com/APKiwiOrg/KhaozEngine/issues/569) appends
        /// <c>GpuBackendKind.MetalNative = 6</c> with that explicit ordinal (decision M-I1), and its issue says
        /// it PARALLELISES with row 2 rather than preceding it, so this row may not depend on it. It may not do
        /// its work either: appending the member without row 3's <c>GoldenBackendToken</c> mapping would leave a
        /// kind that throws out of the golden filename path, which is precisely the silent-orphan failure that
        /// row's audit test exists to prevent, and row 3 additionally has two frame-cap sites and a
        /// frame-capture gate that degrade SILENTLY.
        /// </para>
        /// <para>
        /// So the value is written as the ordinal the design pins, and the registry keys by VALUE, which makes
        /// registration correct TODAY rather than deferred: the provider is really registered, the probe really
        /// answers through <see cref="GpuBackendSelector.IsBackendSupported"/>, and the test-side seat really
        /// registers something. The alternative considered and rejected was a <see cref="Register"/> that throws
        /// until row 3 lands, which would be a public method that lies about what the package does and a
        /// registration seat with nothing to register. This is the phase-3 precedent applied unchanged: its row
        /// 2 registered under <c>(GpuBackendKind)5</c> for exactly these reasons.
        /// </para>
        /// <para>
        /// Until row 3 landed this was a pinned <c>(GpuBackendKind)6</c> cast with a tripwire test forcing its
        /// replacement the moment the member existed. The tripwire fired at the rows' merge and both edits it
        /// prescribed were made: the cast is the named member now, and the constant stays only as the single
        /// spelling the provider and the test seat share.
        /// </para>
        /// </summary>
        internal const GpuBackendKind MetalNativeKind = GpuBackendKind.MetalNative;

        // One provider instance for the process. It holds no device and no state of its own beyond the memoized
        // machine answer, whose lifetime is deliberately this registration's (section 4.1).
        static readonly MetalBackendProvider _provider = new();

        /// <summary>
        /// Register this backend so the native Metal kind can be created. Call once at consumer startup, before
        /// any device is created. Idempotent (a repeated call replaces the same provider) and thread-safe.
        /// <para>
        /// Safe to call on every operating system, and meant to be called unconditionally, which is worth
        /// stating in a package that also ships <see cref="IsPlatformSupported"/>: registering says a provider
        /// EXISTS, which is a fact about the app's wiring, while whether this machine can run it is a separate
        /// question answered by <see cref="GpuBackendSelector.IsBackendSupported"/> through the provider's own
        /// functional probe. Decision M-I4 keeps those two apart on purpose, and here the reason bites as hard
        /// as it does on Vulkan: on macOS the OS probe already returns <see cref="GpuBackendKind.Metal"/>, so a
        /// native request that fails falls back to the incumbent Metal backend and reports
        /// <see cref="GpuBackendSource.FallbackAfterFailure"/>, which in a log line looks a great deal like a
        /// forgotten registration. A forgotten registration THROWS instead, and telling those two apart is what
        /// the whole soak measurement rests on.
        /// </para>
        /// </summary>
        public static void Register() => GpuBackendProviders.Register(MetalNativeKind, _provider);
    }
}
