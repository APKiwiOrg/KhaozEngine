using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Silk.NET.Vulkan;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE SURFACE SEAM AS A FAKE. Everything the swapchain policy reads comes from here, so the format choice,
    /// the present-mode ladder, the image count, the extent clamp and both of V-W2's departures are asserted with
    /// no Vulkan loader and no window, on macOS as well as on Linux.
    /// <para>
    /// It matters more here than anywhere else in this package, because the present path is the ONE area with
    /// ZERO automated coverage in CI on any leg (MV9): a headless Vulkan device enables no surface extension at
    /// all. What these fakes cover is the only automated coverage this row will ever have.
    /// </para>
    /// </summary>
    internal sealed class FakeVulkanSurfaceApi : IVulkanSurfaceApi
    {
        /// <summary>The handle every fake surface has, so an assertion about a destroy names a number a reader
        /// can find.</summary>
        internal const ulong Handle = 0x50FACE;

        /// <summary>A plausible desktop surface: a window that dictates its own size, offering the BGRA pair the
        /// shipped path asks for plus all three optional present modes.</summary>
        internal static VulkanSurfaceReport Desktop(uint width = 1280, uint height = 720) => new(
            MinImageCount: 2,
            MaxImageCount: 8,
            CurrentExtent: new VulkanExtent(width, height),
            MinExtent: new VulkanExtent(1, 1),
            MaxExtent: new VulkanExtent(16384, 16384),
            CurrentTransform: SurfaceTransformFlagsKHR.IdentityBitKhr,
            Formats: new[]
            {
                new VulkanSurfaceFormatPair(Format.B8G8R8A8Unorm, ColorSpaceKHR.SpaceSrgbNonlinearKhr),
                new VulkanSurfaceFormatPair(Format.R8G8B8A8Unorm, ColorSpaceKHR.SpaceSrgbNonlinearKhr),
            },
            PresentModes: new[]
            {
                PresentModeKHR.FifoKhr, PresentModeKHR.FifoRelaxedKhr, PresentModeKHR.MailboxKhr,
                PresentModeKHR.ImmediateKhr,
            });

        /// <summary>A MINIMISED window: every extent zero, which is what a surface reports while its window is
        /// iconified and which is the shape https://github.com/APKiwiOrg/KhaozEngine/issues/81 is about.</summary>
        internal static VulkanSurfaceReport Minimised() => Desktop() with
        {
            CurrentExtent = new VulkanExtent(0, 0),
            MinExtent = new VulkanExtent(0, 0),
            MaxExtent = new VulkanExtent(0, 0),
        };

        /// <summary>What <see cref="Query"/> answers. Settable mid-test, which is how a resize and a minimise are
        /// simulated.</summary>
        internal VulkanSurfaceReport Report { get; set; } = Desktop();

        /// <summary>How many times the boundary re-read the surface. Every recreate must, because a window that
        /// changed can change any of it.</summary>
        internal int Queries { get; private set; }

        /// <summary>Surfaces destroyed, in call order. A surface here twice is a double destroy.</summary>
        internal List<ulong> Destroyed { get; } = new();

        /// <summary>What <see cref="SupportsPresent"/> answers.</summary>
        internal bool PresentSupported { get; set; } = true;

        /// <inheritdoc/>
        public ulong CreateSurface(GpuWindowKind kind, IntPtr windowHandle, IntPtr displayHandle) => Handle;

        /// <inheritdoc/>
        public bool SupportsPresent(ulong surface, uint queueFamily) => PresentSupported;

        /// <inheritdoc/>
        public VulkanSurfaceReport Query(ulong surface)
        {
            Queries++;
            return Report;
        }

        /// <inheritdoc/>
        public void DestroySurface(ulong surface) => Destroyed.Add(surface);
    }

    /// <summary>
    /// THE SWAPCHAIN SEAM AS A FAKE, recording every call in order and letting a test SCRIPT what an acquire or a
    /// present comes back with. That is what makes the <c>OUT_OF_DATE</c> boundary of V-W4 testable at all: the
    /// four questions it answers are about what happens NEXT after a particular result, and no real driver can be
    /// asked to produce one on demand.
    /// </summary>
    internal sealed class FakeVulkanSwapchainApi : IVulkanSwapchainApi
    {
        readonly Queue<VulkanPresentOutcome> _acquireScript = new();
        readonly Queue<VulkanPresentOutcome> _presentScript = new();

        ulong _nextSwapchain = 0x100;
        ulong _nextView = 0x200;
        ulong _nextSemaphore = 0x300;
        uint _nextImage;

        /// <summary>Every call in order, as text. The device-free stand-in for a native call log, and the thing
        /// the retirement-order assertions read.</summary>
        internal List<string> Events { get; } = new();

        /// <summary>Swapchains still alive, in creation order.</summary>
        internal List<ulong> LiveSwapchains { get; } = new();

        /// <summary>Image views still alive. A non-empty list at teardown is a leak.</summary>
        internal List<ulong> LiveViews { get; } = new();

        /// <summary>Binary semaphores still alive. A non-empty list at teardown is a leak.</summary>
        internal List<ulong> LiveSemaphores { get; } = new();

        /// <summary>The semaphore handed to each acquire, in acquire order. THE list the V-F5 reuse-distance
        /// assertion reads.</summary>
        internal List<ulong> AcquireSemaphores { get; } = new();

        /// <summary>Every <c>(swapchain, imageIndex, waitSemaphore)</c> a present was asked for.</summary>
        internal List<(ulong Swapchain, uint Image, ulong Wait)> Presents { get; } = new();

        /// <summary>How many images every swapchain reports. The driver decides this, so the boundary must read
        /// it rather than assume the count it asked for.</summary>
        internal int ImageCount { get; set; } = 3;

        /// <summary>When set, the next <see cref="CreateSwapchain"/> answers 0 with this reason and then clears
        /// itself, which is the "creation failed at a creatable extent" path.</summary>
        internal string? FailNextCreate { get; set; }

        /// <summary>How many blocking acquires were made, which is what separates a probe that found an image
        /// waiting from one that had to stall the CPU.</summary>
        internal int BlockingAcquires { get; private set; }

        /// <summary>Script the next acquire outcomes, consumed in order. Anything past the end is
        /// <see cref="VulkanPresentOutcome.Success"/>.</summary>
        internal void ScriptAcquires(params VulkanPresentOutcome[] outcomes)
        {
            foreach (VulkanPresentOutcome outcome in outcomes) _acquireScript.Enqueue(outcome);
        }

        /// <summary>Script the next present outcomes, consumed in order.</summary>
        internal void ScriptPresents(params VulkanPresentOutcome[] outcomes)
        {
            foreach (VulkanPresentOutcome outcome in outcomes) _presentScript.Enqueue(outcome);
        }

        /// <inheritdoc/>
        public ulong CreateSwapchain(ulong surface, in VulkanSwapchainSpec spec, ulong oldSwapchain,
            out string? failure)
        {
            LastSpec = spec;
            LastOldSwapchain = oldSwapchain;

            if (FailNextCreate is not null)
            {
                failure = FailNextCreate;
                FailNextCreate = null;
                Events.Add("CreateSwapchain -> failed");
                return 0;
            }

            failure = null;
            ulong handle = _nextSwapchain++;
            LiveSwapchains.Add(handle);
            Events.Add($"CreateSwapchain({spec.Extent.Width}x{spec.Extent.Height},{spec.PresentMode}) -> {handle:x}");
            return handle;
        }

        /// <summary>The spec the last creation was asked for, so a test can assert the whole create-info at
        /// once.</summary>
        internal VulkanSwapchainSpec LastSpec { get; private set; }

        /// <summary>The <c>oldSwapchain</c> the last creation was handed, which must be the generation being
        /// replaced rather than 0 on every recreate after the first.</summary>
        internal ulong LastOldSwapchain { get; private set; }

        /// <inheritdoc/>
        public IReadOnlyList<ulong> GetImages(ulong swapchain)
        {
            var images = new ulong[ImageCount];
            for (int i = 0; i < ImageCount; i++) images[i] = (swapchain << 8) | (uint)i;
            return images;
        }

        /// <inheritdoc/>
        public void DestroySwapchain(ulong swapchain)
        {
            LiveSwapchains.Remove(swapchain);
            Events.Add($"DestroySwapchain({swapchain:x})");
        }

        /// <inheritdoc/>
        public ulong CreateImageView(ulong image, Format format)
        {
            ulong view = _nextView++;
            LiveViews.Add(view);
            Events.Add($"CreateImageView({image:x}) -> {view:x}");
            return view;
        }

        /// <inheritdoc/>
        public void DestroyImageView(ulong view)
        {
            LiveViews.Remove(view);
            Events.Add($"DestroyImageView({view:x})");
        }

        /// <inheritdoc/>
        public ulong CreateBinarySemaphore()
        {
            ulong semaphore = _nextSemaphore++;
            LiveSemaphores.Add(semaphore);
            Events.Add($"CreateBinarySemaphore -> {semaphore:x}");
            return semaphore;
        }

        /// <inheritdoc/>
        public void DestroySemaphore(ulong semaphore)
        {
            LiveSemaphores.Remove(semaphore);
            Events.Add($"DestroySemaphore({semaphore:x})");
        }

        /// <inheritdoc/>
        public VulkanPresentOutcome AcquireNextImage(ulong swapchain, ulong semaphore, bool blockUntilReady,
            out uint imageIndex)
        {
            if (blockUntilReady) BlockingAcquires++;
            else AcquireSemaphores.Add(semaphore);

            VulkanPresentOutcome outcome = Next(_acquireScript);
            imageIndex = outcome is VulkanPresentOutcome.Success or VulkanPresentOutcome.Suboptimal
                ? NextImageIndex()
                : 0;

            Events.Add($"Acquire({semaphore:x},{(blockUntilReady ? "block" : "probe")}) -> {outcome}");
            return outcome;
        }

        /// <inheritdoc/>
        public VulkanPresentOutcome AcquireNextImageStalling(ulong swapchain, out uint imageIndex)
        {
            BlockingAcquires++;
            AcquireSemaphores.Add(0);

            VulkanPresentOutcome outcome = Next(_acquireScript);
            imageIndex = outcome is VulkanPresentOutcome.Success or VulkanPresentOutcome.Suboptimal
                ? NextImageIndex()
                : 0;

            Events.Add($"AcquireStalling -> {outcome}");
            return outcome;
        }

        /// <inheritdoc/>
        public VulkanPresentOutcome Present(ulong swapchain, uint imageIndex, ulong waitSemaphore)
        {
            Presents.Add((swapchain, imageIndex, waitSemaphore));
            VulkanPresentOutcome outcome = Next(_presentScript);
            Events.Add($"Present({swapchain:x},{imageIndex},wait={waitSemaphore:x}) -> {outcome}");
            return outcome;
        }

        uint NextImageIndex()
        {
            uint index = _nextImage % (uint)ImageCount;
            _nextImage++;
            return index;
        }

        static VulkanPresentOutcome Next(Queue<VulkanPresentOutcome> script)
            => script.Count > 0 ? script.Dequeue() : VulkanPresentOutcome.Success;
    }

    /// <summary>
    /// THE ORPHAN TARGET AS A FAKE, so the imageless path of V-W4 can be asserted without a device, a memory
    /// allocator or a setup command buffer. What matters is that it is created only when it is needed, released
    /// only once a real image is bound again, and always at the extent the boundary clamped.
    /// </summary>
    internal sealed class FakeVulkanOrphanTarget : IVulkanOrphanTarget
    {
        ulong _nextView = 0x900;

        /// <summary>Every extent and format an orphan was ensured at, in call order.</summary>
        internal List<(VulkanExtent Extent, GpuPixelFormat Format)> Ensured { get; } = new();

        /// <summary>How many times a NEW target was actually made, as opposed to an ensure that found the
        /// existing one already the right shape.</summary>
        internal int Created { get; private set; }

        /// <summary>How many times the target was released.</summary>
        internal int Released { get; private set; }

        /// <summary>Whether a target currently exists.</summary>
        internal bool IsLive { get; private set; }

        VulkanExtent _extent;
        GpuPixelFormat _format;
        ulong _view;

        /// <inheritdoc/>
        public VulkanAttachment Ensure(VulkanExtent extent, GpuPixelFormat format)
        {
            Ensured.Add((extent, format));

            if (!IsLive || _extent != extent || _format != format)
            {
                Created++;
                IsLive = true;
                _extent = extent;
                _format = format;
                _view = _nextView++;
            }

            return new VulkanAttachment(_view, _view + 1, format, DepthStencil: false);
        }

        /// <inheritdoc/>
        public void Release()
        {
            if (!IsLive) return;

            Released++;
            IsLive = false;
        }
    }
}
