using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// An <see cref="IGpuTexture"/> that forwards everything and tells its device when it is created and when it
    /// is freed, so a test can ask the question a leak asks: did the call that threw walk past a texture nobody
    /// owns? Handed out by <see cref="OpenListTrackingGpuDevice"/>'s factory.
    /// <para>
    /// Disposal counts ONCE however many times it is called, so a double-dispose reads as one freed texture rather
    /// than as a negative leak that hides a real one.
    /// </para>
    /// </summary>
    internal sealed class CountingGpuTexture : IGpuTexture
    {
        readonly IGpuTexture _inner;
        readonly OpenListTrackingGpuDevice _device;
        bool _disposed;

        internal CountingGpuTexture(IGpuTexture inner, OpenListTrackingGpuDevice device)
        {
            _inner = inner;
            _device = device;
            device.NoteTextureCreated();
        }

        public uint Width => _inner.Width;
        public uint Height => _inner.Height;
        public uint MipLevels => _inner.MipLevels;
        public uint SampleCount => _inner.SampleCount;
        public GpuPixelFormat Format => _inner.Format;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _device.NoteTextureDisposed();
            _inner.Dispose();
        }
    }
}
