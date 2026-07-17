using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render2D
{
    /// <summary>A 2D texture. Wraps the engine GPU texture; consumers never see the backend.</summary>
    public sealed class Texture2D : IDisposable
    {
        internal IGpuTexture Handle { get; }
        public int Width { get; }
        public int Height { get; }
        readonly bool _ownsHandle;
        readonly IGpuDevice? _gd;

        internal Texture2D(IGpuTexture handle, int width, int height)
            : this(handle, width, height, ownsHandle: true) { }

        internal Texture2D(IGpuTexture handle, int width, int height, bool ownsHandle)
            : this(null, handle, width, height, ownsHandle) { }

        // The gd-carrying ctor makes Dispose drain the device before freeing the handle: a texture created via
        // CreateTexture/LoadTexture (or SpriteFont's atlas) may still have a queued UpdateTexture staging copy in
        // flight when the caller disposes it mid-game. Internal creation sites pass their device. The public
        // Wrap(...) below does not know one, so it keeps the pre-existing immediate-dispose behaviour.
        internal Texture2D(IGpuDevice? gd, IGpuTexture handle, int width, int height, bool ownsHandle)
        {
            _gd = gd; Handle = handle; Width = width; Height = height; _ownsHandle = ownsHandle;
        }

        /// <summary>
        /// Wrap an existing engine GPU texture (created with <c>GpuTextureUsage.Sampled</c>) as a
        /// <see cref="Texture2D"/> so a <see cref="SpriteBatch"/> can draw it. Use this to composite a render
        /// target produced by another module - e.g. an offscreen 3D model preview - into a 2D pass on the SAME
        /// device. When <paramref name="ownsHandle"/> is false the returned wrapper does NOT dispose the
        /// underlying texture (the producer keeps ownership and reuses it across frames); when true,
        /// <see cref="Dispose"/> frees it.
        /// </summary>
        public static Texture2D Wrap(IGpuTexture handle, int width, int height, bool ownsHandle = true) =>
            new(handle, width, height, ownsHandle);

        public void Dispose()
        {
            if (!_ownsHandle) return;
            // Queued GPU work (an upload still in flight, a queued draw) may reference the handle, so drain
            // the device before destroying it when we have one.
            _gd?.WaitForIdle();
            Handle.Dispose();
        }
    }
}
