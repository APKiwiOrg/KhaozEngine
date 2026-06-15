using System;
using System.IO;
using StbImageSharp;
using Veldrid;

namespace KhaozEngine.Render2D.Internal
{
    /// <summary>Owns the GraphicsDevice + SpriteBatch and the texture/font factories. Shared by the host and the snapshot.</summary>
    internal sealed class Render2DCore : IDisposable
    {
        public GraphicsDevice Gd { get; }
        public SpriteBatch Batch { get; }
        readonly bool _ownsDevice;

        public Render2DCore(GraphicsDevice gd, OutputDescription output, bool ownsDevice = true)
        {
            Gd = gd;
            _ownsDevice = ownsDevice;
            Batch = new SpriteBatch(gd, output);
        }

        public Texture2D CreateTexture(byte[] rgba, int width, int height)
        {
            var t = Gd.ResourceFactory.CreateTexture(TextureDescription.Texture2D(
                (uint)width, (uint)height, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Sampled));
            Gd.UpdateTexture(t, rgba, 0, 0, 0, (uint)width, (uint)height, 1, 0, 0);
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
