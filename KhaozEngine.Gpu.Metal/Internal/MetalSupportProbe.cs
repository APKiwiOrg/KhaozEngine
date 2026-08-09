using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// The READ half of the machine probe (M-N4): acquire the device this machine would actually use, take the
    /// four answers section 4.1 names, release it, and hand the snapshot to
    /// <see cref="MetalDeviceRequirements.MissingRequirement"/>. It answers a question about the MACHINE, which
    /// is what a settings screen and the fallback path consume.
    /// <para>
    /// IT IS A FUNCTIONAL PROBE RATHER THAN AN OS TEST, which is the whole of M-N4. The incumbent's
    /// <c>MTLGraphicsDevice.GetIsSupported</c> checks the platform and then creates a device inside a bare catch,
    /// and that is the FLOOR of this rather than the whole of it: a Mac that creates a device and then answers
    /// below the family floor, or reports a buffer-offset alignment the uniform ring's stride is not a multiple
    /// of, is a machine this backend cannot run, and finding that out here is what routes it through the reported
    /// fallback instead of a crash on frame one.
    /// </para>
    /// <para>
    /// IT NOW READS THE SELECTED DEVICE, WHICH IS THE ROW-4 HANDOFF DISCHARGED. Row 2 shipped this reading
    /// <c>MTLCreateSystemDefaultDevice()</c> unconditionally and recorded on
    /// https://github.com/APKiwiOrg/KhaozEngine/issues/570 that <c>KE_METAL_DEVICE</c> would make that
    /// incomplete: on a dual-GPU Mac with the variable set, the probe would answer about a device the backend
    /// does not use, and <c>IsBackendSupported</c> plus the fallback report would both describe the wrong one.
    /// <see cref="MetalDeviceEnumeration.AcquireSelected"/> is the shared acquisition both this and the creation
    /// path go through, so there is one answer asked twice rather than two that can drift.
    /// </para>
    /// <para>
    /// IT DOES NOT LOG THE SELECTION, and creation does. The probe runs from a settings screen and from
    /// <c>GpuBackendSelector.IsBackendSupported</c>, potentially without a device ever being created, so a
    /// selection line here would appear in sessions that never ran this backend at all. The line and the
    /// substitution warning are emitted where they mean something, at creation.
    /// </para>
    /// </summary>
    internal static class MetalSupportProbe
    {
        /// <summary>
        /// What stops THIS MACHINE running the native Metal backend, or null when nothing does. Total on every
        /// operating system: off macOS it answers with the platform rather than throwing, so a caller on Windows
        /// or Linux gets a sentence instead of an exception.
        /// </summary>
        internal static string? MissingRequirement()
        {
            if (!KhaozEngineMetal.IsPlatformSupported)
            {
                return "this operating system has no Metal at all. That is a statement about the PLATFORM rather "
                    + "than about the machine, and it is not a fault: KhaozEngine.Gpu.Metal is safe to reference "
                    + "and safe to register everywhere, and it reports itself unsupported off macOS";
            }

            return MetalDeviceRequirements.MissingRequirement(ReadFacts());
        }

        /// <summary>
        /// The four reads, taken off the SELECTED device and copied out before it is released. Separate from the
        /// decision so the decision can be driven device-free, and separate from <see cref="MissingRequirement"/>
        /// so a test can print what this machine actually answered rather than only whether it passed.
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static MetalDeviceFacts ReadFacts()
        {
            // M-N5's rule, and it applies to a probe as much as to a frame: -name returns an autoreleased
            // NSString, so the body sits inside a pool rather than leaving it to whichever thread pool thread
            // xUnit or a settings screen happened to call on. The architecture test enforces this rather than
            // trusting the comment.
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            MetalSelectedDevice selected = MetalDeviceEnumeration.AcquireSelected();
            try
            {
                return selected.Facts;
            }
            finally
            {
                // The acquisition hands back +1, so the probe owns this one and releases it. On a machine with
                // no eligible device there is nothing to release and the facts describe why.
                selected.Device.Release();
            }
        }
    }
}
