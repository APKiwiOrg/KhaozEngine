using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// DECISION V-W1 AND V-W2 AS A PURE FUNCTION: what format, what colour space, what present mode, how many
    /// images and what extent a swapchain is created with, reproduced from the incumbent except in the two places
    /// the incumbent was wrong.
    ///
    /// <para><b>REPRODUCED EXACTLY, BECAUSE IT IS VISIBLE ONLY TO A HUMAN EYE AND CHANGING IT BUYS NOTHING THIS
    /// PHASE IS MEASURING.</b> Phase 2 took the flip-model swapchain off the table for its v1 on the grounds that
    /// the swapchain is the one area with zero automated coverage anywhere in the net, and that reasoning applies
    /// here with MORE force: the Vulkan golden legs are headless and a headless Vulkan device enables no surface
    /// extension at all, so not one line of this path runs in CI, on any leg, ever (MV9). What is reproduced is
    /// the surface format and colour space, the present-mode preference order, the image count, the usage bits,
    /// the composite alpha and <c>clipped</c>. What has no Vulkan analogue at all is phase 2's actual subject:
    /// rendering here goes DIRECTLY into the swapchain image, so there is no blit-versus-flip question to
    /// have.</para>
    ///
    /// <para><b>FIFO_RELAXED UNDER A VSYNC REQUEST IS ARGUABLY THE WRONG ANSWER AND IS REPRODUCED ANYWAY.</b> It
    /// permits tearing on a late frame, which is not what somebody asking for vsync asked for. It stays because
    /// the pacing work (https://github.com/APKiwiOrg/KhaozEngine/issues/380) is where that gets decided WITH A
    /// MEASUREMENT, and this phase must not move the variable underneath it. Changing it here would make every
    /// pacing capture taken against this backend incomparable with every one taken against the incumbent, which
    /// is the one thing the reproduction exists to prevent.</para>
    ///
    /// <para><b>DEPARTURE ONE (V-W2): <c>preTransform</c> READS <c>currentTransform</c></b> rather than being
    /// hardcoded to <c>IDENTITY</c>. Hardcoding identity on a surface reporting a rotation is wrong on any device
    /// that would reach it, and this fleet's desktop surfaces all report identity, so the departure costs nothing
    /// where it is exercised and is correct where it is not. Reproducing a bug a different device WOULD reach is
    /// not parity.</para>
    ///
    /// <para><b>DEPARTURE TWO (V-W2): THE sRGB FALLBACK'S THROW IS REACHABLE HERE.</b> The incumbent meant to
    /// refuse a surface that offers no sRGB format when sRGB was requested, and its check compared a variable it
    /// had already set to <c>VK_FORMAT_UNDEFINED</c> against an sRGB format, so the condition was never true and
    /// the throw was dead code. That shape is not copied. <see cref="ChooseFormat"/> refuses for real, which is
    /// the only way the request means anything.</para>
    ///
    /// <para>Everything here is pure and takes a <see cref="VulkanSurfaceReport"/>, so every value the create-info
    /// will carry is asserted under <c>dotnet test</c> on a machine with no Vulkan loader and no window.</para>
    /// </summary>
    internal static class VulkanSwapchainPolicy
    {
        /// <summary>The colour space every shipped path presents in.</summary>
        internal const ColorSpaceKHR PresentedColourSpace =
            ColorSpaceKHR.SpaceSrgbNonlinearKhr;

        /// <summary>The format the engine's windowed path asks for, since it creates its swapchain with sRGB
        /// off.</summary>
        internal const Format LinearBgra = Format.B8G8R8A8Unorm;

        /// <summary>The format an sRGB request asks for instead.</summary>
        internal const Format SrgbBgra = Format.B8G8R8A8Srgb;

        /// <summary>
        /// The whole create-info, decided from what the surface reports plus what the caller wants.
        /// </summary>
        /// <param name="surface">What the surface reports right now. Re-read before every recreation, because
        /// every one of these values can change when the window does.</param>
        /// <param name="requested">The size the caller asked for, used only when the surface dictates none.</param>
        /// <param name="syncToVerticalBlank">Whether the caller wants presentation synced to the vertical blank,
        /// which selects the present-mode ladder.</param>
        /// <param name="srgb">Whether the caller wants an sRGB swapchain. False on every shipped path, because the
        /// engine's windowed device is created with sRGB off.</param>
        /// <param name="warning">A line worth logging about a choice that was not the preferred one, or null when
        /// everything came back as asked for. Returned rather than logged so this stays pure.</param>
        internal static VulkanSwapchainSpec Decide(in VulkanSurfaceReport surface, VulkanExtent requested,
            bool syncToVerticalBlank, bool srgb, out string? warning)
        {
            VulkanSurfaceFormatPair format = ChooseFormat(surface.Formats, srgb, out warning);

            return new VulkanSwapchainSpec(
                ImageCount: ChooseImageCount(surface.MinImageCount, surface.MaxImageCount),
                Format: format.Format,
                ColourSpace: format.ColourSpace,
                Extent: ChooseExtent(surface, requested),
                PresentMode: ChoosePresentMode(surface.PresentModes, syncToVerticalBlank),
                // DEPARTURE ONE. See the type remarks.
                PreTransform: surface.CurrentTransform,
                CompositeAlpha: VulkanSwapchainSpec.Opaque,
                Usage: VulkanSwapchainSpec.ColourAndTransferDst,
                Clipped: true);
        }

        /// <summary>
        /// <c>min(maxImageCount, minImageCount + 1)</c>, with a maximum of 0 read as no limit.
        /// <para>
        /// ONE MORE THAN THE MINIMUM is what gives the presentation engine an image to hold while the application
        /// draws into another, and asking for the minimum alone is the shape where every acquire waits for the
        /// display. The clamp against the maximum matters on drivers that report one: asking for more than the
        /// maximum fails creation outright rather than being rounded down.
        /// </para>
        /// </summary>
        internal static uint ChooseImageCount(uint minImageCount, uint maxImageCount)
        {
            uint wanted = minImageCount + 1;
            return maxImageCount != 0 && wanted > maxImageCount ? maxImageCount : wanted;
        }

        /// <summary>
        /// THE EXTENT, and the whole of this backend's structural answer to a minimised window.
        /// <para>
        /// A surface that dictates its own size wins outright, because a swapchain created at any other size is
        /// rejected. A surface that dictates none takes the caller's request. EITHER WAY the result is clamped
        /// into the surface's reported minimum and maximum, which is what makes a zero-extent request survivable:
        /// a minimised window reports <c>currentExtent</c> and both bounds as zero, the clamp produces zero, and
        /// <see cref="VulkanSwapchainSpec.IsCreatable"/> then reads false so the boundary never calls
        /// <c>vkCreateSwapchainKHR</c> with a size the specification forbids
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/81).
        /// </para>
        /// </summary>
        internal static VulkanExtent ChooseExtent(in VulkanSurfaceReport surface, VulkanExtent requested)
        {
            VulkanExtent wanted = surface.CurrentExtent.IsUndecided ? requested : surface.CurrentExtent;

            return new VulkanExtent(
                Clamp(wanted.Width, surface.MinExtent.Width, surface.MaxExtent.Width),
                Clamp(wanted.Height, surface.MinExtent.Height, surface.MaxExtent.Height));
        }

        /// <summary>
        /// THE PRESENT-MODE LADDER, reproduced rung for rung: <c>FIFO_RELAXED</c> then <c>FIFO</c> under a vsync
        /// request, <c>MAILBOX</c> then <c>IMMEDIATE</c> then <c>FIFO</c> without one. <c>FIFO</c> is required by
        /// the specification on every implementation, so the ladder needs no failure arm.
        /// </summary>
        internal static PresentModeKHR ChoosePresentMode(
            IReadOnlyList<PresentModeKHR> supported, bool syncToVerticalBlank)
        {
            ArgumentNullException.ThrowIfNull(supported);

            if (syncToVerticalBlank)
            {
                // FIFO_RELAXED UNDER A VSYNC REQUEST. See the type remarks: this permits tearing on a late frame
                // and is reproduced deliberately, because the pacing work is where it gets decided.
                return Has(supported, PresentModeKHR.FifoRelaxedKhr)
                    ? PresentModeKHR.FifoRelaxedKhr
                    : PresentModeKHR.FifoKhr;
            }

            if (Has(supported, PresentModeKHR.MailboxKhr))
                return PresentModeKHR.MailboxKhr;
            if (Has(supported, PresentModeKHR.ImmediateKhr))
                return PresentModeKHR.ImmediateKhr;

            return PresentModeKHR.FifoKhr;
        }

        /// <summary>
        /// THE FORMAT, with departure two inside it.
        /// <para>
        /// A single reported format of <c>VK_FORMAT_UNDEFINED</c> is the legacy signal that the surface has no
        /// preference at all, and the answer there is the format that was asked for. Otherwise the exact pair is
        /// looked for, then the format alone in any colour space, and only then does the sRGB request refuse. A
        /// non-sRGB request falls through to the surface's first format with a warning, which is a real
        /// presentable swapchain in a colour the caller did not choose rather than a window that never opens.
        /// </para>
        /// </summary>
        /// <exception cref="NotSupportedException">sRGB was requested and the surface offers no sRGB format. THIS
        /// THROW IS THE DEPARTURE: the incumbent meant to make it and could not, because it compared a variable it
        /// had already set to <c>VK_FORMAT_UNDEFINED</c> against an sRGB format.</exception>
        /// <exception cref="ArgumentException">The surface reported no formats at all, which is a surface nothing
        /// can present to.</exception>
        internal static VulkanSurfaceFormatPair ChooseFormat(IReadOnlyList<VulkanSurfaceFormatPair> formats,
            bool srgb, out string? warning)
        {
            ArgumentNullException.ThrowIfNull(formats);
            warning = null;

            if (formats.Count == 0)
            {
                throw new ArgumentException(
                    "The surface handed to the native Vulkan backend reported no formats at all. A surface with no "
                    + "format cannot carry a swapchain, and vkGetPhysicalDeviceSurfaceFormatsKHR is required to "
                    + "report at least one for a surface the device can present to, so this is a surface belonging "
                    + "to another physical device or one that has already been lost.",
                    nameof(formats));
            }

            Format wanted = srgb ? SrgbBgra : LinearBgra;

            // THE LEGACY "ANY FORMAT" SIGNAL, and the only place VK_FORMAT_UNDEFINED means something here.
            if (formats.Count == 1 && formats[0].Format == Format.Undefined)
                return new VulkanSurfaceFormatPair(wanted, PresentedColourSpace);

            foreach (VulkanSurfaceFormatPair candidate in formats)
            {
                if (candidate.Format == wanted && candidate.ColourSpace == PresentedColourSpace) return candidate;
            }

            foreach (VulkanSurfaceFormatPair candidate in formats)
            {
                if (candidate.Format != wanted) continue;

                warning = "The native Vulkan backend's surface offers " + Describe(wanted) + " only in colour "
                    + "space " + candidate.ColourSpace.ToString() + " rather than the expected "
                    + PresentedColourSpace.ToString() + ", and took it. Presented colours may differ from every "
                    + "other backend's on this machine.";
                return candidate;
            }

            if (srgb)
            {
                // DEPARTURE TWO. The throw the incumbent intended and could not reach.
                throw new NotSupportedException(
                    "An sRGB swapchain was asked for and this surface offers no sRGB format. The native Vulkan "
                    + "backend refuses rather than silently presenting through a linear format, because a request "
                    + "for sRGB that is quietly ignored produces output that is wrong in a way no error names. "
                    + "The surface offers: " + DescribeAll(formats) + ".");
            }

            warning = "The native Vulkan backend's surface offers no " + Describe(wanted) + " at all, so the "
                + "swapchain took the surface's first format, " + Describe(formats[0].Format) + " in "
                + formats[0].ColourSpace.ToString() + ". Presented colours may differ from every other backend's "
                + "on this machine. The surface offers: " + DescribeAll(formats) + ".";
            return formats[0];
        }

        /// <summary>
        /// The chosen swapchain format as the SEAM names it, which is what the swapchain framebuffer publishes in
        /// its <c>Outputs</c> and what a pipeline built against it is validated on.
        /// <para>
        /// TWO FORMATS ARE EXPRESSIBLE AND THE REST REFUSE, which is honest rather than restrictive.
        /// <c>GpuPixelFormat</c> has eight members and none of them is an sRGB one, so a swapchain created in an
        /// sRGB format could not describe itself to the seam at all and every pipeline built against it would be
        /// validated against the wrong description. That is a silent wrong-colours defect, so it throws here
        /// instead, at creation, naming the format the surface offered.
        /// </para>
        /// </summary>
        /// <exception cref="NotSupportedException">The format is one the seam cannot name.</exception>
        internal static GpuPixelFormat SeamFormatFor(Format format) => format switch
        {
            Format.B8G8R8A8Unorm => GpuPixelFormat.B8G8R8A8UNorm,
            Format.R8G8B8A8Unorm => GpuPixelFormat.R8G8B8A8UNorm,
            _ => throw new NotSupportedException(
                "The native Vulkan backend's surface produced the swapchain format " + format.ToString()
                + ", which GpuPixelFormat cannot name. A swapchain the seam cannot describe would have every "
                + "pipeline built against it validated on the wrong output description, which is a wrong-colours "
                + "defect no error reports, so creation refuses here instead. The two expressible formats are "
                + "B8G8R8A8_UNORM and R8G8B8A8_UNORM."),
        };

        static bool Has(IReadOnlyList<PresentModeKHR> supported,
            PresentModeKHR mode)
        {
            for (int i = 0; i < supported.Count; i++)
            {
                if (supported[i] == mode) return true;
            }
            return false;
        }

        // Clamped with the LOW bound applied last, so a surface reporting a minimum above its maximum (which is a
        // driver bug rather than a state) lands on the minimum rather than on a size below it that creation would
        // reject anyway.
        static uint Clamp(uint value, uint min, uint max)
        {
            if (value > max) value = max;
            if (value < min) value = min;
            return value;
        }

        static string Describe(Format format) => format.ToString();

        static string DescribeAll(IReadOnlyList<VulkanSurfaceFormatPair> formats)
        {
            var parts = new List<string>(formats.Count);
            foreach (VulkanSurfaceFormatPair pair in formats)
                parts.Add(pair.Format.ToString() + "/" + pair.ColourSpace.ToString());

            return string.Join(", ", parts);
        }
    }
}
