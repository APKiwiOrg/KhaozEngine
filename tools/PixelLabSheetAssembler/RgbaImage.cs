using System;
using System.IO;
using KhaozEngine.Imaging;

namespace PixelLabSheetAssembler;

/// <summary>One 8-bit RGBA pixel.</summary>
public readonly record struct Rgba32(byte R, byte G, byte B, byte A);

/// <summary>
/// Top-to-bottom, row-major RGBA8 pixel buffer. A new image is fully transparent. PNG load and save go
/// through <c>KhaozEngine.Imaging</c>, so the tool carries no third-party image library.
/// </summary>
public sealed class RgbaImage
{
    private readonly byte[] _pixels;

    public RgbaImage(int width, int height)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        Width = width;
        Height = height;
        _pixels = new byte[checked(width * height * 4)];
    }

    public int Width { get; }
    public int Height { get; }

    public Rgba32 this[int x, int y]
    {
        get
        {
            int o = Offset(x, y);
            return new Rgba32(_pixels[o], _pixels[o + 1], _pixels[o + 2], _pixels[o + 3]);
        }
        set
        {
            int o = Offset(x, y);
            _pixels[o] = value.R;
            _pixels[o + 1] = value.G;
            _pixels[o + 2] = value.B;
            _pixels[o + 3] = value.A;
        }
    }

    /// <summary>Decodes a PNG file into RGBA8.</summary>
    public static RgbaImage Load(string path) => FromPng(PngReader.Decode(File.ReadAllBytes(path)));

    /// <summary>Encodes this image as an RGBA8 PNG at <paramref name="path"/>.</summary>
    public void SaveAsPng(string path) => PngWriter.Save(path, _pixels, Width, Height);

    // Expands any PngReader layout (grey, grey+alpha, RGB, RGBA at 8 or 16 bits) to RGBA8. Grey is
    // replicated into R, G and B, a missing alpha is opaque, and a 16-bit sample rounds to the nearest 8-bit value.
    internal static RgbaImage FromPng(PngImage png)
    {
        var img = new RgbaImage(png.Width, png.Height);
        int channels = png.Channels;
        bool wide = png.BitDepth == 16;
        int count = png.Width * png.Height;
        for (int i = 0; i < count; i++)
        {
            int o = i * 4;
            byte first = Sample(png.Bytes, i * channels, wide);
            if (channels <= 2)
            {
                img._pixels[o] = img._pixels[o + 1] = img._pixels[o + 2] = first;
                img._pixels[o + 3] = channels == 2 ? Sample(png.Bytes, i * channels + 1, wide) : (byte)255;
            }
            else
            {
                img._pixels[o] = first;
                img._pixels[o + 1] = Sample(png.Bytes, i * channels + 1, wide);
                img._pixels[o + 2] = Sample(png.Bytes, i * channels + 2, wide);
                img._pixels[o + 3] = channels == 4 ? Sample(png.Bytes, i * channels + 3, wide) : (byte)255;
            }
        }
        return img;
    }

    private static byte Sample(byte[] bytes, int sampleIndex, bool wide)
    {
        if (!wide) return bytes[sampleIndex];
        int value = (bytes[sampleIndex * 2] << 8) | bytes[sampleIndex * 2 + 1];
        return (byte)((value * 255 + 32895) >> 16);
    }

    private int Offset(int x, int y)
    {
        if ((uint)x >= (uint)Width) throw new ArgumentOutOfRangeException(nameof(x));
        if ((uint)y >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(y));
        return (y * Width + x) * 4;
    }
}
