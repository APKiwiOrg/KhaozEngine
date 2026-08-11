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
    ///
    /// <para><b>THE POINTS-TO-PIXELS ARITHMETIC IS HERE FOR EXACTLY THAT REASON</b>
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/605">#605</see>). The alternative shape,
    /// <c>-[NSView convertRectToBacking:]</c>, would have done the multiply inside Cocoa and produced the same
    /// pixels on a Mac while moving the one piece of this that CAN be asserted on Linux and Windows into a
    /// selector that can be asserted nowhere. Reading the scalar and multiplying in managed code is what puts the
    /// Retina fix on every leg.</para>
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
        /// kept. The fractional case is the one that matters: a window reports a fractional point size often
        /// enough that rounding instead would produce a drawable one pixel wider than the window's real backing
        /// size, which is a golden-invisible, human-visible difference of exactly the kind M-W1 exists to prevent.
        /// <para>Scale 1 is the non-Retina display, where points and pixels are the same number.</para>
        /// </summary>
        [Theory]
        [InlineData(1280d, 720d, 1280u, 720u)]
        [InlineData(1279.9d, 719.5d, 1279u, 719u)]
        [InlineData(1d, 1d, 1u, 1u)]
        public void AHostViewFrameTruncatesRatherThanRounds(double w, double h, uint expectedW, uint expectedH)
        {
            MetalDrawableSize size = MetalSwapchainPolicy.SizeOfHostView(Frame(w, h), 1d);

            Assert.Equal(expectedW, size.Width);
            Assert.Equal(expectedH, size.Height);
        }

        /// <summary>
        /// THE RETINA FIX (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/605">#605</see>): a view
        /// frame is in POINTS and a drawable size is in PIXELS, so the frame is MULTIPLIED by the window's backing
        /// scale factor. The incumbent's NSView arm does not do this and its own UIView arm does, which is the
        /// precedent that makes diverging here the parity-correct answer rather than an improvement smuggled into
        /// a backend swap.
        /// <para>
        /// THE TRUNCATION IS AFTER THE MULTIPLY, which the 1279.9 x 2 row is there to pin: scaling first gives
        /// 2559 and truncating first would give 2558, and only the first is the number the window actually
        /// occupies. A 3.0 row is carried for the same reason the sRGB arm of the pixel format is: the shipped
        /// path cannot reach it today, and an unreachable arm is the one nobody notices going wrong.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData(1280d, 720d, 2d, 2560u, 1440u)]
        [InlineData(1279.9d, 719.5d, 2d, 2559u, 1439u)]
        [InlineData(800d, 600d, 3d, 2400u, 1800u)]
        [InlineData(640d, 480d, 1.5d, 960u, 720u)]
        public void AHostViewFrameIsScaledIntoPixelsBeforeItIsTruncated(double w, double h, double scale,
            uint expectedW, uint expectedH)
        {
            MetalDrawableSize size = MetalSwapchainPolicy.SizeOfHostView(Frame(w, h), scale);

            Assert.Equal(expectedW, size.Width);
            Assert.Equal(expectedH, size.Height);
        }

        /// <summary>
        /// A DEGENERATE SCALE FALLS BACK TO THE NON-RETINA IDENTITY rather than propagating. <c>objc_msgSend</c>
        /// to nil answers zero, so a handle that is not a live <c>NSWindow</c> reports a scale of 0, and a zero
        /// applied faithfully would configure the layer at nothing at all on a window whose points are perfectly
        /// readable. Falling back to 1.0 is the incumbent's exact behaviour in the one case this backend cannot do
        /// better in, which is the smallest divergence available rather than a new failure mode.
        /// </summary>
        [Theory]
        [InlineData(0d)]
        [InlineData(-2d)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void ADegenerateScaleFallsBackToTheUnscaledSize(double scale)
        {
            Assert.Equal(1d, MetalSwapchainPolicy.UsableScale(scale));

            MetalDrawableSize size = MetalSwapchainPolicy.SizeOfHostView(Frame(1280d, 720d), scale);

            Assert.Equal(1280u, size.Width);
            Assert.Equal(720u, size.Height);
        }

        /// <summary>A REAL SCALE IS LEFT ALONE, which is the half a fallback usually gets wrong.</summary>
        [Theory]
        [InlineData(1d)]
        [InlineData(2d)]
        [InlineData(3d)]
        public void AUsableScaleIsPassedThroughUnchanged(double scale)
            => Assert.Equal(scale, MetalSwapchainPolicy.UsableScale(scale));

        /// <summary>
        /// A DEGENERATE FRAME RESOLVES TO ZERO RATHER THAN WRAPPING, and then the clamp handles it. A window
        /// mid-teardown and a minimised window both report sizes an unchecked cast turns into four billion, which
        /// would reach <c>-setDrawableSize:</c> as a request for a texture no machine can allocate rather than as
        /// the one-by-one the orphan path is built for. The scale cannot rescue it: zero points is zero pixels at
        /// any scale, which is the row the Retina arm must not have changed.
        /// </summary>
        [Theory]
        [InlineData(0d, 0d)]
        [InlineData(-4d, 8d)]
        [InlineData(double.NaN, double.NaN)]
        public void ADegenerateFrameResolvesToZeroAndThenClampsToOnePixel(double w, double h)
        {
            MetalDrawableSize size = MetalSwapchainPolicy.SizeOfHostView(Frame(w, h), 2d);

            Assert.True(size.IsEmpty);

            MetalDrawableSize clamped = size.AtLeastOnePixel;
            Assert.True(clamped.Width >= 1u);
            Assert.True(clamped.Height >= 1u);
        }

        /// <summary>
        /// AND A SCALED SIZE STILL SATURATES RATHER THAN WRAPPING. A frame just under the cast's limit multiplied
        /// by 2 is past it, and the scale is exactly what turns a size that used to fit into one that does not, so
        /// the saturation guard has to sit AFTER the multiply and this is what says it does.
        /// </summary>
        [Fact]
        public void AScaledSizePastTheCastsLimitSaturatesRatherThanWrapping()
        {
            MetalDrawableSize size = MetalSwapchainPolicy.SizeOfHostView(
                Frame(uint.MaxValue - 1000d, uint.MaxValue - 1000d), 2d);

            Assert.Equal(uint.MaxValue, size.Width);
            Assert.Equal(uint.MaxValue, size.Height);
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
