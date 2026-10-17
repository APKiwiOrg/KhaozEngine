using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Windowing
{
    /// <summary>
    /// The window's registered <see cref="IFrameCaptureTap"/>s, driven by <see cref="FramePhases"/>. It owns the
    /// one swapchain image copy every tap shares and the retire queue tap resources die through, and it asks for
    /// swapchain copy-source usage only while a tap is registered.
    /// <para>
    /// The tap list is copy-on-write, so a tap that adds or removes a tap from inside a callback never disturbs
    /// the walk in progress. A frame walks the snapshot it took at <see cref="Record"/> for both callbacks, and
    /// skips any tap removed since, so a detached tap is never called again and a tap added mid-frame starts on
    /// the next frame.
    /// </para>
    /// </summary>
    internal sealed class FrameCaptureTaps : IDisposable
    {
        readonly IGpuDevice _device;
        readonly IGpuSwapchainCapture? _capture;
        readonly FrameCaptureContext _context;
        readonly Action<IDisposable> _retireAction;
        IFrameCaptureTap[] _taps = Array.Empty<IFrameCaptureTap>();
        IFrameCaptureTap[] _frameTaps = Array.Empty<IFrameCaptureTap>();
        GpuRetireQueue? _retire;
        IGpuTexture? _image;
        bool _disposed;

        internal FrameCaptureTaps(IGpuDevice device, IGpuSwapchainCapture? capture)
        {
            ArgumentNullException.ThrowIfNull(device);
            _device = device;
            _capture = capture;
            _context = new FrameCaptureContext(this, device);
            _retireAction = Retire;
        }

        /// <summary>Taps registered right now.</summary>
        internal int Count => _taps.Length;

        /// <summary>True while a tap is registered or a retired resource is still waiting on the GPU. False means
        /// a frame needs nothing from this registry and runs exactly as it would without one.</summary>
        internal bool HasWork => _taps.Length > 0 || _retire is { PendingCount: > 0 };

        /// <summary>Register <paramref name="tap"/>. A tap already registered is left as it is.</summary>
        internal void Add(IFrameCaptureTap tap)
        {
            ArgumentNullException.ThrowIfNull(tap);
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Array.IndexOf(_taps, tap) >= 0) return;

            var next = new IFrameCaptureTap[_taps.Length + 1];
            Array.Copy(_taps, next, _taps.Length);
            next[^1] = tap;
            _retire ??= GpuRetireQueue.Create(_device);
            bool first = _taps.Length == 0;
            _taps = next;
            if (first && _capture is not null) _capture.SwapchainCopySourceRequested = true;
        }

        /// <summary>Unregister <paramref name="tap"/> and call its <see cref="IFrameCaptureTap.Detach"/>. A tap
        /// not registered is ignored. Safe from inside a tap callback.</summary>
        internal void Remove(IFrameCaptureTap tap)
        {
            ArgumentNullException.ThrowIfNull(tap);
            int index = Array.IndexOf(_taps, tap);
            if (index < 0) return;

            var next = new IFrameCaptureTap[_taps.Length - 1];
            Array.Copy(_taps, 0, next, 0, index);
            Array.Copy(_taps, index + 1, next, index, _taps.Length - index - 1);
            _taps = next;
            tap.Detach(_retireAction);
            if (_taps.Length == 0) ReleaseSwapchainUse();
        }

        /// <summary>Run every tap's <see cref="IFrameCaptureTap.Record"/> inside the frame's open recording and
        /// return the fence one of them handed in, or null.</summary>
        internal IGpuFence? Record(Frame frame, IGpuCommandList commands, double frameMilliseconds)
        {
            _frameTaps = _taps;
            _context.BeginRecord(frame, commands, frameMilliseconds);
            try
            {
                foreach (IFrameCaptureTap tap in _frameTaps)
                {
                    if (Array.IndexOf(_taps, tap) >= 0) tap.Record(_context);
                }
            }
            finally { _context.EndRecord(); }
            return _context.Fence;
        }

        /// <summary>Run every tap's <see cref="IFrameCaptureTap.AfterSubmit"/>, then advance the retire queue.
        /// Nothing records during either.</summary>
        internal void AfterSubmit()
        {
            foreach (IFrameCaptureTap tap in _frameTaps)
            {
                if (Array.IndexOf(_taps, tap) >= 0) tap.AfterSubmit(_context);
            }
            _frameTaps = Array.Empty<IFrameCaptureTap>();
            _retire?.BeginFrame();
        }

        internal void Retire(IDisposable resource)
        {
            if (_retire is null) resource.Dispose();
            else _retire.Retire(resource);
        }

        /// <summary>Copy this frame's swapchain image into the shared texture, sized to the swapchain and remade
        /// when that size changes. Null when the device cannot copy it this frame.</summary>
        internal IGpuTexture? CopySwapchainImage(IGpuCommandList commands)
        {
            if (_capture is null || !_capture.SwapchainCopyable) return null;
            if (_device.SwapchainFramebuffer is not { } swapchain) return null;

            GpuPixelFormat format = swapchain.Outputs.Colour[0];
            if (_image is { } old
                && (old.Width != swapchain.Width || old.Height != swapchain.Height || old.Format != format))
            {
                Retire(old);
                _image = null;
            }
            _image ??= _device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                swapchain.Width, swapchain.Height, format, GpuTextureUsage.Sampled));
            _capture.CopySwapchainColor(commands, _image);
            return _image;
        }

        /// <summary>Detach every tap, then free everything retired. Window teardown, with nothing recording.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            IFrameCaptureTap[] taps = _taps;
            _taps = Array.Empty<IFrameCaptureTap>();
            foreach (IFrameCaptureTap tap in taps) tap.Detach(_retireAction);
            if (taps.Length > 0) ReleaseSwapchainUse();
            // Dispose is FlushAll followed by freeing the queue's own fence barrier.
            _retire?.Dispose();
            _retire = null;
        }

        void ReleaseSwapchainUse()
        {
            if (_image is { } image)
            {
                Retire(image);
                _image = null;
            }
            if (_capture is not null) _capture.SwapchainCopySourceRequested = false;
        }
    }
}
