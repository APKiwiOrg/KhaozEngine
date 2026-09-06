using System;

namespace KhaozEngine.Imaging;

/// <summary>
/// Decoded, top-to-bottom PNG samples. <see cref="Bytes"/> keeps the PNG channel order. Greyscale and RGB images
/// with a <c>tRNS</c> chunk are expanded to GA and RGBA. Each 16-bit sample is stored most-significant byte first,
/// matching the PNG wire format and preserving all 16 bits.
/// </summary>
public readonly record struct PngImage(int Width, int Height, int Channels, int BitDepth, byte[] Bytes)
{
    /// <summary>Reads one 16-bit channel sample from <see cref="Bytes"/>.</summary>
    public ushort Sample16(int pixelIndex, int channel = 0)
    {
        if (BitDepth != 16) throw new InvalidOperationException("the PNG does not contain 16-bit samples");
        if ((uint)channel >= (uint)Channels) throw new ArgumentOutOfRangeException(nameof(channel));
        if ((uint)pixelIndex >= (uint)(Width * Height)) throw new ArgumentOutOfRangeException(nameof(pixelIndex));
        int offset = (pixelIndex * Channels + channel) * 2;
        return (ushort)((Bytes[offset] << 8) | Bytes[offset + 1]);
    }
}
