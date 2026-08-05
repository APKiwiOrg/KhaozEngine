namespace KhaozEngine.Gpu.Vulkan
{
    /// <summary>
    /// The public surface of the engine-owned native Vulkan backend, and for now the whole of it.
    /// <para>
    /// This package is a SKELETON. Row 1 of the work breakdown in
    /// <c>docs/design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md</c> creates the assembly, its guard rows and
    /// the binding-sufficiency spike, and nothing else. There is deliberately no <c>Register()</c> here yet:
    /// registration, the <c>IGpuBackendProvider</c> and the functional probe are row 2, and a registration call
    /// that existed before the provider it registers would be a public method that lies about what the package
    /// can do. Until then no consumer can reach a Vulkan device through this package, and
    /// <see cref="GpuBackendKind"/> has no native Vulkan member at all (that append is row 3).
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
    }
}
