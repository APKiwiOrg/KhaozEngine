using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Silk.NET.Vulkan;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// DECISION V-W1's REPRODUCTION AND V-W2's TWO DEPARTURES, asserted value by value.
    /// <para>
    /// These are the assertions that matter most in the whole row, because the present path is visible only to a
    /// human eye and has ZERO automated coverage in CI on any leg (MV9). A format or a present mode that drifted
    /// from the incumbent's would be found by a tester at a window or not at all, so the choice is made in a pure
    /// function and pinned here instead.
    /// </para>
    /// </summary>
    public sealed class VulkanSwapchainPolicyTests
    {
        // ---- format and colour space -----------------------------------------------------------------------

        /// <summary>The shipped path asks for BGRA8 UNORM in SRGB_NONLINEAR, which is the incumbent's choice
        /// exactly, and the engine's windowed device is created with sRGB off.</summary>
        [Fact]
        public void TheShippedPathTakesTheLinearBgraPair()
        {
            VulkanSurfaceFormatPair chosen = VulkanSwapchainPolicy.ChooseFormat(
                FakeVulkanSurfaceApi.Desktop().Formats, srgb: false, out string? warning);

            Assert.Equal(Format.B8G8R8A8Unorm, chosen.Format);
            Assert.Equal(ColorSpaceKHR.SpaceSrgbNonlinearKhr, chosen.ColourSpace);
            Assert.Null(warning);
        }

        /// <summary>A surface reporting a single UNDEFINED format is the legacy "no preference" signal, and the
        /// answer there is the format that was asked for rather than a refusal.</summary>
        [Fact]
        public void ASurfaceWithNoPreferenceGetsTheRequestedFormat()
        {
            var noPreference = new[] { new VulkanSurfaceFormatPair(Format.Undefined, ColorSpaceKHR.SpaceSrgbNonlinearKhr) };

            VulkanSurfaceFormatPair chosen = VulkanSwapchainPolicy.ChooseFormat(
                noPreference, srgb: false, out string? warning);

            Assert.Equal(Format.B8G8R8A8Unorm, chosen.Format);
            Assert.Null(warning);
        }

        /// <summary>
        /// DEPARTURE TWO (V-W2), AND THE POINT OF IT IS THAT THE THROW IS REACHABLE. The incumbent means to refuse
        /// a surface that offers no sRGB format when sRGB was asked for, and its check compares a variable it has
        /// already set to <c>VK_FORMAT_UNDEFINED</c> against an sRGB format, so the condition is never true and
        /// the throw is dead code. Reproducing a bug a different device WOULD reach is not parity.
        /// </summary>
        [Fact]
        public void AnSrgbRequestAgainstASurfaceWithNoSrgbFormatIsRefused()
        {
            NotSupportedException refused = Assert.Throws<NotSupportedException>(
                () => VulkanSwapchainPolicy.ChooseFormat(
                    FakeVulkanSurfaceApi.Desktop().Formats, srgb: true, out _));

            Assert.Contains("sRGB", refused.Message, StringComparison.Ordinal);
        }

        /// <summary>An sRGB request a surface CAN serve is served, so the refusal above is about the surface
        /// rather than about the request being unsupported outright.</summary>
        [Fact]
        public void AnSrgbRequestASurfaceCanServeIsServed()
        {
            var formats = new[]
            {
                new VulkanSurfaceFormatPair(Format.B8G8R8A8Unorm, ColorSpaceKHR.SpaceSrgbNonlinearKhr),
                new VulkanSurfaceFormatPair(Format.B8G8R8A8Srgb, ColorSpaceKHR.SpaceSrgbNonlinearKhr),
            };

            VulkanSurfaceFormatPair chosen = VulkanSwapchainPolicy.ChooseFormat(formats, srgb: true, out _);

            Assert.Equal(Format.B8G8R8A8Srgb, chosen.Format);
        }

        /// <summary>A surface offering neither of the two the seam can name still produces a presentable
        /// swapchain, with a WARNING, rather than a window that never opens. The refusal that follows is
        /// <see cref="VulkanSwapchainPolicy.SeamFormatFor"/>'s, and it is a different question.</summary>
        [Fact]
        public void ASurfaceWithNeitherPreferredFormatFallsBackAndSaysSo()
        {
            var exotic = new[] { new VulkanSurfaceFormatPair(Format.A2B10G10R10UnormPack32, ColorSpaceKHR.SpaceSrgbNonlinearKhr) };

            VulkanSurfaceFormatPair chosen = VulkanSwapchainPolicy.ChooseFormat(exotic, srgb: false, out string? warning);

            Assert.Equal(Format.A2B10G10R10UnormPack32, chosen.Format);
            Assert.NotNull(warning);
        }

        /// <summary>
        /// A FORMAT THE SEAM CANNOT NAME REFUSES AT CREATION rather than publishing a wrong output description. A
        /// swapchain framebuffer whose <c>Outputs</c> lied would have every pipeline built against it validated
        /// against the wrong thing, which is a wrong-colours defect no error reports.
        /// </summary>
        [Fact]
        public void AFormatTheSeamCannotNameRefuses()
        {
            Assert.Equal(GpuPixelFormat.B8G8R8A8UNorm, VulkanSwapchainPolicy.SeamFormatFor(Format.B8G8R8A8Unorm));
            Assert.Equal(GpuPixelFormat.R8G8B8A8UNorm, VulkanSwapchainPolicy.SeamFormatFor(Format.R8G8B8A8Unorm));
            Assert.Throws<NotSupportedException>(() => VulkanSwapchainPolicy.SeamFormatFor(Format.B8G8R8A8Srgb));
        }

        // ---- present mode ----------------------------------------------------------------------------------

        /// <summary>
        /// THE LADDER, RUNG BY RUNG. <c>FIFO_RELAXED</c> under a vsync request PERMITS TEARING on a late frame and
        /// is arguably the wrong answer, and it is reproduced anyway: the pacing work
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/380) is where that gets decided with a measurement,
        /// and this phase must not move the variable underneath it.
        /// </summary>
        [Theory]
        [InlineData(true, PresentModeKHR.FifoRelaxedKhr)]
        [InlineData(false, PresentModeKHR.MailboxKhr)]
        public void ThePresentModeLadderReproducesTheIncumbent(bool vsync, PresentModeKHR expected)
        {
            Assert.Equal(expected, VulkanSwapchainPolicy.ChoosePresentMode(
                FakeVulkanSurfaceApi.Desktop().PresentModes, vsync));
        }

        /// <summary>Each rung falls through to the next when the surface does not offer it, and FIFO is the floor
        /// because the specification requires every implementation to support it.</summary>
        [Theory]
        [InlineData(true, new[] { PresentModeKHR.FifoKhr }, PresentModeKHR.FifoKhr)]
        [InlineData(false, new[] { PresentModeKHR.FifoKhr, PresentModeKHR.ImmediateKhr }, PresentModeKHR.ImmediateKhr)]
        [InlineData(false, new[] { PresentModeKHR.FifoKhr }, PresentModeKHR.FifoKhr)]
        public void EachRungFallsThroughToTheNext(bool vsync, PresentModeKHR[] supported, PresentModeKHR expected)
        {
            Assert.Equal(expected, VulkanSwapchainPolicy.ChoosePresentMode(supported, vsync));
        }

        // ---- image count -----------------------------------------------------------------------------------

        /// <summary><c>min(maxImageCount, minImageCount + 1)</c>, with a maximum of 0 read as no limit.</summary>
        [Theory]
        [InlineData(2u, 8u, 3u)]
        [InlineData(2u, 0u, 3u)]
        [InlineData(3u, 3u, 3u)]
        [InlineData(1u, 1u, 1u)]
        public void TheImageCountIsOneMoreThanTheMinimumClampedToTheMaximum(uint min, uint max, uint expected)
        {
            Assert.Equal(expected, VulkanSwapchainPolicy.ChooseImageCount(min, max));
        }

        // ---- extent ----------------------------------------------------------------------------------------

        /// <summary>A surface that dictates its own size wins outright, because a swapchain created at any other
        /// size is rejected.</summary>
        [Fact]
        public void ASurfaceThatDictatesItsSizeWinsOverTheRequest()
        {
            VulkanExtent extent = VulkanSwapchainPolicy.ChooseExtent(
                FakeVulkanSurfaceApi.Desktop(1600, 900), new VulkanExtent(800, 600));

            Assert.Equal(new VulkanExtent(1600, 900), extent);
        }

        /// <summary>A surface that dictates none takes the caller's request, clamped into its own bounds.</summary>
        [Fact]
        public void ASurfaceThatDictatesNothingTakesTheRequestClamped()
        {
            VulkanSurfaceReport report = FakeVulkanSurfaceApi.Desktop() with
            {
                CurrentExtent = VulkanExtent.SurfaceDecidesNothing,
                MinExtent = new VulkanExtent(64, 64),
                MaxExtent = new VulkanExtent(1920, 1080),
            };

            Assert.Equal(new VulkanExtent(800, 600),
                VulkanSwapchainPolicy.ChooseExtent(report, new VulkanExtent(800, 600)));
            Assert.Equal(new VulkanExtent(1920, 1080),
                VulkanSwapchainPolicy.ChooseExtent(report, new VulkanExtent(4000, 4000)));
            Assert.Equal(new VulkanExtent(64, 64),
                VulkanSwapchainPolicy.ChooseExtent(report, new VulkanExtent(0, 0)));
        }

        /// <summary>
        /// THE MINIMISE CASE, and this backend's structural answer to
        /// https://github.com/APKiwiOrg/KhaozEngine/issues/81. A minimised window reports every extent as zero,
        /// the clamp produces zero, and the spec then reads as NOT CREATABLE, so <c>vkCreateSwapchainKHR</c> is
        /// never called at a size the specification forbids. The guard is the arithmetic rather than a special
        /// case somebody has to remember to write at each call site.
        /// </summary>
        [Fact]
        public void AMinimisedWindowProducesASpecThatIsNotCreatable()
        {
            VulkanSwapchainSpec spec = VulkanSwapchainPolicy.Decide(
                FakeVulkanSurfaceApi.Minimised(), new VulkanExtent(1280, 720), syncToVerticalBlank: true,
                srgb: false, out _);

            Assert.Equal(new VulkanExtent(0, 0), spec.Extent);
            Assert.False(spec.IsCreatable);
            Assert.Equal(new VulkanExtent(1, 1), spec.Extent.AtLeastOnePixel);
        }

        // ---- the whole create-info -------------------------------------------------------------------------

        /// <summary>
        /// THE REST OF V-W1's REPRODUCTION IN ONE ASSERTION: usage, composite alpha and <c>clipped</c>, none of
        /// which is narrowed against what the surface says it supports. Narrowing would be a THIRD departure and
        /// the design names exactly two.
        /// </summary>
        [Fact]
        public void TheCreateInfoReproducesUsageCompositeAlphaAndClipped()
        {
            VulkanSwapchainSpec spec = VulkanSwapchainPolicy.Decide(
                FakeVulkanSurfaceApi.Desktop(), new VulkanExtent(1280, 720), syncToVerticalBlank: true,
                srgb: false, out _);

            Assert.Equal(ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit, spec.Usage);
            Assert.Equal(CompositeAlphaFlagsKHR.OpaqueBitKhr, spec.CompositeAlpha);
            Assert.True(spec.Clipped);
            Assert.True(spec.IsCreatable);
        }

        /// <summary>
        /// DEPARTURE ONE (V-W2): <c>preTransform</c> READS the surface's <c>currentTransform</c> rather than being
        /// hardcoded to <c>IDENTITY</c>. Hardcoding identity on a surface reporting a rotation is wrong on any
        /// device that would reach it, and this fleet's desktop surfaces all report identity, so the departure
        /// costs nothing where it is exercised and is correct where it is not.
        /// </summary>
        [Fact]
        public void ThePreTransformIsTheSurfacesOwnRatherThanIdentity()
        {
            VulkanSurfaceReport rotated = FakeVulkanSurfaceApi.Desktop() with
            {
                CurrentTransform = SurfaceTransformFlagsKHR.Rotate90BitKhr,
            };

            VulkanSwapchainSpec spec = VulkanSwapchainPolicy.Decide(
                rotated, new VulkanExtent(1280, 720), syncToVerticalBlank: true, srgb: false, out _);

            Assert.Equal(SurfaceTransformFlagsKHR.Rotate90BitKhr, spec.PreTransform);
            Assert.NotEqual(SurfaceTransformFlagsKHR.IdentityBitKhr, spec.PreTransform);
        }

        /// <summary>A surface with no formats at all is a surface nothing can present to, and it is refused by
        /// name rather than producing a swapchain in an unspecified format.</summary>
        [Fact]
        public void ASurfaceWithNoFormatsIsRefused()
        {
            Assert.Throws<ArgumentException>(() => VulkanSwapchainPolicy.ChooseFormat(
                Array.Empty<VulkanSurfaceFormatPair>(), srgb: false, out _));
        }

        /// <summary>The whole decision is pure, so a second call on the same report answers identically. That is
        /// what lets the present boundary decide the create-info BEFORE it takes the submit lock without the
        /// answer changing under it.</summary>
        [Fact]
        public void TheDecisionIsPure()
        {
            VulkanSurfaceReport report = FakeVulkanSurfaceApi.Desktop();
            IReadOnlyList<VulkanSurfaceFormatPair> formats = report.Formats;

            VulkanSwapchainSpec first = VulkanSwapchainPolicy.Decide(
                report, new VulkanExtent(1280, 720), syncToVerticalBlank: false, srgb: false, out _);
            VulkanSwapchainSpec second = VulkanSwapchainPolicy.Decide(
                report, new VulkanExtent(1280, 720), syncToVerticalBlank: false, srgb: false, out _);

            Assert.Equal(first, second);
            Assert.Same(formats, report.Formats);
        }
    }
}
