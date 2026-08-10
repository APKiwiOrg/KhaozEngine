using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// M-N4's four reads, taken off ONE <c>MTLDevice</c> and copied out as a <see cref="MetalDeviceFacts"/>
    /// snapshot. Separated from <see cref="MetalSupportProbe"/> because it now has two callers that must not
    /// disagree: the probe, which asks about the device the backend WOULD use, and
    /// <see cref="MetalDeviceEnumeration"/>, which asks the same question of every device on the machine so
    /// <c>KE_METAL_DEVICE</c> can never pin an ineligible one.
    /// <para>
    /// SHARING RATHER THAN COPYING IS THE POINT, and the design predicted this collision by name: row 2's probe
    /// reads the system default, row 4 adds selection, and "whichever lands second shares rather than copies".
    /// Two readings of the same device that could drift would make the probe's yes and the creation path's yes
    /// different answers, which is exactly the split decision M-I4 spends its effort keeping OUT of the refusal
    /// messages.
    /// </para>
    /// <para>
    /// IT READS AND NEVER DECIDES. The decision lives in <see cref="MetalDeviceRequirements"/> over the snapshot
    /// and runs device-free on every leg, which is the split phase 3 established and this package inherited at
    /// row 2.
    /// </para>
    /// </summary>
    internal static class MetalDeviceFactsReader
    {
        /// <summary>
        /// The four reads off <paramref name="device"/>, plus the two diagnostics that make a refusal readable.
        /// The device is neither retained nor released here: the caller owns it, and this borrows it for the
        /// length of the read.
        /// <para>
        /// The caller must already be inside an <see cref="ObjCAutoreleasePool"/> scope, because <c>-name</c>
        /// returns an autoreleased <c>NSString</c>. Every caller is, and the architecture test is what keeps that
        /// true rather than this sentence.
        /// </para>
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static MetalDeviceFacts Read(MTLDevice device)
        {
            if (device.IsNull) return new MetalDeviceFacts(false, "", 0, false, false, 0, "(no device)", false);

            string name = device.Name();

            int highestApple = 0;
            for (nint family = (nint)MTLGPUFamily.Apple1; family <= (nint)MTLGPUFamily.Apple9; family++)
            {
                if (device.SupportsFamily((MTLGPUFamily)family))
                    highestApple = (int)(family - (nint)MTLGPUFamily.Apple1) + 1;
            }

            bool mac2 = device.SupportsFamily(MTLGPUFamily.Mac2);
            bool common1 = device.SupportsFamily(MTLGPUFamily.Common1);

            (nuint alignment, string alignmentSource) = ReadBufferOffsetAlignment(device);

            bool sampleCount1 = device.SupportsTextureSampleCount(1);

            return new MetalDeviceFacts(true, name, highestApple, mac2, common1, alignment, alignmentSource,
                sampleCount1);
        }

        /// <summary>
        /// The device's minimum buffer-offset alignment, with the selector that produced it.
        /// <para>
        /// M-N4 CALLS THIS "the device's minimum constant-buffer offset alignment", AND METAL EXPOSES NO SUCH
        /// PROPERTY. Measured on an Apple M2 Max under macOS 26.6: <c>MTLDevice</c> does not respond to
        /// <c>minimumConstantBufferOffsetAlignment</c> or to <c>minimumBufferOffsetAlignment</c>, and it does
        /// respond to <c>minimumLinearTextureAlignmentForPixelFormat:</c>. Metal's constant-buffer offset rule is
        /// a feature-table fact rather than a runtime query, which is why the incumbent hardcodes it
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
        internal static (nuint Alignment, string Source) ReadBufferOffsetAlignment(MTLDevice device)
        {
            const string constantBuffer = "minimumConstantBufferOffsetAlignment";
            if (device.RespondsTo(constantBuffer))
                return (device.UIntProperty(constantBuffer), "-" + constantBuffer);

            const string linearTexture = "minimumLinearTextureAlignmentForPixelFormat:";
            if (device.RespondsTo(linearTexture))
            {
                return (device.MinimumLinearTextureAlignment(MTLPixelFormat.BGRA8Unorm),
                    "-minimumLinearTextureAlignmentForPixelFormat: (BGRA8Unorm), because Metal exposes no "
                    + "constant-buffer-specific query");
            }

            return (0, "no buffer-offset alignment selector this device answers");
        }
    }
}
