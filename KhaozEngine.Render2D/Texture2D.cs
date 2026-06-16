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

        internal Texture2D(IGpuTexture handle, int width, int height)
        {
            Handle = handle; Width = width; Height = height;
        }

        public void Dispose() => Handle.Dispose();
    }
}
