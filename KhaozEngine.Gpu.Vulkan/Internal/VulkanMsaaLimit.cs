using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// The complete <see cref="GpuCapabilities.SupportedMsaaSampleCounts"/> set, read from the same per-format
    /// image-property query the incumbent used. <see cref="GpuCapabilities.MaxMsaaSampleCount"/> remains the
    /// highest member for compatibility.
    ///
    /// <para><b>A MAXIMUM CANNOT REPRESENT A SPARSE MASK.</b> Issue #853 measured lavapipe reporting 1x and 4x
    /// for both framebuffer colour and depth, with no 2x. Reducing each format to its highest bit and taking the
    /// minimum reported 4, after which the renderer treated 2 as supported and produced a black frame. The
    /// supported answer is the bitwise intersection across every MRT attachment. The maximum is derived from that
    /// set after the holes have been preserved.</para>
    ///
    /// <para><b>THE QUERY STILL MATCHES THE INCUMBENT.</b> Section 13
    /// says the incumbent's shape was "a per-format <c>vkGetPhysicalDeviceFormatProperties</c> read reduced to the
    /// highest supported bit". It is not: <c>VkGraphicsDevice.GetSampleCountLimit</c> calls
    /// <c>vkGetPhysicalDeviceImageFormatProperties</c> and reduces the <c>sampleCounts</c> field of the
    /// <c>VkImageFormatProperties</c> it returns, which is a different query with a different answer (it takes the
    /// image type, the tiling and the USAGE, and a format's sample counts genuinely differ by usage). Issue #853
    /// keeps that query and changes only the lossy reduction of its masks.</para>
    ///
    /// <para><b>THE THREE FORMATS ARE THE ENGINE'S, NOT THE BACKEND'S.</b>
    /// <c>KhaozEngine.Gpu.Internal.VeldridMap.MaxMsaaSampleCount</c> (deleted in 18.0.0, in git history) folded
    /// over the colour target, the linear-depth target and the depth-stencil target the 3D scene renders into.
    /// Every attachment of an MRT must support the selected count, so their masks are intersected.</para>
    ///
    /// <para><b>AND THE DEPTH FLAG DOES NOT REACH THE FORMAT MAPPING, WHICH LOOKS LIKE A BUG AND IS THE
    /// CONTRACT.</b> <c>GetSampleCountLimit</c> passes its <c>depthFormat</c> argument to the USAGE bits alone and
    /// calls <c>VdToVkPixelFormat(format)</c> with its default, so the linear-depth target is queried as
    /// <c>R32_SFLOAT</c> with a COLOUR attachment usage even though the shadow pass renders depth into it.
    /// Reproducing the answer means reproducing that, and the two combined depth formats carry their own depth
    /// spelling whatever the flag says, so the third query really is
    /// <c>D32_SFLOAT_S8_UINT</c> with a depth-stencil usage.</para>
    ///
    /// <para><b>NOTHING HERE TOUCHES A DEVICE.</b> The query is a delegate, so the fold, the reduction and the
    /// three-format table are a plain <c>[Fact]</c>, and the ONE line that names
    /// <c>vkGetPhysicalDeviceImageFormatProperties</c> lives in <see cref="VulkanPhysicalDeviceReader"/> with
    /// every other physical-device read.</para>
    /// </summary>
    internal static class VulkanMsaaLimit
    {
        /// <summary>
        /// The historical source of the per-format query and maximum reduction. Issue #853 keeps the query and
        /// replaces the maximum-only capability with the mask intersection above.
        /// </summary>
        internal const string Citation =
            "Veldrid 4.9.103 (Vulkan tree v4.9.0): VkGraphicsDevice.GetSampleCountLimit, folded by "
            + "KhaozEngine.Gpu.Internal.VeldridMap.MaxMsaaSampleCount (deleted in 18.0.0) over "
            + "R8_G8_B8_A8_UNorm, R32_Float and D32_Float_S8_UInt. Issue #853 keeps the query and "
            + "intersects its masks.";

        /// <summary>
        /// The three formats the fold covers and whether each is queried with a DEPTH-STENCIL attachment usage
        /// rather than a colour one. In the incumbent's own order, which does not matter to a minimum and does
        /// matter to anybody diffing the two.
        /// </summary>
        internal static IReadOnlyList<(GpuPixelFormat Format, bool DepthAttachment)> Formats { get; } =
        [
            (GpuPixelFormat.R8G8B8A8UNorm, false),
            (GpuPixelFormat.R32Float, false),
            (GpuPixelFormat.D32FloatS8UInt, true),
        ];

        /// <summary>
        /// The highest sample count a <c>VkSampleCountFlags</c> mask supports, as the seam's plain integer. The
        /// incumbent's if-else ladder over 32, 16, 8, 4 and 2 with 1 as the fallback, reproduced: a mask with no
        /// recognised bit answers 1 rather than 0, which is what makes "no MSAA" the safe floor.
        /// </summary>
        internal static int Reduce(SampleCountFlags counts)
        {
            if ((counts & SampleCountFlags.Count32Bit) != 0) return 32;
            if ((counts & SampleCountFlags.Count16Bit) != 0) return 16;
            if ((counts & SampleCountFlags.Count8Bit) != 0) return 8;
            if ((counts & SampleCountFlags.Count4Bit) != 0) return 4;
            if ((counts & SampleCountFlags.Count2Bit) != 0) return 2;
            return 1;
        }

        /// <summary>
        /// The highest member of <see cref="SupportedOverTheEngineTargets"/>.
        /// </summary>
        /// <param name="sampleCounts">The device's <c>sampleCounts</c> for one format and one attachment usage.
        /// Real on a device, a table in the device-free tests.</param>
        internal static int MinOverTheEngineTargets(
            Func<GpuPixelFormat, bool, SampleCountFlags> sampleCounts)
            => GpuSampleCountSet.HighestAtMost(SupportedOverTheEngineTargets(sampleCounts), int.MaxValue);

        /// <summary>The intersection of supported counts across every attachment in the engine's MRT.</summary>
        internal static GpuSampleCounts SupportedOverTheEngineTargets(
            Func<GpuPixelFormat, bool, SampleCountFlags> sampleCounts)
            => SupportedIncludingFramebufferLimits(
                SampleCountFlags.Count1Bit | SampleCountFlags.Count2Bit | SampleCountFlags.Count4Bit
                    | SampleCountFlags.Count8Bit | SampleCountFlags.Count16Bit | SampleCountFlags.Count32Bit,
                SampleCountFlags.Count1Bit | SampleCountFlags.Count2Bit | SampleCountFlags.Count4Bit
                    | SampleCountFlags.Count8Bit | SampleCountFlags.Count16Bit | SampleCountFlags.Count32Bit,
                SampleCountFlags.Count1Bit | SampleCountFlags.Count2Bit | SampleCountFlags.Count4Bit
                    | SampleCountFlags.Count8Bit | SampleCountFlags.Count16Bit | SampleCountFlags.Count32Bit,
                sampleCounts);

        /// <summary>
        /// The same intersection including the physical device's framebuffer colour, depth and stencil limits.
        /// </summary>
        internal static GpuSampleCounts SupportedIncludingFramebufferLimits(
            SampleCountFlags framebufferColor, SampleCountFlags framebufferDepth,
            SampleCountFlags framebufferStencil,
            Func<GpuPixelFormat, bool, SampleCountFlags> sampleCounts)
        {
            ArgumentNullException.ThrowIfNull(sampleCounts);

            SampleCountFlags common = SampleCountFlags.Count1Bit | SampleCountFlags.Count2Bit
                | SampleCountFlags.Count4Bit | SampleCountFlags.Count8Bit | SampleCountFlags.Count16Bit
                | SampleCountFlags.Count32Bit;
            common &= framebufferColor;
            common &= framebufferDepth;
            common &= framebufferStencil;
            foreach ((GpuPixelFormat format, bool depthAttachment) in Formats)
                common &= sampleCounts(format, depthAttachment);

            GpuSampleCounts supported = GpuSampleCounts.None;
            if ((common & SampleCountFlags.Count1Bit) != 0) supported |= GpuSampleCounts.One;
            if ((common & SampleCountFlags.Count2Bit) != 0) supported |= GpuSampleCounts.Two;
            if ((common & SampleCountFlags.Count4Bit) != 0) supported |= GpuSampleCounts.Four;
            if ((common & SampleCountFlags.Count8Bit) != 0) supported |= GpuSampleCounts.Eight;
            if ((common & SampleCountFlags.Count16Bit) != 0) supported |= GpuSampleCounts.Sixteen;
            if ((common & SampleCountFlags.Count32Bit) != 0) supported |= GpuSampleCounts.ThirtyTwo;
            return GpuSampleCountSet.Normalize(supported);
        }
    }
}
