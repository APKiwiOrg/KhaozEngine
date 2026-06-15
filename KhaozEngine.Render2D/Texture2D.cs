using System;
using Veldrid;

namespace KhaozEngine.Render2D
{
    /// <summary>A 2D texture. Wraps the internal GPU resource; consumers never see Veldrid.</summary>
    public sealed class Texture2D : IDisposable
    {
        internal Texture Handle { get; }
        public int Width { get; }
        public int Height { get; }

        internal Texture2D(Texture handle, int width, int height)
        {
            Handle = handle; Width = width; Height = height;
        }

        public void Dispose() => Handle.Dispose();
    }
}
