using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// <see cref="GpuCapabilities.MaxMsaaSampleCount"/>, READ OFF THE INCUMBENT'S OWN COMPUTATION AND REPRODUCED
    /// (V-C5), with the citation pinned below. Work-breakdown row 15
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/525).
    ///
    /// <para><b>NEITHER DRAFT'S INVENTED FORMULA IS TAKEN, AND THAT IS THE WHOLE DECISION.</b> One draft computed
    /// this as the minimum over framebuffer colour and depth sample-count LIMITS intersected with per-format image
    /// properties across three MRT formats. The other computed it as the AND of framebuffer colour, framebuffer
    /// depth and sampled-image colour sample counts. Those are DIFFERENT computations, at most one of them can
    /// equal the incumbent's, and both drafts then asserted equality with the incumbent as a test. That is exactly
    /// the failure phase 2 had to correct in flight, where a first draft asked the driver a different question
    /// than the incumbent did and "asserted equal" rested on the two happening to answer the same. So row 18's
    /// zero-difference parity assertion (https://github.com/APKiwiOrg/KhaozEngine/issues/528) is satisfiable HERE
    /// by construction rather than by luck.</para>
    ///
    /// <para><b>THE DESIGN DOCUMENT DESCRIBES THE WRONG CALL AND THE INCUMBENT IS THE AUTHORITY.</b> Section 13
    /// says the incumbent's shape is "a per-format <c>vkGetPhysicalDeviceFormatProperties</c> read reduced to the
    /// highest supported bit". It is not: <c>VkGraphicsDevice.GetSampleCountLimit</c> calls
    /// <c>vkGetPhysicalDeviceImageFormatProperties</c> and reduces the <c>sampleCounts</c> field of the
    /// <c>VkImageFormatProperties</c> it returns, which is a different query with a different answer (it takes the
    /// image type, the tiling and the USAGE, and a format's sample counts genuinely differ by usage). Reproducing
    /// what the incumbent does is the decision, so the call below is the image-format one and the design doc
    /// carries a corrected-in-flight note. This paragraph is why re-reading the source before writing was made
    /// this row's own obligation.</para>
    ///
    /// <para><b>THE THREE FORMATS ARE THE ENGINE'S, NOT THE BACKEND'S.</b>
    /// <c>KhaozEngine.Gpu.Internal.VeldridMap.MaxMsaaSampleCount</c> (deleted in 18.0.0, in git history) folded
    /// the MINIMUM over the colour target, the linear-depth target and the depth-stencil target the 3D scene
    /// renders into, because every attachment of an MRT must support the count. Both halves of the computation
    /// are reproduced here: the fold and the per-format query.</para>
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
        /// WHAT THIS REPRODUCES, AS A MEMBER RATHER THAN A LINE NUMBER (V-I6). Pinned in a constant so a reader
        /// comparing the two sources knows exactly which two functions to open, and so a later edit that changed
        /// the computation without changing this string is a lie somebody has to write deliberately. Only one of
        /// the two is still openable in the tree: Veldrid's own is upstream, and the engine's fold went with the
        /// incumbent in 18.0.0, so it reads out of git history now.
        /// </summary>
        internal const string Citation =
            "Veldrid 4.9.103 (Vulkan tree v4.9.0): VkGraphicsDevice.GetSampleCountLimit, folded by "
            + "KhaozEngine.Gpu.Internal.VeldridMap.MaxMsaaSampleCount (deleted in 18.0.0) over "
            + "R8_G8_B8_A8_UNorm, R32_Float and D32_Float_S8_UInt.";

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
        /// The MINIMUM of <see cref="Reduce"/> over the three formats, which is
        /// <see cref="GpuCapabilities.MaxMsaaSampleCount"/>.
        /// </summary>
        /// <param name="sampleCounts">The device's <c>sampleCounts</c> for one format and one attachment usage.
        /// Real on a device, a table in the device-free tests.</param>
        internal static int MinOverTheEngineTargets(
            Func<GpuPixelFormat, bool, SampleCountFlags> sampleCounts)
        {
            ArgumentNullException.ThrowIfNull(sampleCounts);

            int limit = int.MaxValue;
            foreach ((GpuPixelFormat format, bool depthAttachment) in Formats)
            {
                limit = Math.Min(limit, Reduce(sampleCounts(format, depthAttachment)));
            }

            return limit;
        }
    }
}
