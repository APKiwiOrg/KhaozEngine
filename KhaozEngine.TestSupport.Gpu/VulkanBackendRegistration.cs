using System;
using KhaozEngine.Gpu.Vulkan;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Registers the real native Vulkan backend for the whole test process, once, on demand. The sibling of
    /// <see cref="D3D11BackendRegistration"/>, in the SAME project and for the same reason.
    /// <para>
    /// It lives in the shared support project rather than in one test assembly because the gate that decides
    /// whether a GPU test runs is not per-assembly. Every assembly with a <c>[GpuFact]</c> in it references this
    /// project by definition (that is where the attribute lives), so putting the registration here means no test
    /// project can be wired for GPU tests and still be missing the backend. The regression evidence is the
    /// Direct3D 11 one and it is not re-earned here: registration lived in <c>KhaozEngine.Render.Tests</c> first,
    /// and all four of <c>KhaozEngine.MapEditor.Tests</c>' GPU tests threw
    /// <c>GpuBackendProviderMissingException</c> on the native leg because that project takes <c>[GpuFact]</c>
    /// from here and never had a registration line of its own. Work-breakdown row 2 of
    /// <c>docs/design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md</c> names this seat explicitly, in this project
    /// and not in a single test assembly, so the second backend package does not have to pay for that lesson a
    /// second time.
    /// </para>
    /// <para>
    /// WHAT PULLS THE TRIGGER is <see cref="GpuFactAttribute"/>'s static constructor, which calls both
    /// registrations. The full reasoning for that hook, and why it is not a <c>[ModuleInitializer]</c> in a
    /// library, is on <see cref="D3D11BackendRegistration"/> and is not repeated here.
    /// </para>
    /// <para>
    /// REGISTERING IS ALL THIS DOES, and on this backend that is currently more than it sounds and less than it
    /// looks. The provider it registers answers a real functional probe (a Vulkan loader, a throwaway instance at
    /// the 1.3 floor, every physical device read against the design's requirements) and refuses device creation
    /// with a message naming the row that builds it. So no GPU test runs on this backend yet, and the seat exists
    /// now anyway: the row that builds the device is the row that must NOT also have to discover where the
    /// registration goes. What it registers under is <c>GpuBackendKind.VulkanNative</c>, which arrived a row
    /// later than this seat did and replaced the pinned ordinal the seat was first written against.
    /// </para>
    /// <para>
    /// It is process-wide state, so a test that needs the native kind UNREGISTERED says so explicitly with
    /// <c>BackendProviderScope(kind, provider: null)</c> and belongs in the non-parallel
    /// <c>GraphicsBackendGlobalState</c> collection, exactly as the Direct3D 11 rows do.
    /// </para>
    /// </summary>
    public static class VulkanBackendRegistration
    {
        // A Lazy rather than a bare flag, for the reason spelled out on the Direct3D 11 sibling: this has to be
        // safe to call from several threads AND has to have FINISHED registering by the time any of them returns,
        // which an Interlocked one-shot gives only the first half of.
        static readonly Lazy<bool> Registration = new(() =>
        {
            KhaozEngineVulkan.Register();
            return true;
        });

        /// <summary>
        /// Register the native Vulkan backend if this process has not already, and return once it is registered.
        /// Idempotent and thread-safe, so every caller that wants the guarantee can simply ask for it rather than
        /// reasoning about who asked first.
        /// </summary>
        public static void EnsureRegistered() => _ = Registration.Value;
    }
}
