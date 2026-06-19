using System;
using System.IO;
using StbImageSharp;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// A decoded image held on the CPU as tightly-packed 8-bit RGBA (row-major, top-left origin,
    /// <c>Pixels.Length == Width * Height * 4</c>). Carries no GPU resources - decode a PNG with
    /// <see cref="Load"/> / <see cref="Decode"/> (or <see cref="Render2DSurface.LoadImageRgba"/>) when a game
    /// needs the pixels themselves, e.g. to rebuild an opaque-pixel collision mask. To also draw it, hand
    /// <see cref="Pixels"/> with <see cref="Width"/>/<see cref="Height"/> to <c>Render2DSurface.CreateTexture</c>
    /// (no second decode).
    /// </summary>
    public readonly struct ImageRgba
    {
        /// <summary>Tightly-packed RGBA8 pixels, row-major, top-left origin (length = Width*Height*4).</summary>
        public byte[] Pixels { get; }
        public int Width { get; }
        public int Height { get; }

        public ImageRgba(byte[] pixels, int width, int height)
        {
            ArgumentNullException.ThrowIfNull(pixels);
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "width/height must be positive.");
            int expected = width * height * 4;
            if (pixels.Length != expected)
                throw new ArgumentException($"pixels length {pixels.Length} != width*height*4 ({expected}).", nameof(pixels));
            Pixels = pixels; Width = width; Height = height;
        }

        /// <summary>The alpha channel (0..255) at pixel (<paramref name="x"/>, <paramref name="y"/>).</summary>
        public byte AlphaAt(int x, int y)
        {
            if ((uint)x >= (uint)Width) throw new ArgumentOutOfRangeException(nameof(x));
            if ((uint)y >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(y));
            return Pixels[(y * Width + x) * 4 + 3];
        }

        /// <summary>
        /// True when the pixel's alpha is at least <paramref name="threshold"/> - the per-pixel test for building
        /// an opaque-pixel collision mask. Default threshold 1 treats any non-zero alpha as solid.
        /// </summary>
        public bool IsOpaqueAt(int x, int y, byte threshold = 1) => AlphaAt(x, y) >= threshold;

        /// <summary>Decode an encoded image (PNG/JPG/...) from memory to RGBA on the CPU. No GPU device needed.</summary>
        public static ImageRgba Decode(ReadOnlySpan<byte> fileBytes)
        {
            ImageResult img = ImageResult.FromMemory(fileBytes.ToArray(), ColorComponents.RedGreenBlueAlpha);
            return new ImageRgba(img.Data, img.Width, img.Height);
        }

        /// <summary>Decode an image file from disk to RGBA on the CPU. No GPU device needed.</summary>
        public static ImageRgba Load(string path) => Decode(File.ReadAllBytes(path));
    }
}
