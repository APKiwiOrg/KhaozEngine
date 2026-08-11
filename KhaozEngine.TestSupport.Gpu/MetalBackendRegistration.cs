using System;
using KhaozEngine.Gpu.Metal;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Registers the real native Metal backend for the whole test process, once, on demand. The third sibling of
    /// <see cref="D3D11BackendRegistration"/> and <see cref="VulkanBackendRegistration"/>, in the SAME project
    /// and for the same reason.
    /// <para>
    /// It lives in the shared support project rather than in one test assembly because the gate that decides
    /// whether a GPU test runs is not per-assembly. Every assembly with a <c>[GpuFact]</c> in it references this
    /// project by definition (that is where the attribute lives), so putting the registration here means no test
    /// project can be wired for GPU tests and still be missing the backend. The regression evidence is the
    /// Direct3D 11 one and it is not re-earned a third time: registration lived in
    /// <c>KhaozEngine.Render.Tests</c> first, and all four of <c>KhaozEngine.MapEditor.Tests</c>' GPU tests threw
    /// <c>GpuBackendProviderMissingException</c> on the native leg because that project takes <c>[GpuFact]</c>
    /// from here and never had a registration line of its own. Work-breakdown row 2 of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c> names this seat explicitly, in this project
    /// and not in a single test assembly, in the same words phase 3's row 2 did.
    /// </para>
    /// <para>
    /// WHAT PULLS THE TRIGGER is <see cref="GpuFactAttribute"/>'s static constructor, which now calls all three
    /// registrations. The full reasoning for that hook, and why it is not a <c>[ModuleInitializer]</c> in a
    /// library, is on <see cref="D3D11BackendRegistration"/> and is not repeated here.
    /// </para>
    /// <para>
    /// SAFE ON EVERY OPERATING SYSTEM, and unlike the Vulkan sibling that needs saying, because this package
    /// carries a platform guard. Registering is a fact about the process wiring rather than about the machine
    /// (decision M-I4), so this line runs on the Linux and Windows legs too. It loads no Objective-C: the
    /// provider's every entry point checks <c>KhaozEngineMetal.IsPlatformSupported</c> before any body that
    /// names a Metal selector, and those bodies are <c>NoInlining</c> so the JIT never compiles one off macOS.
    /// </para>
    /// <para>
    /// REGISTERING IS ALL THIS DOES, and what it registers can probe and create a device on either path. The
    /// provider answers a real functional probe (the device <c>KE_METAL_DEVICE</c> names, its name, its
    /// <c>supportsFamily:</c> floor, its buffer-offset alignment and its sample-count answer) and hands back a
    /// device holding a real <c>MTLDevice</c> and one <c>MTLCommandQueue</c>, with the WINDOWED path building a
    /// real swapchain since row 15 (https://github.com/APKiwiOrg/KhaozEngine/issues/581). The seat was taken two
    /// rows before the device existed, deliberately, and that paid out exactly as intended: the row that built
    /// the device did not also have to discover where the registration goes. What it registers under is <c>KhaozEngineMetal.MetalNativeKind</c>, which was a pinned
    /// ordinal until row 3 (https://github.com/APKiwiOrg/KhaozEngine/issues/569) landed and is the named
    /// <c>GpuBackendKind.MetalNative</c> now, the same shape the Vulkan seat went through.
    /// </para>
    /// <para>
    /// It is process-wide state, so a test that needs the native kind UNREGISTERED says so explicitly with
    /// <c>BackendProviderScope(kind, provider: null)</c> and belongs in the non-parallel
    /// <c>GraphicsBackendGlobalState</c> collection, exactly as the Direct3D 11 rows do.
    /// </para>
    /// </summary>
    public static class MetalBackendRegistration
    {
        // A Lazy rather than a bare flag, for the reason spelled out on the Direct3D 11 sibling: this has to be
        // safe to call from several threads AND has to have FINISHED registering by the time any of them returns,
        // which an Interlocked one-shot gives only the first half of.
        static readonly Lazy<bool> Registration = new(() =>
        {
            KhaozEngineMetal.Register();
            return true;
        });

        /// <summary>
        /// Register the native Metal backend if this process has not already, and return once it is registered.
        /// Idempotent and thread-safe, so every caller that wants the guarantee can simply ask for it rather than
        /// reasoning about who asked first.
        /// </summary>
        public static void EnsureRegistered() => _ = Registration.Value;
    }
}
