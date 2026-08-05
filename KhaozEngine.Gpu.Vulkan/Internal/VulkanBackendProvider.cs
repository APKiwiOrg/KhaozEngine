using System;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// The engine's native Vulkan backend as the GPU seam sees it. Registered by
    /// <see cref="KhaozEngineVulkan.Register"/> and consumed only through <see cref="IGpuBackendProvider"/>, so
    /// nothing outside this package ever names a Silk.NET type.
    /// <para>
    /// THE PROBE IS REAL AND CREATION IS NOT, which is exactly the state work-breakdown row 2 of
    /// <c>docs/design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md</c> leaves the package in.
    /// <see cref="IsSupported"/> resolves a loader, creates a throwaway instance and reads every physical device
    /// against section 5.2's requirements. Both creation entry points throw until row 4
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/514) builds the refcounted instance and the device.
    /// </para>
    /// <para>
    /// That ordering is deliberate rather than an artefact. The probe answers a question about the MACHINE, which
    /// is what a settings screen and the fallback path consume, and it is the row that makes a silent fallback
    /// impossible: without it a soak session could measure the incumbent Veldrid Vulkan backend and file the
    /// numbers under the native one. Whether this package can build a device is a different fact, answered by
    /// whether the row that builds it has landed, and folding the two together would make the probe answer false
    /// for a reason that has nothing to do with the hardware.
    /// </para>
    /// <para>
    /// No platform guard, anywhere, and that is decision V-P1 rather than an omission. Vulkan is not a Windows
    /// API: the same managed code runs on Windows and Linux, the loader is resolved at runtime, and a machine
    /// without one answers <see cref="IsSupported"/> with a named reason. The
    /// <c>[SupportedOSPlatformGuard]</c>-over-<c>NoInlining</c> apparatus <c>D3D11BackendProvider</c> opens with
    /// has no analogue here and must not be added back by analogy.
    /// </para>
    /// </summary>
    internal sealed class VulkanBackendProvider : IGpuBackendProvider
    {
        static readonly ILogger log = Log.For<VulkanBackendProvider>();

        /// <inheritdoc/>
        public bool IsSupported()
        {
            try
            {
                string? missing = VulkanSupportProbe.MissingRequirement();
                if (missing is null) return true;

                log.Info($"The native Vulkan backend is not available on this machine: {missing}.");
                return false;
            }
            catch (Exception ex)
            {
                // Deliberately broad, and the contract requires it: this probe must NEVER throw, because a probe
                // that blows up and a probe that answers no are the same answer to the settings screen and to the
                // fallback that consume it. Everything under it is a loader resolving native entry points, so the
                // failure can be anything from a DllNotFoundException to an EntryPointNotFoundException out of a
                // driver that exports less than its version claims.
                log.Info("The native Vulkan support probe could not answer, so this machine is reported as "
                    + $"unsupported. It threw {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <inheritdoc/>
        public GpuProviderDevice CreateForWindow(in GpuWindowedDeviceRequest request)
            => throw NotBuiltYet("windowed");

        /// <inheritdoc/>
        public GpuProviderDevice CreateHeadless() => throw NotBuiltYet("headless");

        // The row-2 refusal. It says which row builds the device, because the creation path CATCHES this, WARNs
        // with the message and falls back to the incumbent, so this text is what a tester who named the native
        // backend actually reads. It must not read as a machine problem: an incapable machine is answered by
        // IsSupported above, with its own sentence, and decision V-I4 exists to keep the two tellable apart.
        static NotSupportedException NotBuiltYet(string path)
            => new($"The native Vulkan backend cannot create a {path} device yet. This package currently carries "
                + "its registration and its machine-capability probe, and the instance, the device and the queue "
                + "land in https://github.com/APKiwiOrg/KhaozEngine/issues/514. This is a statement about the "
                + "package, not about this machine: read GpuBackendSelector.IsBackendSupported for that. Until "
                + "then the Vulkan path through Veldrid is the working one.");
    }
}
