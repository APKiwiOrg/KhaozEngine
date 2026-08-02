using System;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.D3D11.Internal;

namespace KhaozEngine.Gpu.D3D11
{
    /// <summary>
    /// The entire public surface of the native Direct3D 11 backend: one call that registers it with
    /// <see cref="GpuBackendProviders"/>, plus the platform guard a caller can read before naming the backend.
    /// A consumer opts in with a package reference and <c>KhaozEngineD3D11.Register()</c> at startup, and
    /// <see cref="GpuBackendKind.Direct3D11Native"/> then becomes creatable through the ordinary
    /// <see cref="GpuDeviceContext"/> entry points.
    /// <para>
    /// Registration is EXPLICIT, per decision P4 and section 4.1 of
    /// <c>docs/design/D3D11-NATIVE-BACKEND-DESIGN-2026-08-02.md</c>. A <c>[ModuleInitializer]</c> in this package
    /// was rejected: the CLR loads an assembly lazily on first type reference, so a package reference with no
    /// static type use does not guarantee the initializer ever runs, and that failure is silent and
    /// machine-dependent, which is the worst possible shape for a rollout whose purpose is attributing field
    /// measurements to a backend. Reflection probing by assembly name was rejected too, being trim and AOT
    /// hostile, invisible to the architecture tests, and a way of turning a missing reference into a runtime
    /// string mismatch.
    /// </para>
    /// <para>
    /// This type is safe to touch on ANY operating system. It names no Direct3D type, and every body that does is
    /// behind <see cref="IsPlatformSupported"/>, so referencing this package on macOS or Linux does not put the
    /// Vortice interop on the load path.
    /// </para>
    /// </summary>
    public static class KhaozEngineD3D11
    {
        // One provider instance for the process. It holds no device and no state of its own: the machine
        // capability answer it produces is cached by GpuBackendSelector, not here.
        static readonly D3D11BackendProvider _provider = new();

        /// <summary>
        /// Whether this operating system can run the native Direct3D 11 backend at all. False everywhere except
        /// Windows, and false is not a fault: it is the same "nothing to ask" answer the driver-threading probe
        /// gives off Windows.
        /// <para>
        /// This is the platform GUARD the whole package hangs off. Marked
        /// <see cref="SupportedOSPlatformGuardAttribute"/> so the platform-compatibility analyzer treats a false
        /// return as ruling Windows out, which is what lets the Direct3D bodies keep their
        /// <c>[SupportedOSPlatform("windows")]</c> contract with ONE check rather than a drift-prone copy at every
        /// call site. With warnings as errors, CA1416 then enforces the boundary at compile time.
        /// </para>
        /// </summary>
        [SupportedOSPlatformGuard("windows")]
        public static bool IsPlatformSupported => OperatingSystem.IsWindows();

        /// <summary>
        /// Register this backend so <see cref="GpuBackendKind.Direct3D11Native"/> can be created. Call once at
        /// consumer startup, before any device is created. Idempotent (a repeated call replaces the same
        /// provider) and thread-safe.
        /// <para>
        /// Safe to call on every operating system, and meant to be called unconditionally: registering says a
        /// provider EXISTS, which is a fact about the app's wiring, while whether the machine can run it is a
        /// separate question answered by <see cref="GpuBackendSelector.IsBackendSupported"/> through the
        /// provider's own functional probe. Decision I2 keeps those two apart on purpose, because a forgotten
        /// registration that read as an incapable machine would fall back silently and file a soak session's
        /// numbers under the wrong backend.
        /// </para>
        /// </summary>
        public static void Register() => GpuBackendProviders.Register(GpuBackendKind.Direct3D11Native, _provider);
    }
}
