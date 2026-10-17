using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Windowing
{
    /// <summary>
    /// What a <see cref="IFrameCaptureTap"/> sees of one frame. The window's tap registry sets it up once per
    /// frame and every tap shares it, which is what lets one swapchain copy and one fence serve them all.
    /// </summary>
    public sealed class FrameCaptureContext
    {
        readonly FrameCaptureTaps _owner;
        bool _recording;
        IGpuFence? _fence;
        bool _imageAcquired;
        IGpuTexture? _image;

        internal FrameCaptureContext(FrameCaptureTaps owner, IGpuDevice device)
        {
            _owner = owner;
            Device = device;
            Frame = null!;
            Commands = null!;
        }

        /// <summary>The frame being captured.</summary>
        public Frame Frame { get; private set; }

        /// <summary>The window's GPU device.</summary>
        public IGpuDevice Device { get; }

        /// <summary>The frame's own command list. Open during <see cref="IFrameCaptureTap.Record"/> only.</summary>
        public IGpuCommandList Commands { get; private set; }

        /// <summary>The frame loop's delta in milliseconds, before the 100 ms clamp <see cref="Frame.Dt"/>
        /// carries, so a hitch reads at its real length.</summary>
        public double FrameMilliseconds { get; private set; }

        /// <summary>The fence a tap handed in this frame, or null.</summary>
        internal IGpuFence? Fence => _fence;

        /// <summary>
        /// A copy of the image this frame rendered, taken once per frame and shared by every tap that asks. It has
        /// the swapchain's width, height and colour format, one mip and <see cref="GpuTextureUsage.Sampled"/>
        /// usage, and the registry owns it: do not dispose it. Null when the device cannot copy its swapchain
        /// image this frame (the first frame after copy-source usage was requested, or a device with no
        /// swapchain capture).
        /// </summary>
        /// <exception cref="InvalidOperationException">Called outside <see cref="IFrameCaptureTap.Record"/>.
        /// </exception>
        public IGpuTexture? AcquireSwapchainImage()
        {
            RequireRecording(nameof(AcquireSwapchainImage));
            if (!_imageAcquired)
            {
                _imageAcquired = true;
                _image = _owner.CopySwapchainImage(Commands);
            }
            return _image;
        }

        /// <summary>
        /// Submit this frame's list with <paramref name="fence"/>, so it signals once the GPU has finished the
        /// frame, the copies a tap recorded included. One fence per frame across every tap.
        /// </summary>
        /// <exception cref="InvalidOperationException">Called outside <see cref="IFrameCaptureTap.Record"/>, or a
        /// fence was already handed in this frame.</exception>
        public void SignalOnSubmit(IGpuFence fence)
        {
            ArgumentNullException.ThrowIfNull(fence);
            RequireRecording(nameof(SignalOnSubmit));
            if (_fence is not null)
            {
                throw new InvalidOperationException(
                    "A frame carries one fence and a capture tap already handed one in this frame. Share the "
                    + "first tap's fence or signal on the next frame.");
            }
            _fence = fence;
        }

        /// <summary>Destroy <paramref name="resource"/> once the GPU has finished with it. Legal in any
        /// callback.</summary>
        public void Retire(IDisposable resource)
        {
            ArgumentNullException.ThrowIfNull(resource);
            _owner.Retire(resource);
        }

        internal void BeginRecord(Frame frame, IGpuCommandList commands, double frameMilliseconds)
        {
            Frame = frame;
            Commands = commands;
            FrameMilliseconds = frameMilliseconds;
            _fence = null;
            _imageAcquired = false;
            _image = null;
            _recording = true;
        }

        internal void EndRecord() => _recording = false;

        void RequireRecording(string member)
        {
            if (!_recording)
            {
                throw new InvalidOperationException(
                    member + " is legal only inside IFrameCaptureTap.Record, while the frame's list is recording.");
            }
        }
    }
}
