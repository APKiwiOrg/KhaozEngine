using System;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE SWAPCHAIN BACK BUFFER AS A COPY SOURCE. The back buffer is not a <see cref="D3D11Texture"/>, so a
    /// swapchain copy records an ordinary <c>CopyTexture</c> whose source is this stand-in, and the native
    /// emitter's <c>CopyTexture</c> resolves it at replay: the back buffer comes from the named render target
    /// view through <c>GetResource</c>, is copied with <c>CopyResource</c>, and the reference is released at once.
    /// <para>
    /// IT NAMES A VIEW, NOT A TEXTURE, AND HOLDS NO NATIVE REFERENCE. <c>ResizeBuffers</c> fails while any
    /// reference to a back buffer survives, so keeping one across frames would break every resize. A resize lands
    /// only at the present boundary (W3), after the submit that replays this copy, so the view named at record
    /// time is still the live one at replay.
    /// </para>
    /// <para>
    /// One per copy, with the size and format read when the copy is recorded. Owned by nothing: disposing it does
    /// nothing because it owns nothing.
    /// </para>
    /// </summary>
    internal sealed class D3D11BackBufferCopySource : IGpuTexture
    {
        D3D11BackBufferCopySource(object colourView, uint width, uint height, GpuPixelFormat format)
        {
            ColourView = colourView;
            Width = width;
            Height = height;
            Format = format;
        }

        /// <summary>The render target view over the back buffer this copy reads, typed <c>object</c> for the
        /// reason <see cref="D3D11SwapchainAttachments"/> gives. The native emitter casts it back.</summary>
        internal object ColourView { get; }

        /// <inheritdoc/>
        public uint Width { get; }

        /// <inheritdoc/>
        public uint Height { get; }

        /// <inheritdoc/>
        public uint MipLevels => 1;

        /// <inheritdoc/>
        public uint SampleCount => 1;

        /// <inheritdoc/>
        public GpuPixelFormat Format { get; }

        /// <summary>
        /// <c>IGpuSwapchainCapture.CopySwapchainColor</c> for this backend: refuse an uncopyable swapchain and a
        /// mismatched destination, then record the copy through the list's own <c>CopyTexture</c>, so replay
        /// issues <c>CopyResource</c> on the immediate context in recording order.
        /// </summary>
        /// <exception cref="InvalidOperationException">There is no swapchain, or its attachments are not
        /// live.</exception>
        /// <exception cref="ArgumentException"><paramref name="destination"/> does not match the back
        /// buffer.</exception>
        internal static void RecordCopy(D3D11Swapchain? swapchain, IGpuCommandList commands, IGpuTexture destination)
        {
            ArgumentNullException.ThrowIfNull(commands);
            ArgumentNullException.ThrowIfNull(destination);

            object view = swapchain?.CurrentColourView ?? throw new InvalidOperationException(
                "CopySwapchainColor was called while the swapchain is not copyable: the device has no swapchain, or "
                + "its back buffer views are released for a resize or for disposal. Check SwapchainCopyable "
                + "first.");

            D3D11SwapchainFramebuffer framebuffer = swapchain.Framebuffer;
            SwapchainCopyCheck.RequireDestination(framebuffer, destination);

            commands.CopyTexture(
                new D3D11BackBufferCopySource(view, framebuffer.Width, framebuffer.Height,
                    framebuffer.Outputs.Colour[0]),
                destination);
        }

        /// <summary>Nothing to release. The stand-in owns no native object.</summary>
        public void Dispose()
        {
        }
    }
}
