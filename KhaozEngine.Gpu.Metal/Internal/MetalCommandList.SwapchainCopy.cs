using System;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE SWAPCHAIN COPY: a whole copy of the held drawable's texture into an engine texture, recorded on the
    /// same blit encoder <see cref="CopyTexture"/> uses. The drawable's texture is not a <see cref="MetalTexture"/>,
    /// so it arrives as a raw <c>MTLTexture</c> handle. The command buffer retains it until the copy completes, so
    /// the present boundary releasing the drawable after the submit is safe.
    /// </summary>
    internal sealed partial class MetalCommandList
    {
        /// <summary>
        /// Record a whole copy of <paramref name="drawableTexture"/> into <paramref name="dst"/>. The caller has
        /// already proved the drawable copyable and the destination's shape equal to the swapchain image's.
        /// </summary>
        /// <param name="drawableTexture">The held drawable's <c>MTLTexture</c>, vended with
        /// <c>framebufferOnly</c> false.</param>
        /// <param name="dst">A texture of this device, Private or staging.</param>
        internal void CopyFromDrawable(IntPtr drawableTexture, IGpuTexture dst)
        {
            // M-N5's one pool for this member.
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(dst);

            MetalTexture destination = MetalResourceOwnership.Require<MetalTexture>(dst, _liveness, nameof(dst));
            RequireRecording("Copying the swapchain image");

            IntPtr encoder = _encoders.EnsureBlitEncoder();
            if (encoder == IntPtr.Zero || drawableTexture == IntPtr.Zero) return;

            if (destination.IsStaging)
            {
                _blit.CopyTextureToBuffer(encoder, drawableTexture, destination.StagingBuffer.Handle,
                    MetalTransferPlan.ReadbackRegion(destination.Shape, 0, 0, 0, 0, destination.Width,
                        destination.Height));
                return;
            }

            _blit.CopyTextureToTexture(encoder, drawableTexture, destination.Handle.Handle,
                MetalTransferPlan.TextureRegion(0, 0, 0, 0, destination.Width, destination.Height));
        }
    }
}
