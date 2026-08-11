using System;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// A DRAWABLE SIZE IN PIXELS, clamped where it is used rather than where it arrives.
    /// <para>
    /// A MINIMISED WINDOW REPORTS (0, 0) THROUGH THE WINDOWING LAYER's framebuffer-resize event, which is
    /// https://github.com/APKiwiOrg/KhaozEngine/issues/81 on the Vulkan sibling and reaches this backend through
    /// exactly the same callback. <c>CAMetalLayer</c> takes a zero <c>drawableSize</c> without complaining and
    /// then vends no drawable, so the clamp is what keeps the layer describable while the window is down and
    /// M-W5's orphan target is what the frame renders into meanwhile.
    /// </para>
    /// </summary>
    /// <param name="Width">Width in pixels, possibly zero before <see cref="AtLeastOnePixel"/>.</param>
    /// <param name="Height">Height in pixels.</param>
    internal readonly record struct MetalDrawableSize(uint Width, uint Height)
    {
        /// <summary>The same size with each dimension floored at one, which is what is written to the layer and
        /// what the orphan target is created at.</summary>
        internal MetalDrawableSize AtLeastOnePixel => new(Math.Max(1u, Width), Math.Max(1u, Height));

        /// <summary>True when either dimension is zero, which is the minimised window.</summary>
        internal bool IsEmpty => Width == 0 || Height == 0;

        /// <summary>As the <c>CGSize</c> <c>-setDrawableSize:</c> takes.</summary>
        internal CGSize ToCGSize() => new(Width, Height);
    }

    /// <summary>
    /// EVERY SWAPCHAIN DECISION THAT NEEDS NO DEVICE, so all of them are asserted on every leg rather than on the
    /// one machine that has a window. Work-breakdown row 15
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/581).
    ///
    /// <para><b>THIS TYPE IS WHERE MM7 IS ANSWERED AS FAR AS IT CAN BE.</b> The design records that not one line
    /// of the incumbent's swapchain runs in CI on any leg, ever, and that the Metal leg being otherwise the
    /// best-covered leg in the matrix makes a green run read as stronger evidence than it is. Nothing can move
    /// <c>nextDrawable</c> onto a headless runner. What CAN move is every decision AROUND it: which pixel format
    /// the layer gets, what a zero-sized window resolves to, and how the size the layer is configured at is
    /// derived. Those are here, with no <c>MTLDevice</c> and no <c>CAMetalLayer</c> anywhere in the type.</para>
    ///
    /// <para><b>THE INITIAL SIZE COMES OFF THE CONTENT VIEW AND NOT OFF THE REQUEST (M-W1), AND IT IS MULTIPLIED
    /// BY THE BACKING SCALE, WHICH IS THE ONE PLACE THIS BACKEND DIVERGES FROM THE INCUMBENT'S NSVIEW ARM
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/605">#605</see>).</b> <c>MTLSwapchain</c>'s
    /// constructor reads <c>contentView.frame.size</c> and writes it straight into <c>drawableSize</c>, ignoring
    /// the width and height its own <c>SwapchainDescription</c> carries. A view frame is in POINTS and a drawable
    /// size is in PIXELS, so that arm opens a Retina window at half its real resolution until the first
    /// framebuffer-resize callback (which forwards the windowing layer's pixel size) writes the right number over
    /// it.</para>
    ///
    /// <para><b>THE INCUMBENT'S OWN UIVIEW ARM MULTIPLIES BY THE NATIVE SCALE</b>, which is what makes this a fix
    /// rather than an improvement: the two arms of the same constructor disagree, one of them is right, and
    /// reproducing the wrong one field for field would be reproducing a defect rather than a decision. So the
    /// scale is read from <c>-[NSWindow backingScaleFactor]</c> and applied here, and
    /// <c>MetalSwapchainPolicyTests.AHostViewFrameIsScaledIntoPixelsBeforeItIsTruncated</c> pins it.</para>
    ///
    /// <para><b>WHAT WAS ALREADY CORRECT, AND WHAT ONLY A WINDOW CAN CONFIRM.</b> The STEADY STATE never had a
    /// defect: <c>ResizeSwapchain</c> writes the numbers the windowing layer forwards and those are already
    /// pixels, so this changes the FIRST frame and nothing after it. Whether that first frame was ever visible at
    /// half resolution is a Silk/GLFW question about callback timing that nobody here has measured, and gate 5's
    /// windowed pass read the window as full-scale, which is consistent with the callback landing before anything
    /// visible rendered. So the evidence for this change is the arithmetic and the incumbent's own disagreement
    /// with itself, and what a windowed run on a Retina display still has to confirm is that
    /// <c>CAMetalLayer.drawableSize</c> on frame one now equals the window's backing size.</para>
    /// </summary>
    internal static class MetalSwapchainPolicy
    {
        /// <summary>
        /// WHETHER THE SHIPPED PATH ASKS FOR AN sRGB SWAPCHAIN, which it does not, and the constant exists so the
        /// answer is written down once instead of being a literal <c>false</c> at the call site.
        /// <para>
        /// <c>GpuWindowedDeviceRequest</c> HAS NO FIELD FOR IT. The seam carries a window, a size and a vsync flag
        /// and nothing else, and the Veldrid path's own <c>SwapchainDescription</c> is built with
        /// <c>colorSrgb: false</c> at the one windowed site in <c>GpuDeviceContext</c>. So the sRGB arm of
        /// <see cref="LayerPixelFormat"/> is unreachable from the shipped path today. It is written and tested
        /// anyway because it is the incumbent's own conditional and M-W1 reproduces the configuration field for
        /// field: the day the seam grows the request, the arm is already correct rather than being invented then.
        /// </para>
        /// </summary>
        internal const bool ColourSrgbRequested = false;

        /// <summary>
        /// The layer's <c>pixelFormat</c>, which is the incumbent's <c>B8_G8_R8_A8_UNorm</c> or its sRGB sibling
        /// depending on <paramref name="colourSrgb"/>.
        /// </summary>
        internal static MTLPixelFormat LayerPixelFormat(bool colourSrgb)
            => colourSrgb ? MTLPixelFormat.BGRA8UnormSrgb : MTLPixelFormat.BGRA8Unorm;

        /// <summary>
        /// The colour format the swapchain framebuffer publishes in its <c>Outputs</c>, which every pipeline built
        /// against the window is validated against.
        /// <para>
        /// IT IS THE SAME MEMBER FOR BOTH ARMS, and that is a fact about the seam rather than a shortcut.
        /// <see cref="GpuPixelFormat"/> has no sRGB member at all, so an sRGB swapchain is describable to Metal
        /// and not to the engine. The layouts are identical, which is why the pair exists in
        /// <see cref="MTLPixelFormat"/> and not here: the difference is how the hardware converts on write, not
        /// what a pipeline has to declare.
        /// </para>
        /// </summary>
        internal static GpuPixelFormat SeamColourFormat(bool colourSrgb)
        {
            _ = colourSrgb;
            return GpuPixelFormat.B8G8R8A8UNorm;
        }

        /// <summary>
        /// The drawable size a host view's frame asks for, in PIXELS. <paramref name="frame"/> is in POINTS and
        /// <paramref name="backingScale"/> is how many pixels a point covers on the display the window is
        /// currently on, so the answer is their product (see the type remarks for why the multiply is here and not
        /// in Cocoa).
        /// <para>
        /// TRUNCATION RATHER THAN ROUNDING, AND IT HAPPENS AFTER THE MULTIPLY. The truncation is the incumbent's
        /// <c>(uint)</c> cast, kept: a drawable one pixel wider than the window's real backing size is not a
        /// better answer than one truncated to it. Doing it after the scale is what the incumbent's own UIView arm
        /// does, and it matters at a fractional point size, where truncating first would lose up to a whole pixel
        /// per point of scale rather than up to one pixel.
        /// </para>
        /// <para>
        /// A DEGENERATE FRAME RESOLVES TO ZERO and is then clamped by the caller, rather than wrapping to four
        /// billion through an unchecked cast: a window mid-teardown and a minimised window both report sizes an
        /// unchecked cast turns into a texture no machine can allocate.
        /// </para>
        /// <para>
        /// AND A DEGENERATE SCALE RESOLVES TO 1.0, THE NON-RETINA IDENTITY, which is the direction that fails
        /// safe. <c>objc_msgSend</c> to nil answers zero, so a handle that is not an <c>NSWindow</c> reports a
        /// scale of 0 rather than raising, and a scale of zero applied faithfully would configure a layer at
        /// nothing at all on a window whose points are perfectly readable. Falling back to the unscaled size
        /// reproduces exactly the incumbent's behaviour in the one case where this backend cannot do better,
        /// which is the smallest available divergence rather than a new failure mode.
        /// </para>
        /// </summary>
        internal static MetalDrawableSize SizeOfHostView(CGRect frame, double backingScale)
        {
            double scale = UsableScale(backingScale);
            return new(ToPixels(frame.Size.Width, scale), ToPixels(frame.Size.Height, scale));
        }

        /// <summary>The scale to actually multiply by: <paramref name="backingScale"/> when it is a real positive
        /// number, and 1.0 otherwise. Separate from <see cref="ToPixels"/> so the fallback is decided ONCE per
        /// resolve rather than once per dimension, which is what stops a pathological scale producing a drawable
        /// with two different aspect ratios in it.</summary>
        internal static double UsableScale(double backingScale)
            => double.IsNaN(backingScale) || double.IsInfinity(backingScale) || backingScale <= 0d
                ? 1d
                : backingScale;

        static uint ToPixels(double points, double scale)
        {
            if (double.IsNaN(points) || points <= 0d) return 0u;
            double pixels = points * scale;
            if (double.IsNaN(pixels) || pixels <= 0d) return 0u;
            if (pixels >= uint.MaxValue) return uint.MaxValue;
            return (uint)pixels;
        }
    }
}
