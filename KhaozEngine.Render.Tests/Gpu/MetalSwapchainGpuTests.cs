using System;
using System.Runtime.Versioning;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Metal.Internal.ObjC;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE SWAPCHAIN AGAINST A REAL <c>CAMetalLayer</c> ON A REAL DEVICE, WITH NO WINDOW. Work-breakdown row 15
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/581).
    ///
    /// <para><b>THIS IS AS FAR AS MM7 CAN BE PUSHED, AND IT IS FURTHER THAN THE DESIGN EXPECTED.</b> The design
    /// records that not one line of <c>MTLSwapchain</c>, <c>MTLSwapchainFramebuffer</c>, <c>nextDrawable</c> or
    /// <c>presentDrawable</c> runs in CI on any leg, ever, and reasons about the swapchain as an area with zero
    /// automated coverage. What that argument assumes is that presentation needs a window. It does not: a
    /// <c>CAMetalLayer</c> can be created, configured and asked for drawables with no <c>NSWindow</c>, no view and
    /// no display server, which row 1's spike had already half-established when it round-tripped
    /// <c>maximumDrawableCount</c> on a headless layer. So the ONLY part of this row that has to wait for a
    /// windowed playtest is <see cref="MetalLayerHost"/>'s six Cocoa selectors, and everything from the layer
    /// down runs here.</para>
    ///
    /// <para><b>WHAT A HEADLESS LAYER DOES NOT PROVE is that anything appears on a screen.</b> These rows are
    /// evidence about the API surface, the lifetimes and the counters, not about presentation, and a green run
    /// here says nothing about whether the window is the right size or whether vsync paces the frame loop. Those
    /// are gate 5's, on a real window, by a human.</para>
    ///
    /// <para><b>IT SITS IN <c>NativeDeviceLifecycle</c></b> because two of its rows build a whole device beside the
    /// suite's own.</para>
    /// </summary>
    [Collection("NativeDeviceLifecycle")]
    public sealed class MetalSwapchainGpuTests
    {
        readonly ITestOutputHelper _out;
        public MetalSwapchainGpuTests(ITestOutputHelper o) => _out = o;

        /// <summary>
        /// M-W1's CONFIGURATION READ BACK OFF THE LAYER BY VALUE, which is the only way this surface can be
        /// checked at all: every one of these is a property a human sees the effect of and nothing else does.
        /// M-W4's <c>maximumDrawableCount</c> and M-W2's unconditional <c>displaySyncEnabled</c> are the two that
        /// are NOT the incumbent's, and both are here for the same reason.
        /// </summary>
        [GpuFact]
        public void TheLayerIsConfiguredFieldForFieldAndReadsBack()
        {
            if (!MetalDormancy.NativeDeviceAvailable(_out)) return;

            using GpuDeviceContext context = GpuDeviceContext.CreateHeadless(GpuBackendKind.MetalNative);
            var device = (MetalGpuDevice)context.GpuDevice;

            ReadConfigurationBack(device, _out);
        }

        /// <summary>
        /// A DRAWABLE OFF A LAYER WITH NO WINDOW, WITH A REAL TEXTURE BEHIND IT, PRESENTED AND RELEASED. This is
        /// the row that establishes the premise the whole file rests on, so it asserts each half separately rather
        /// than as a sequence that could pass by not reaching the interesting part.
        /// </summary>
        [GpuFact]
        public void AHeadlessLayerVendsDrawablesAndPresentsThem()
        {
            if (!MetalDormancy.NativeDeviceAvailable(_out)) return;

            using GpuDeviceContext context = GpuDeviceContext.CreateHeadless(GpuBackendKind.MetalNative);
            var device = (MetalGpuDevice)context.GpuDevice;

            AcquireAndPresentOnce(device, _out);
        }

        /// <summary>
        /// THE WHOLE BOUNDARY ON A REAL DEVICE: presents, acquires, a resize applied after a drain, and the
        /// counters moving. Driven through a device built over a layer, so the counter fill is read exactly as a
        /// windowed device's would be.
        /// <para>
        /// <b>THE THREE PRESENT-BOUNDARY FIELDS ARE DRIVEN OFF ZERO BEFORE THEIR IDENTITIES ARE ASSERTED</b>,
        /// which is row 16's correction: an identity assertion between two zeros holds whichever accumulator the
        /// field is wired to, and a transposed pair is the single failure a counter fill can have.
        /// </para>
        /// </summary>
        [GpuFact]
        public void ThePresentBoundaryDrivesARealLayerAndFillsTheCounters()
        {
            if (!MetalDormancy.NativeDeviceAvailable(_out)) return;

            DriveALayerBackedDevice(_out);
        }

        // ---- The macOS-only bodies ----------------------------------------------------------------------------
        //
        // SPLIT OUT UNDER THE GUARD for the reason every Metal row in this assembly splits: CA1416 reads
        // KhaozEngineMetal.IsPlatformSupported AT THE CALL SITE, and a helper returning a value carries no guard
        // the analyzer can see.

        [SupportedOSPlatform("macos")]
        static void ReadConfigurationBack(MetalGpuDevice device, ITestOutputHelper output)
        {
            CAMetalLayer layer = NewLayer(output);
            if (layer.IsNull) return;

            try
            {
                var api = new MetalSwapchainApi(layer, device.Handle, new MetalCommandBufferSource(device.Queue));
                api.Configure(new MetalDrawableSize(320u, 200u), MetalSwapchainPolicy.ColourSrgbRequested,
                    syncToVerticalBlank: true, maximumDrawableCount: 3);

                Assert.Equal(device.Handle.Handle, layer.Device());
                Assert.Equal(MTLPixelFormat.BGRA8Unorm, layer.PixelFormat());
                Assert.True(layer.FramebufferOnly());
                Assert.Equal(320d, layer.DrawableSize().Width);
                Assert.Equal(200d, layer.DrawableSize().Height);
                Assert.True(layer.DisplaySyncEnabled());
                Assert.Equal(3, layer.MaximumDrawableCount());

                // M-W2: THE WRITE IS UNCONDITIONAL, so it toggles on a machine whatever its feature set. The
                // incumbent writes it only inside three values of an enum deprecated since macOS 10.15, and this
                // machine's own answer to that enum is not consulted anywhere in this backend.
                api.SetDisplaySyncEnabled(false);
                Assert.False(layer.DisplaySyncEnabled());
                api.SetDisplaySyncEnabled(true);
                Assert.True(layer.DisplaySyncEnabled());

                // AND THE CGSize CROSSES CORRECTLY IN BOTH DIRECTIONS, which is the one arm64 register-class
                // question this row adds and the only place it can be answered by VALUE.
                api.SetDrawableSize(new MetalDrawableSize(1280u, 720u));
                Assert.Equal(1280d, layer.DrawableSize().Width);
                Assert.Equal(720d, layer.DrawableSize().Height);

                output.WriteLine("headless CAMetalLayer configured and read back: "
                    + $"{layer.DrawableSize().Width}x{layer.DrawableSize().Height}, "
                    + $"maximumDrawableCount={layer.MaximumDrawableCount()}");

                api.Dispose();
            }
            catch
            {
                layer.Release();
                throw;
            }
        }

        [SupportedOSPlatform("macos")]
        static void AcquireAndPresentOnce(MetalGpuDevice device, ITestOutputHelper output)
        {
            CAMetalLayer layer = NewLayer(output);
            if (layer.IsNull) return;

            var api = new MetalSwapchainApi(layer, device.Handle, new MetalCommandBufferSource(device.Queue));

            try
            {
                api.Configure(new MetalDrawableSize(64u, 64u), MetalSwapchainPolicy.ColourSrgbRequested,
                    syncToVerticalBlank: false, maximumDrawableCount: 3);

                MetalAcquiredDrawable acquired = api.NextDrawable();

                Assert.True(acquired.HasDrawable,
                    "a CAMetalLayer with a device, a format and a non-zero drawable size vended no drawable with "
                    + "no window attached. That is the premise every other row in this file rests on, and if it "
                    + "stops holding the whole layer half of row 15 goes back to being windowed-playtest-only.");

                Assert.NotEqual(IntPtr.Zero, acquired.Texture);

                // THE TEXTURE IS THE DRAWABLE's AND IS BORROWED FROM IT, which is what makes the framebuffer's
                // colour attachment safe to bind for a whole recording: the retain the acquire took is the only
                // thing keeping it alive.
                IntPtr presentBuffer = api.AcquirePresentBuffer();
                Assert.NotEqual(IntPtr.Zero, presentBuffer);

                api.PresentDrawable(presentBuffer, acquired.Drawable);
                api.ReleaseDrawable(acquired.Drawable);

                // A SECOND ACQUIRE AFTER A PRESENT, which is what a frame loop does and what would block forever
                // if the drawable pool were not being returned to.
                MetalAcquiredDrawable second = api.NextDrawable();
                Assert.True(second.HasDrawable);
                api.ReleaseDrawable(second.Drawable);

                output.WriteLine("headless CAMetalLayer vended two drawables and presented one.");
            }
            finally
            {
                api.Dispose();
            }
        }

        [SupportedOSPlatform("macos")]
        static void DriveALayerBackedDevice(ITestOutputHelper output)
        {
            CAMetalLayer layer = NewLayer(output);
            if (layer.IsNull) return;

            // OWNERSHIP TRANSFERS INTO THE DEVICE, on both the success and the failure path, which is what
            // CreateForHost documents. So there is no release of the layer anywhere below.
            GpuProviderDevice created = MetalGpuDevice.CreateForHost(
                new MetalSwapchainHost(layer, new MetalDrawableSize(256u, 144u)), syncToVerticalBlank: false);

            var device = (MetalGpuDevice)created.Device;

            try
            {
                MetalPresentBoundary boundary = device.PresentBoundary!;
                Assert.NotNull(device.SwapchainFramebuffer);
                Assert.Same(boundary.Framebuffer, device.SwapchainFramebuffer);

                // THE THREE FIELDS ARE ZERO BEFORE ANY FRAME AND THE ACQUIRE PAIR IS NOT, because the first
                // drawable is taken at CREATION. That offset is the fact M-W4's timing produces.
                GpuDeviceCounters atRest = device.Counters;
                Assert.Equal(0L, atRest.FramesBegun);
                Assert.Equal(1L, atRest.AcquireWaitCount);

                for (int i = 0; i < 8; i++) device.Present();

                // A RESIZE, APPLIED AT A BOUNDARY AFTER A DRAIN. Queued from here (which is where a window
                // callback would queue it) and applied by the next present.
                device.ResizeSwapchain(128u, 96u);
                Assert.True(boundary.HasPendingWork);
                device.Present();

                Assert.False(boundary.HasPendingWork);
                Assert.Equal(new MetalDrawableSize(128u, 96u), boundary.Size);
                Assert.Equal(128u, device.SwapchainFramebuffer!.Width);
                Assert.Equal(96u, device.SwapchainFramebuffer!.Height);
                Assert.Equal(128d, layer.DrawableSize().Width);

                // A VSYNC FLIP, likewise queued and applied at the next boundary, written unconditionally (M-W2).
                device.SyncToVerticalBlank = true;
                device.Present();
                Assert.True(layer.DisplaySyncEnabled());

                GpuDeviceCounters counters = device.Counters;

                // DRIVEN OFF ZERO FIRST, which is row 16's correction: an identity between two zeros holds
                // whichever accumulator the field is wired to.
                Assert.True(counters.FramesBegun > 0L,
                    "ten presents left FramesBegun at zero, so the identity assertion below cannot tell this "
                    + "field from any other zero.");
                Assert.True(counters.AcquireWaitCount > 0L, "the acquire pair never recorded an acquire.");

                // THEN THE IDENTITIES, each field against the accumulator section 14 names as its source.
                Assert.Equal(boundary.FramesBegun, counters.FramesBegun);
                Assert.Equal(device.AcquireTotals.Count, counters.AcquireWaitCount);
                Assert.Equal(device.AcquireTotals.TotalMs, counters.AcquireWaitMs);

                // AND THE OFFSET IS EXACTLY ONE, which is the whole of M-W4's timing stated as an equation.
                Assert.Equal(counters.FramesBegun + 1L, counters.AcquireWaitCount);

                // THE PRESENT BUFFER OCCUPIED THE UNCOMMITTED BOUND'S PLUS ONE, which had no occupant before this
                // row and is what makes section 6.1's bound tight.
                Assert.True(device.Uncommitted.Peak >= 1,
                    "ten presents never held a command buffer, so M-W6's present buffer is not being counted.");
                Assert.False(device.Uncommitted.ExceededBound);

                output.WriteLine($"layer-backed device: framesBegun={counters.FramesBegun}, "
                    + $"acquireWaits={counters.AcquireWaitCount} in "
                    + counters.AcquireWaitMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                    + $" ms, skippedPresents={boundary.SkippedPresents}, "
                    + $"uncommittedPeak={device.Uncommitted.Peak}/{device.Uncommitted.Bound}, "
                    + $"drains={counters.DrainCount}");
            }
            finally
            {
                device.Dispose();
            }
        }

        // A CAMetalLayer with no window, no view and no display server. Row 1's spike established that this
        // works and this is the first thing in the backend to depend on it, so a machine that cannot make one
        // says so and the row goes dormant rather than failing.
        [SupportedOSPlatform("macos")]
        static CAMetalLayer NewLayer(ITestOutputHelper output)
        {
            CAMetalLayer layer = CAMetalLayer.New();
            if (!layer.IsNull) return layer;

            output.WriteLine("dormant: this macOS would not create a CAMetalLayer at all, so the layer half of "
                + "the swapchain cannot be driven here.");
            return layer;
        }
    }
}
