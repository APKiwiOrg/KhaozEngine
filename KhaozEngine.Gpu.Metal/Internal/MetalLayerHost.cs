using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// A RESOLVED HOST: the <c>CAMetalLayer</c> a windowed device presents through, at +1 held by the caller, and
    /// the size the host view asked for.
    /// </summary>
    /// <param name="Layer">The layer, owned by whoever received this.</param>
    /// <param name="Size">The initial drawable size, in the incumbent's own units. See
    /// <see cref="MetalSwapchainPolicy"/> for why a view's POINTS become a drawable's PIXELS unchanged.</param>
    internal readonly record struct MetalSwapchainHost(CAMetalLayer Layer, MetalDrawableSize Size);

    /// <summary>
    /// THE ADOPT-OR-CREATE DANCE (M-W1), and the ONLY place this backend talks to Cocoa. It turns the
    /// <c>GpuWindowHandle</c> the windowing package built into a layer and a size, and everything downstream of it
    /// knows nothing about windows at all.
    ///
    /// <para><b>IT IS SPLIT OUT SO THE REST OF THE SWAPCHAIN NEEDS NO WINDOW.</b> A <c>[GpuFact]</c> on a headless
    /// runner cannot produce an <c>NSWindow</c> and CAN produce a <c>CAMetalLayer</c>, which row 1's spike already
    /// established. Keeping the window resolution in its own type is what lets
    /// <c>MetalGpuDevice.CreateForLayer</c> exist beside <c>CreateForWindow</c>, so the whole swapchain below this
    /// line is driven against a REAL layer on a real device in CI, and only these four selectors wait for a
    /// windowed playtest. That is as far as MM7 can be pushed and it is a long way further than zero.</para>
    ///
    /// <para><b>ONLY THE <c>NSWindow</c> SOURCE IS REPRODUCED.</b> The incumbent additionally accepts an
    /// <c>NSView</c> and a <c>UIView</c> source. <see cref="GpuWindowHandle"/> can express neither: <c>Cocoa</c>
    /// means an <c>NSWindow</c> at the one site that builds one, and there is no iOS head in this fleet, so a
    /// <c>UIView</c> arm would be a code path with no caller and no test on the one surface that already has
    /// none.</para>
    ///
    /// <para><b>THE OWNERSHIP IS THE SAME ON BOTH ARMS, deliberately.</b> A layer this method CREATES arrives at
    /// +1 from <c>alloc</c>/<c>init</c> and the host view retains it again when it is assigned, so the caller
    /// holds exactly one. A layer this method ADOPTS is retained here, so the caller holds exactly one of that
    /// too. That is what lets <see cref="MetalSwapchainApi.Dispose"/> release unconditionally where the
    /// incumbent's identical release is an over-release on the adopt path.</para>
    /// </summary>
    internal static class MetalLayerHost
    {
        /// <summary>
        /// Resolve <paramref name="window"/> into a layer and a size, or refuse by name. Called BEFORE any device
        /// exists, so a window this backend cannot present to costs nothing to find out about.
        /// </summary>
        /// <exception cref="ArgumentException">The handle is not a Cocoa <c>NSWindow</c> with a content
        /// view.</exception>
        /// <exception cref="InvalidOperationException">This macOS has no <c>CAMetalLayer</c> class, or would not
        /// make one.</exception>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static MetalSwapchainHost Resolve(in GpuWindowHandle window)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            if (window.Kind != GpuWindowKind.Cocoa) throw NotCocoa(window.Kind);
            if (window.Handle == IntPtr.Zero) throw NoHandle();

            NSView view = new NSWindow(window.Handle).ContentView();
            if (view.IsNull) throw NoContentView();

            MetalDrawableSize size = MetalSwapchainPolicy.SizeOfHostView(view.Frame());

            IntPtr existing = view.Layer();
            if (CAMetalLayer.IsMetalLayer(existing))
            {
                // ADOPTED. The view owns it, so the reference the caller is about to hold is taken here.
                var adopted = new CAMetalLayer(existing);
                adopted.Retain();
                return new MetalSwapchainHost(adopted, size);
            }

            CAMetalLayer created = CAMetalLayer.New();

            // THE DIAGNOSTIC IS READ HERE rather than inside the exception factory, because CA1416 reads a
            // platform guard AT THE CALL SITE and a macOS-only call made from an unguarded static factory is one
            // the analyzer cannot see the guard for. It is the same reason MetalGpuDevice.SubmitCore spells its
            // liveness read inline.
            if (created.IsNull) throw NoLayer(DescribeClass());

            // wantsLayer FIRST and then the assignment, which is the incumbent's order and AppKit's rule: a view
            // told to want a layer AFTER one was assigned makes its own and discards the assignment.
            view.SetWantsLayer(true);
            view.SetLayer(created.Handle);
            return new MetalSwapchainHost(created, size);
        }

        static ArgumentException NotCocoa(GpuWindowKind kind)
            => new("A native Metal swapchain can only be built on a Cocoa window handle, and this one is "
                + kind.ToString() + ". That is a wiring fault rather than a machine fact: the windowing package "
                + "reports GpuWindowKind.Cocoa on macOS and Metal exists nowhere else, so a handle of another "
                + "kind means the window and the backend were chosen by two different answers to what platform "
                + "this is.", nameof(kind));

        static ArgumentException NoHandle()
            => new("The Cocoa window handle a native Metal swapchain was asked to present to is null. The "
                + "windowing package reads the NSWindow off the platform window, so a null here means the window "
                + "had not been created, or had already been destroyed, when the device was asked for.",
                "window");

        static ArgumentException NoContentView()
            => new("The NSWindow a native Metal swapchain was asked to present to has no content view, so there "
                + "is nothing to attach a CAMetalLayer to. A window mid-teardown reports this, and so does a "
                + "handle that is a live Objective-C object but not an NSWindow.", "window");

        static InvalidOperationException NoLayer(string detail)
            => new("This macOS would not create a CAMetalLayer, on a machine whose Metal support probe answered "
                + "yes. The class is looked up after QuartzCore has been loaded explicitly (which is what a "
                + "process with no Cocoa window would otherwise be missing), so a failure here is the framework "
                + "itself rather than a load order: " + detail + ".");

        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static string DescribeClass()
        {
            IntPtr cls = CAMetalLayer.TryGetClass();
            return cls == IntPtr.Zero
                ? "the CAMetalLayer class is not registered in this process at all"
                : "the class is registered (at 0x" + cls.ToString("x", CultureInfo.InvariantCulture)
                    + ") and alloc/init answered nil";
        }
    }
}
