using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A swapchain's native half with no DXGI behind it, so the engine half (when a resize is applied, which size
    /// wins, whether the views are released before the buffers are resized, what the sync interval is, whether
    /// the framebuffer keeps its identity) is driven by plain <c>[Fact]</c>s on macOS and Linux as well as
    /// Windows.
    /// <para>
    /// This is why <see cref="ID3D11SwapchainSurface"/> is an interface at all. What is left behind it on the real
    /// path is <c>ResizeBuffers</c>, <c>GetBuffer</c> plus the view creations, the releases and <c>Present</c>,
    /// and everything that could be wrong about a swapchain sits above it.
    /// </para>
    /// <para>
    /// IT REFUSES A RESIZE WITH ATTACHMENTS OUTSTANDING, BY NAME, because that is the one ordering rule the whole
    /// three-member split exists to make testable: <c>IDXGISwapChain::ResizeBuffers</c> fails while any reference
    /// to a backbuffer survives, and the incumbent's silent dependence on releasing first is what a device-free
    /// test can otherwise never see. It refuses a double create for the same reason in the other direction, since
    /// that leaks a view the runtime never gets back.
    /// </para>
    /// </summary>
    internal sealed class FakeD3D11SwapchainSurface : ID3D11SwapchainSurface
    {
        readonly List<FakeSwapchainCall> _calls = new();

        internal FakeD3D11SwapchainSurface(uint width, uint height, GpuPixelFormat? depthFormat = null)
        {
            BackbufferWidth = width;
            BackbufferHeight = height;
            DepthFormat = depthFormat;
        }

        /// <summary>Every call made on this surface, in order.</summary>
        internal IReadOnlyList<FakeSwapchainCall> Calls => _calls;

        /// <summary>The calls as one string each, which is the shape an order assertion reads best in.</summary>
        internal string[] Trace => _calls.Select(c => c.ToString()).ToArray();

        /// <summary>
        /// What the backbuffer's real size is, which a resize sets and a test may set by hand FIRST to model
        /// DXGI reading a zero request as "match the window's client area". This is what
        /// <see cref="CreateAttachments"/> reports, never the requested size.
        /// </summary>
        internal uint BackbufferWidth { get; set; }

        /// <summary>The other half of <see cref="BackbufferWidth"/>.</summary>
        internal uint BackbufferHeight { get; set; }

        /// <summary>When true, a resize to a zero width or height leaves the backbuffer size alone, which is what
        /// DXGI does. Off by default so a test says what it means.</summary>
        internal bool ZeroMeansKeepTheWindowSize { get; set; }

        /// <summary>The <c>HRESULT</c> <see cref="Present"/> returns. Zero (S_OK) unless a test sets a failure,
        /// which is how the device-loss shape of decision G3 is exercised without a device to lose.</summary>
        internal int PresentResult { get; set; }

        /// <summary>The submit lock the swapchain under test was built with, so the fake can record whether each
        /// call arrived holding it. Left null by a test that does not care.</summary>
        internal object? SubmitLock { get; set; }

        /// <summary>Whether a generation of views is outstanding right now.</summary>
        internal bool AttachmentsOutstanding { get; private set; }

        /// <summary>How many generations of attachments have been created.</summary>
        internal int CreateCount { get; private set; }

        /// <summary>How many times the surface itself was disposed.</summary>
        internal int DisposeCount { get; private set; }

        /// <summary>The render target view handed out by the last <see cref="CreateAttachments"/>, so a test can
        /// say the views were swapped rather than merely that a resize happened.</summary>
        internal object? LastRenderTargetView { get; private set; }

        /// <summary>The depth-stencil view handed out by the last <see cref="CreateAttachments"/>, or null when
        /// the surface carries no depth attachment.</summary>
        internal object? LastDepthStencilView { get; private set; }

        /// <inheritdoc/>
        public GpuPixelFormat ColourFormat { get; init; } = GpuPixelFormat.B8G8R8A8UNorm;

        /// <inheritdoc/>
        public GpuPixelFormat? DepthFormat { get; }

        /// <inheritdoc/>
        public void ReleaseAttachments()
        {
            Record("ReleaseAttachments", "");
            AttachmentsOutstanding = false;
        }

        /// <inheritdoc/>
        public void ResizeBuffers(uint width, uint height)
        {
            if (AttachmentsOutstanding)
                throw new InvalidOperationException(
                    "A fake swapchain surface was asked to resize its buffers while a generation of views over "
                    + "backbuffer 0 was still outstanding. IDXGISwapChain::ResizeBuffers fails in exactly that "
                    + "case, and the window is then left presenting buffers that no longer match it, so the "
                    + "release has to come first.");

            Record("ResizeBuffers", Size(width, height));
            if (ZeroMeansKeepTheWindowSize && (width == 0 || height == 0)) return;

            BackbufferWidth = width;
            BackbufferHeight = height;
        }

        /// <inheritdoc/>
        public D3D11SwapchainAttachments CreateAttachments(uint width, uint height)
        {
            if (AttachmentsOutstanding)
                throw new InvalidOperationException(
                    "A fake swapchain surface was asked for a second generation of views while the first was "
                    + "still outstanding. In production that leaks a render target view over a backbuffer the "
                    + "runtime never gets back, and the next ResizeBuffers fails because of it.");

            Record("CreateAttachments", Size(width, height));
            AttachmentsOutstanding = true;
            CreateCount++;
            LastRenderTargetView = new object();
            LastDepthStencilView = DepthFormat is null ? null : new object();

            return new D3D11SwapchainAttachments(
                BackbufferWidth, BackbufferHeight, LastRenderTargetView, LastDepthStencilView);
        }

        /// <inheritdoc/>
        public int Present(int syncInterval)
        {
            Record("Present", syncInterval.ToString(CultureInfo.InvariantCulture));
            return PresentResult;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            ReleaseAttachments();
            Record("Dispose", "");
            DisposeCount++;
        }

        void Record(string name, string detail)
            => _calls.Add(new FakeSwapchainCall(name, detail,
                SubmitLock is object submitLock && Monitor.IsEntered(submitLock)));

        static string Size(uint width, uint height)
            => width.ToString(CultureInfo.InvariantCulture) + "x" + height.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>One call on a <see cref="FakeD3D11SwapchainSurface"/>: what it was, its arguments, and whether the
    /// caller held the submit lock when it arrived. The lock answer is the assertion decision W4 turns on, since
    /// present and the resize apply both owe it and the queue must not take it.</summary>
    /// <param name="Name">The member called.</param>
    /// <param name="Detail">Its arguments, formatted for a readable order assertion.</param>
    /// <param name="HeldTheSubmitLock">Whether the submit lock was held. Always false when the test did not wire
    /// one up, so a test that cares has to say so.</param>
    internal readonly record struct FakeSwapchainCall(string Name, string Detail, bool HeldTheSubmitLock)
    {
        /// <inheritdoc/>
        public override string ToString() => Detail.Length == 0 ? Name : Name + " " + Detail;
    }
}
