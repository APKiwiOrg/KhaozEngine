using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Metal.Internal.ObjC;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// EVERY SWAPCHAIN DECISION THAT NEEDS NO DEVICE, asserted on every leg. Work-breakdown row 15
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/581).
    ///
    /// <para><b>THIS FILE AND ITS SIBLINGS ARE THE FIRST AUTOMATED COVERAGE THIS SURFACE HAS EVER HAD.</b> MM7
    /// records that not one line of the incumbent's <c>MTLSwapchain</c>, <c>MTLSwapchainFramebuffer</c>,
    /// <c>nextDrawable</c> or <c>presentDrawable</c> runs in CI on any leg, ever, and that the Metal leg being
    /// otherwise the best-covered leg in the matrix makes a green run read as stronger evidence than it is.
    /// Nothing can move a real <c>CAMetalLayer</c> onto a headless runner. What CAN move is every decision around
    /// it, and this is where they are.</para>
    /// </summary>
    public sealed class MetalSwapchainPolicyTests
    {
        /// <summary>
        /// The incumbent's own conditional: <c>B8_G8_R8_A8_UNorm</c> or its sRGB sibling, and nothing else. Both
        /// arms, even though the shipped path can only reach one, because the arm that cannot be reached is the
        /// one nobody would notice going wrong.
        /// </summary>
        [Fact]
        public void TheLayerFormatIsTheIncumbentsPairAndNothingElse()
        {
            // BOTH ARMS IN ONE ROW rather than a [Theory], because MTLPixelFormat is internal to the backend and
            // an xUnit theory argument has to be as accessible as the public test class it lands on.
            Assert.Equal(MTLPixelFormat.BGRA8Unorm, MetalSwapchainPolicy.LayerPixelFormat(false));
            Assert.Equal(MTLPixelFormat.BGRA8UnormSrgb, MetalSwapchainPolicy.LayerPixelFormat(true));
        }

        /// <summary>
        /// THE SHIPPED PATH ASKS FOR NON-SRGB, and this pins the constant rather than the reasoning. The one
        /// windowed site in <c>GpuDeviceContext</c> builds its Veldrid <c>SwapchainDescription</c> with
        /// <c>colorSrgb: false</c>, and <see cref="GpuWindowedDeviceRequest"/> has no field for it at all, so a
        /// native device cannot ask for the other arm however it is created.
        /// </summary>
        [Fact]
        public void TheShippedPathAsksForTheNonSrgbFormat()
        {
            Assert.False(MetalSwapchainPolicy.ColourSrgbRequested);
            Assert.Equal(MTLPixelFormat.BGRA8Unorm,
                MetalSwapchainPolicy.LayerPixelFormat(MetalSwapchainPolicy.ColourSrgbRequested));
        }

        /// <summary>
        /// THE SEAM HAS ONE MEMBER FOR BOTH, which is a fact about <see cref="GpuPixelFormat"/> rather than a
        /// shortcut: it has no sRGB member, so an sRGB swapchain is describable to Metal and not to the engine.
        /// Asserted because a pipeline is validated against the framebuffer's published format, so a policy that
        /// answered two different members here would make every pipeline built against an sRGB window invalid.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TheSeamColourFormatIsTheSameMemberForBothArms(bool srgb)
            => Assert.Equal(GpuPixelFormat.B8G8R8A8UNorm, MetalSwapchainPolicy.SeamColourFormat(srgb));

        /// <summary>
        /// A HOST VIEW's FRAME BECOMES A DRAWABLE SIZE BY TRUNCATION, which is the incumbent's <c>(uint)</c> cast
        /// reproduced. The fractional case is the one that matters: a Retina window reports a fractional point
        /// size often enough that rounding instead would produce a drawable one pixel wider than the incumbent's
        /// on the same window, which is a golden-invisible, human-visible difference of exactly the kind M-W1
        /// exists to prevent.
        /// </summary>
        [Theory]
        [InlineData(1280d, 720d, 1280u, 720u)]
        [InlineData(1279.9d, 719.5d, 1279u, 719u)]
        [InlineData(1d, 1d, 1u, 1u)]
        public void AHostViewFrameTruncatesRatherThanRounds(double w, double h, uint expectedW, uint expectedH)
        {
            MetalDrawableSize size = MetalSwapchainPolicy.SizeOfHostView(Frame(w, h));

            Assert.Equal(expectedW, size.Width);
            Assert.Equal(expectedH, size.Height);
        }

        /// <summary>
        /// A DEGENERATE FRAME RESOLVES TO ZERO RATHER THAN WRAPPING, and then the clamp handles it. A window
        /// mid-teardown and a minimised window both report sizes an unchecked cast turns into four billion, which
        /// would reach <c>-setDrawableSize:</c> as a request for a texture no machine can allocate rather than as
        /// the one-by-one the orphan path is built for.
        /// </summary>
        [Theory]
        [InlineData(0d, 0d)]
        [InlineData(-4d, 8d)]
        [InlineData(double.NaN, double.NaN)]
        public void ADegenerateFrameResolvesToZeroAndThenClampsToOnePixel(double w, double h)
        {
            MetalDrawableSize size = MetalSwapchainPolicy.SizeOfHostView(Frame(w, h));

            Assert.True(size.IsEmpty);

            MetalDrawableSize clamped = size.AtLeastOnePixel;
            Assert.True(clamped.Width >= 1u);
            Assert.True(clamped.Height >= 1u);
        }

        /// <summary>The clamp leaves a real size alone, which is the half a clamp usually gets wrong.</summary>
        [Fact]
        public void TheClampLeavesARealSizeAlone()
        {
            var size = new MetalDrawableSize(1920u, 1080u);

            Assert.Equal(size, size.AtLeastOnePixel);
            Assert.False(size.IsEmpty);
        }

        /// <summary>
        /// AND THE SIZE CROSSES AS A <c>CGSize</c> OF DOUBLES, which is the shape
        /// <c>-[CAMetalLayer setDrawableSize:]</c> takes. Pinned because the conversion is the last managed step
        /// before an arm64 register-class decision that no test on this leg can see.
        /// </summary>
        [Fact]
        public void TheSizeCrossesAsACGSizeOfDoubles()
        {
            CGSize size = new MetalDrawableSize(800u, 600u).ToCGSize();

            Assert.Equal(800d, size.Width);
            Assert.Equal(600d, size.Height);
        }

        static CGRect Frame(double width, double height)
            => new(new CGPoint(0d, 0d), new CGSize(width, height));
    }
}
