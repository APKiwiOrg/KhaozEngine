using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// The READ half of the machine probe (M-N4): create the system default <c>MTLDevice</c>, take the four
    /// answers section 4.1 names, release it, and hand the snapshot to
    /// <see cref="MetalDeviceRequirements.MissingRequirement"/>. It answers a question about the MACHINE, which
    /// is what a settings screen and the fallback path consume, and it deliberately does not answer whether this
    /// PACKAGE can build a device yet. Folding the two together would make the probe report false for a reason
    /// with nothing to do with the hardware.
    /// <para>
    /// IT IS A FUNCTIONAL PROBE RATHER THAN AN OS TEST, which is the whole of M-N4. The incumbent's
    /// <c>MTLGraphicsDevice.GetIsSupported</c> checks the platform and then creates a device inside a bare catch,
    /// and that is the FLOOR of this rather than the whole of it: a Mac that creates a device and then answers
    /// below the family floor, or reports a buffer-offset alignment the uniform ring's stride is not a multiple
    /// of, is a machine this backend cannot run, and finding that out here is what routes it through the reported
    /// fallback instead of a crash on frame one.
    /// </para>
    /// <para>
    /// IT READS THE SYSTEM DEFAULT DEVICE, AND ROW 4 WILL MAKE THAT INCOMPLETE. <c>KE_METAL_DEVICE</c> selection
    /// over <c>MTLCopyAllDevices()</c> is decision M-N1 and lands with the device and the queue
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/570). On a dual-GPU Mac with that variable set, this
    /// probe currently answers about a device the backend would not use. That is recorded rather than worked
    /// around, because implementing the selection here would be building row 4's parsing, its substitution
    /// logging and its fallback twice: when row 4 lands, the probe reads the SELECTED device, and the phase-3
    /// precedent for this exact collision (row 2's throwaway probe against row 4's real instance) says whichever
    /// lands second shares rather than copies.
    /// </para>
    /// </summary>
    internal static class MetalSupportProbe
    {
        // MTLGPUFamily, by number. The enum itself is row 4's to declare (Internal/ObjC/), and declaring half of
        // one here would be the start of a second copy, which is the same call row 1's spike made.
        const nint GpuFamilyApple1 = 1001;
        const nint GpuFamilyApple9 = 1009;
        const nint GpuFamilyMac2 = 2002;
        const nint GpuFamilyCommon1 = 3001;

        // MTLPixelFormatBGRA8Unorm, the format the swapchain and every golden readback use, so the alignment
        // question is asked about the format the engine actually binds buffers around rather than an exotic one.
        const nuint PixelFormatBgra8Unorm = 80;

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
        /// The four reads, taken off the system default device and copied out before it is released. Separate
        /// from the decision so the decision can be driven device-free, and separate from
        /// <see cref="MissingRequirement"/> so a test can print what this machine actually answered rather than
        /// only whether it passed.
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static MetalDeviceFacts ReadFacts()
        {
            // M-N5's rule, and it applies to a probe as much as to a frame: -name returns an autoreleased
            // NSString, so the body sits inside a pool rather than leaving it to whichever thread pool thread
            // xUnit or a settings screen happened to call on.
            IntPtr pool = MetalSupportProbeNative.AutoreleasePoolPush();
            try
            {
                return ReadFactsInsidePool();
            }
            finally
            {
                MetalSupportProbeNative.AutoreleasePoolPop(pool);
            }
        }

        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static MetalDeviceFacts ReadFactsInsidePool()
        {
            IntPtr device = MetalSupportProbeNative.MTLCreateSystemDefaultDevice();
            if (device == IntPtr.Zero)
            {
                return new MetalDeviceFacts(false, "", 0, false, false, 0, "(no device)", false);
            }

            try
            {
                string name = MetalSupportProbeNative.NSStringToManaged(
                    MetalSupportProbeNative.MsgSend(device, MetalSupportProbeNative.Sel("name")));

                IntPtr supportsFamily = MetalSupportProbeNative.Sel("supportsFamily:");
                int highestApple = 0;
                for (nint family = GpuFamilyApple1; family <= GpuFamilyApple9; family++)
                {
                    if (MetalSupportProbeNative.MsgSendBoolNInt(device, supportsFamily, family) != 0)
                        highestApple = (int)(family - GpuFamilyApple1) + 1;
                }

                bool mac2 = MetalSupportProbeNative.MsgSendBoolNInt(device, supportsFamily, GpuFamilyMac2) != 0;
                bool common1 = MetalSupportProbeNative.MsgSendBoolNInt(device, supportsFamily, GpuFamilyCommon1) != 0;

                (nuint alignment, string alignmentSource) = ReadBufferOffsetAlignment(device);

                bool sampleCount1 = MetalSupportProbeNative.MsgSendBoolNUInt(
                    device, MetalSupportProbeNative.Sel("supportsTextureSampleCount:"), 1) != 0;

                return new MetalDeviceFacts(true, name, highestApple, mac2, common1, alignment, alignmentSource,
                    sampleCount1);
            }
            finally
            {
                // MTLCreateSystemDefaultDevice hands back a +1 device, so the probe owns this one and releases
                // it. Everything else it touched is autoreleased and dies with the pool above.
                MetalSupportProbeNative.ObjcRelease(device);
            }
        }

        /// <summary>
        /// The device's minimum buffer-offset alignment, with the selector that produced it.
        /// <para>
        /// M-N4 CALLS THIS "the device's minimum constant-buffer offset alignment", AND METAL EXPOSES NO SUCH
        /// PROPERTY. Measured on an Apple M2 Max under macOS 26.6: <c>MTLDevice</c> does not respond to
        /// <c>minimumConstantBufferOffsetAlignment</c> or to <c>minimumBufferOffsetAlignment</c>, and it does
        /// respond to <c>minimumLinearTextureAlignmentForPixelFormat:</c> and
        /// <c>minimumTextureBufferAlignmentForPixelFormat:</c>. Metal's constant-buffer offset rule is a feature
        /// table fact rather than a runtime query, which is why the incumbent hardcodes it
        /// (<c>MetalFeatures.IsMacOS ? 16u : 256u</c>) rather than asking.
        /// </para>
        /// <para>
        /// So the read is the closest question the API actually answers: the minimum alignment the DEVICE
        /// requires for a buffer offset, asked for the pixel format the swapchain and every golden readback use.
        /// It came back 16 on that machine, which is exactly what the incumbent hardcodes for macOS, so the two
        /// independent statements of the number agree. The check over it (M-M3's 256 stride is a multiple of it)
        /// is the tripwire M-N4 wanted, and it fires in the conservative direction on a device with coarser
        /// buffer granularity than the ring assumes.
        /// </para>
        /// <para>
        /// The real property is still ASKED FOR FIRST through <c>respondsToSelector:</c>, so the day a macOS
        /// version ships a constant-buffer query this reads it and the proxy stops being used, with no code
        /// change and no reader left believing the proxy was the constant-buffer number.
        /// </para>
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static (nuint Alignment, string Source) ReadBufferOffsetAlignment(IntPtr device)
        {
            IntPtr responds = MetalSupportProbeNative.Sel("respondsToSelector:");

            IntPtr constantBuffer = MetalSupportProbeNative.Sel("minimumConstantBufferOffsetAlignment");
            if (MetalSupportProbeNative.MsgSendBoolPtr(device, responds, constantBuffer) != 0)
            {
                return (MetalSupportProbeNative.MsgSendNUInt(device, constantBuffer),
                    "-minimumConstantBufferOffsetAlignment");
            }

            IntPtr linearTexture = MetalSupportProbeNative.Sel("minimumLinearTextureAlignmentForPixelFormat:");
            if (MetalSupportProbeNative.MsgSendBoolPtr(device, responds, linearTexture) != 0)
            {
                return (MetalSupportProbeNative.MsgSendNUIntNUInt(device, linearTexture, PixelFormatBgra8Unorm),
                    "-minimumLinearTextureAlignmentForPixelFormat: (BGRA8Unorm), because Metal exposes no "
                    + "constant-buffer-specific query");
            }

            return (0, "no buffer-offset alignment selector this device answers");
        }
    }
}
