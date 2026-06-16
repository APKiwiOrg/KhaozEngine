using System;
using System.IO;
using StbImageSharp;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render2D.Internal
{
    /// <summary>Owns the GPU device + SpriteBatch and the texture/font factories. Shared by the host and the snapshot.</summary>
    internal sealed class Render2DCore : IDisposable
    {
        public IGpuDevice Gd { get; }
        public SpriteBatch Batch { get; }
        readonly bool _ownsDevice;

        public Render2DCore(IGpuDevice gd, GpuOutputDescription output, bool ownsDevice = true)
        {
            Gd = gd;
            _ownsDevice = ownsDevice;
            Batch = new SpriteBatch(gd, output);
        }

        public Texture2D CreateTexture(byte[] rgba, int width, int height)
        {
            var t = Gd.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                (uint)width, (uint)height, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
            Gd.UpdateTexture(t, rgba, 0, 0, (uint)width, (uint)height);
            return new Texture2D(t, width, height);
        }

        public Texture2D LoadTexture(string pngPath)
        {
            ImageResult img = ImageResult.FromMemory(File.ReadAllBytes(pngPath), ColorComponents.RedGreenBlueAlpha);
            return CreateTexture(img.Data, img.Width, img.Height);
        }

        public SpriteFont LoadFont(string ttfPath, float pixelHeight) =>
            SpriteFont.Build(Gd, File.ReadAllBytes(ttfPath), pixelHeight);

        public void Dispose() { Batch.Dispose(); if (_ownsDevice) Gd.Dispose(); }
    }
}
