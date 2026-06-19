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

        internal Texture2D(IGpuTexture handle, int width, int height)
            : this(handle, width, height, ownsHandle: true) { }

        internal Texture2D(IGpuTexture handle, int width, int height, bool ownsHandle)
        {
            Handle = handle; Width = width; Height = height; _ownsHandle = ownsHandle;
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
            if (_ownsHandle) Handle.Dispose();
        }
    }
}
