using System;
using System.Runtime.Versioning;
using Vortice.Direct3D11;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// <see cref="IGpuFramebuffer"/> over engine textures: the render target views and the depth-stencil view a
    /// pass binds, gathered from the attachments.
    /// <para>
    /// THIS TYPE CREATES NOTHING AND OWNS NOTHING, which is the visible consequence of decision X1. The incumbent
    /// builds a render target view per attachment inside the framebuffer, so the views' lifetime is the
    /// framebuffer's. Here every view already exists on the texture, made at texture creation, so a framebuffer is
    /// an aggregate of borrowed pointers and its disposal releases nothing. The textures outlive it and are
    /// disposed by whoever created them.
    /// </para>
    /// <para>
    /// Mip 0 and layer 0 are the whole story because <c>CreateFramebuffer</c> takes bare textures with no mip and
    /// no layer parameter. That is why the eager render target and depth views can be single objects rather than
    /// one per targetable slice, and it is the seam fact the four-view bound rests on.
    /// </para>
    /// <para>
    /// The swapchain's framebuffer is NOT this type, it is <see cref="D3D11SwapchainFramebuffer"/>. Its views are
    /// recreated on every resize while its wrapper identity has to stay stable (decision W2), so its attachments
    /// are mutable by nature and it is handed them by the swapchain rather than reading them off engine textures.
    /// The two have opposite lifetimes and only <see cref="IGpuFramebuffer"/> in common, which is why they are
    /// siblings rather than one type with a mode.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class D3D11Framebuffer : IGpuFramebuffer, ID3D11RenderTargetSurface
    {
        readonly ID3D11RenderTargetView[] _renderTargetViews;

        internal D3D11Framebuffer(D3D11Texture? depth, D3D11Texture[] colour)
        {
            ArgumentNullException.ThrowIfNull(colour);
            if (depth is null && colour.Length == 0)
            {
                throw new ArgumentException(
                    "A framebuffer needs at least one attachment. A pass with no target renders nowhere.",
                    nameof(colour));
            }

            DepthTexture = depth;
            ColourTextures = colour;

            D3D11Texture first = colour.Length > 0 ? colour[0] : depth!;
            Width = first.Width;
            Height = first.Height;

            _renderTargetViews = new ID3D11RenderTargetView[colour.Length];
            var formats = new GpuPixelFormat[colour.Length];
            for (int i = 0; i < colour.Length; i++)
            {
                RequireMatching(first, colour[i], "colour attachment " + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                _renderTargetViews[i] = colour[i].RenderTargetView ?? throw MissingView(
                    "colour attachment " + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    nameof(GpuTextureUsage.RenderTarget));
                formats[i] = colour[i].Format;
            }

            if (depth is not null)
            {
                RequireMatching(first, depth, "depth attachment");
                DepthStencilView = depth.DepthStencilView ?? throw MissingView(
                    "depth attachment", nameof(GpuTextureUsage.DepthStencil));
            }

            Outputs = new GpuOutputDescription(depth?.Format, formats).WithSampleCount((int)first.SampleCount);
        }

        /// <inheritdoc/>
        public GpuOutputDescription Outputs { get; }
        /// <inheritdoc/>
        public uint Width { get; }
        /// <inheritdoc/>
        public uint Height { get; }

        /// <summary>The colour attachment textures, in order. Borrowed, never owned.</summary>
        internal D3D11Texture[] ColourTextures { get; }
        /// <summary>The depth attachment texture, or null. Borrowed, never owned.</summary>
        internal D3D11Texture? DepthTexture { get; }

        /// <summary>The attachments' render target views, in order. Owned by the textures.</summary>
        internal ID3D11RenderTargetView[] RenderTargetViews => _renderTargetViews;
        /// <summary>The depth-stencil view, or null. Owned by the texture.</summary>
        internal ID3D11DepthStencilView? DepthStencilView { get; }

        /// <summary>True once disposed. Nothing native is released. See the type remarks.</summary>
        internal bool IsDisposed { get; private set; }

        // ---- ID3D11RenderTargetSurface: what the output merger binds, object-typed so one emitter path covers
        // this type AND the swapchain's, whose views are swapped underneath it on every resize ----

        /// <inheritdoc/>
        int ID3D11RenderTargetSurface.RenderTargetCount => _renderTargetViews.Length;

        /// <inheritdoc/>
        object ID3D11RenderTargetSurface.RenderTargetAt(int index) => _renderTargetViews[index];

        /// <inheritdoc/>
        object? ID3D11RenderTargetSurface.DepthStencil => DepthStencilView;

        public void Dispose() => IsDisposed = true;

        static void RequireMatching(D3D11Texture first, D3D11Texture attachment, string what)
        {
            if (attachment.Width == first.Width && attachment.Height == first.Height
                && attachment.SampleCount == first.SampleCount)
            {
                return;
            }

            throw new ArgumentException(
                $"Every framebuffer attachment must share one size and one sample count. The {what} is "
                + $"{attachment.Width}x{attachment.Height} at {attachment.SampleCount} samples against "
                + $"{first.Width}x{first.Height} at {first.SampleCount}.");
        }

        static ArgumentException MissingView(string what, string usage)
            => new($"The {what} has no view to bind, because its texture was not created with "
                + $"GpuTextureUsage.{usage}. Views follow from the declared usage at creation, so a texture that "
                + "did not ask to be a target never got one.");
    }
}
