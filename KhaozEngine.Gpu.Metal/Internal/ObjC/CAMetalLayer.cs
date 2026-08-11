using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC
{
    /// <summary>
    /// A <c>CAMetalLayer</c> handle: the presentable surface a windowed native Metal device renders into, and the
    /// ONE object M-W1's field-for-field reproduction is about.
    ///
    /// <para><b>THE PROPERTY SET IS THE INCUMBENT'S, EXACTLY.</b> <c>MTLSwapchain</c>'s constructor writes
    /// <see cref="SetDevice"/>, <see cref="SetPixelFormat"/>, <see cref="SetFramebufferOnly"/> and
    /// <see cref="SetDrawableSize"/> and then <c>displaySyncEnabled</c>, and this type declares those five and
    /// nothing beyond them except <see cref="SetMaximumDrawableCount"/>, which is M-W4's one addition. Every
    /// setter has its getter beside it, because the swapchain has no automated coverage anywhere in the net (MM7)
    /// and a property that can be read back is a property a <c>[GpuFact]</c> can assert by VALUE.</para>
    ///
    /// <para><b>QUARTZCORE HAS TO BE LOADED BEFORE THE CLASS EXISTS.</b> <c>objc_getClass("CAMetalLayer")</c>
    /// answers nil in a process that has not loaded the framework, which a headless test host has not: the
    /// engine's own windowed path pulls it in through Cocoa long before this runs, and a <c>[GpuFact]</c> making a
    /// layer with no window does not. Row 1's spike found exactly that and loads it explicitly, so
    /// <see cref="TryGetClass"/> does the same, once per process.</para>
    ///
    /// <para><b><c>-nextDrawable</c> BLOCKS AND CANNOT BE ASKED NOT TO (M-W4).</b> There is no zero-timeout
    /// variant, no semaphore form and no "is one ready" query, which is the whole reason the acquire is TIMED at
    /// the call site rather than probed first the way the Vulkan sibling probes <c>vkAcquireNextImageKHR</c>. It
    /// also answers NIL rather than throwing when the layer has none to give, which is M-W5's whole subject.</para>
    ///
    /// <para><b>SIX PROPERTIES AND ONE ACQUIRE IS THE WHOLE SURFACE.</b> <c>allowsNextDrawableTimeout</c> is a
    /// pacing knob that belongs to https://github.com/APKiwiOrg/KhaozEngine/issues/380 with its own measurement
    /// and is deliberately absent (11.2), and <c>presentsWithTransaction</c>, <c>colorspace</c> and
    /// <c>wantsExtendedDynamicRangeContent</c> are all things the incumbent never writes, so writing one here
    /// would be a divergence wearing a feature's clothes.</para>
    /// </summary>
    /// <param name="Handle">The Objective-C object, or <see cref="IntPtr.Zero"/> for nil.</param>
    internal readonly record struct CAMetalLayer(IntPtr Handle)
    {
        /// <summary>True when there is no layer.</summary>
        internal bool IsNull => Handle == IntPtr.Zero;

        /// <summary>
        /// The <c>CAMetalLayer</c> class object, loading QuartzCore first if this process has not, or
        /// <see cref="IntPtr.Zero"/> when the framework will not load at all. Zero is a real answer rather than a
        /// failure, exactly as it is for <see cref="ObjCRuntime.ClassNamed"/>: a machine that cannot produce the
        /// class cannot have a windowed Metal device, and creation says so rather than sending a message to nil.
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static IntPtr TryGetClass()
        {
            // TryLoad rather than Load, and the result is deliberately unread: the class lookup below is the real
            // answer, and a framework that was already loaded (which is every windowed process) reports the same
            // handle rather than an error. NativeLibrary caches by path, so this is a dictionary hit after the
            // first call and needs no memo of its own.
            NativeLibrary.TryLoad(ObjCRuntime.QuartzCoreFramework, out _);
            return ObjCRuntime.ClassNamed("CAMetalLayer");
        }

        /// <summary>
        /// <c>[[CAMetalLayer alloc] init]</c>, at +1, or a null handle when the class is not there. The caller owns
        /// the reference and releases it exactly once.
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static CAMetalLayer New()
        {
            IntPtr cls = TryGetClass();
            if (cls == IntPtr.Zero) return new CAMetalLayer(IntPtr.Zero);

            IntPtr allocated = ObjCMsgSend.Send(cls, ObjCRuntime.Sel("alloc"));
            if (allocated == IntPtr.Zero) return new CAMetalLayer(IntPtr.Zero);

            return new CAMetalLayer(ObjCMsgSend.Send(allocated, ObjCRuntime.Sel("init")));
        }

        /// <summary>
        /// Whether <paramref name="layer"/> IS a <c>CAMetalLayer</c>, which is the adopt half of the incumbent's
        /// adopt-or-create dance: a view that already has one keeps it, and only a view with some other layer
        /// (or none) gets a fresh one attached.
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool IsMetalLayer(IntPtr layer)
        {
            if (layer == IntPtr.Zero) return false;

            IntPtr cls = TryGetClass();
            if (cls == IntPtr.Zero) return false;

            return ObjCMsgSend.SendBoolPtr(layer, ObjCRuntime.Sel("isKindOfClass:"), cls) != 0;
        }

        /// <summary>Retain this layer, so the swapchain's own reference is balanced whether it CREATED the layer
        /// or ADOPTED the host view's. See <c>MetalSwapchainApi</c> for why the incumbent's unconditional release
        /// is not reproduced.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Retain()
        {
            if (Handle != IntPtr.Zero) ObjCRuntime.ObjcRetain(Handle);
        }

        /// <summary>Release a reference this swapchain owned.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Release()
        {
            if (Handle != IntPtr.Zero) ObjCRuntime.ObjcRelease(Handle);
        }

        /// <summary><c>-setDevice:</c>. Which <c>MTLDevice</c> the layer vends drawables for, and the one property
        /// that has to agree with the device the frame was recorded on.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void SetDevice(IntPtr device)
            => ObjCMsgSend.SendVoidPtr(Handle, ObjCRuntime.Sel("setDevice:"), device);

        /// <summary><c>-device</c>.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal IntPtr Device() => ObjCMsgSend.Send(Handle, ObjCRuntime.Sel("device"));

        /// <summary><c>-setPixelFormat:</c>, an <c>NSUInteger</c> like every other Metal enum.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void SetPixelFormat(MTLPixelFormat format)
            => ObjCMsgSend.SendVoidNUInt(Handle, ObjCRuntime.Sel("setPixelFormat:"), (nuint)format);

        /// <summary><c>-pixelFormat</c>.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal MTLPixelFormat PixelFormat()
            => (MTLPixelFormat)ObjCMsgSend.SendNUInt(Handle, ObjCRuntime.Sel("pixelFormat"));

        /// <summary><c>-setFramebufferOnly:</c>, which the incumbent sets true: a drawable's texture is an
        /// attachment and never a sampling or copy source, which is what lets the driver pick the cheapest
        /// layout for it.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void SetFramebufferOnly(bool value)
            => ObjCMsgSend.SendVoidBool(Handle, ObjCRuntime.Sel("setFramebufferOnly:"), value ? (byte)1 : (byte)0);

        /// <summary><c>-framebufferOnly</c>.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal bool FramebufferOnly()
            => ObjCMsgSend.SendBool(Handle, ObjCRuntime.Sel("framebufferOnly")) != 0;

        /// <summary><c>-setDrawableSize:</c>, in PIXELS. The whole of what a resize does on this API (M-W7): there
        /// is no swapchain object to recreate.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void SetDrawableSize(CGSize size)
            => ObjCMsgSend.SendVoidCGSize(Handle, ObjCRuntime.Sel("setDrawableSize:"), size);

        /// <summary><c>-drawableSize</c>.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal CGSize DrawableSize() => ObjCMsgSend.SendCGSize(Handle, ObjCRuntime.Sel("drawableSize"));

        /// <summary>
        /// <c>-setDisplaySyncEnabled:</c>, written UNCONDITIONALLY (M-W2).
        /// <para>
        /// THE INCUMBENT WRITES IT ONLY INSIDE THREE VALUES OF A DEPRECATED ENUM, so on a machine outside that set
        /// a vsync toggle silently does nothing (2.9). This is a <c>CAMetalLayer</c> property on a macOS-only
        /// backend and needs no capability test at all.
        /// </para>
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void SetDisplaySyncEnabled(bool value)
            => ObjCMsgSend.SendVoidBool(Handle, ObjCRuntime.Sel("setDisplaySyncEnabled:"),
                value ? (byte)1 : (byte)0);

        /// <summary><c>-displaySyncEnabled</c>.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal bool DisplaySyncEnabled()
            => ObjCMsgSend.SendBool(Handle, ObjCRuntime.Sel("displaySyncEnabled")) != 0;

        /// <summary>
        /// <c>-setMaximumDrawableCount:</c>, set to <c>KE_METAL_FRAMES_IN_FLIGHT</c> (M-W4), so the depth of the
        /// drawable queue and the depth of the uniform ring are one number. The incumbent never writes it and
        /// takes whatever the default is. Row 1's spike round-tripped it on a headless layer.
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void SetMaximumDrawableCount(int count)
            => ObjCMsgSend.SendVoidNUInt(Handle, ObjCRuntime.Sel("setMaximumDrawableCount:"), (nuint)count);

        /// <summary><c>-maximumDrawableCount</c>.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal int MaximumDrawableCount()
            => (int)ObjCMsgSend.SendNUInt(Handle, ObjCRuntime.Sel("maximumDrawableCount"));

        /// <summary>
        /// <c>-nextDrawable</c>: the next presentable drawable, AUTORELEASED, or nil when the layer has none to
        /// give. IT BLOCKS when every drawable is still in flight, with no timeout knob and no non-blocking
        /// variant, which is M-W4's whole finding: the stall is not removable and is measured instead.
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal CAMetalDrawable NextDrawable()
            => new(ObjCMsgSend.Send(Handle, ObjCRuntime.Sel("nextDrawable")));
    }
}
