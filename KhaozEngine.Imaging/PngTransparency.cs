using System;
using System.IO;

namespace KhaozEngine.Imaging;

internal readonly record struct PngTransparency(
    int SourceChannels, ushort Grey, ushort Red, ushort Green, ushort Blue)
{
    public int OutputChannels => SourceChannels + 1;

    public static PngTransparency Parse(
        int colorType, int bitDepth, int width, int height, ReadOnlySpan<byte> data)
    {
        int sourceChannels = colorType switch
        {
            0 => 1,
            2 => 3,
            4 or 6 => throw new InvalidDataException("PNG tRNS is invalid for a color type with alpha"),
            _ => throw new NotSupportedException("PNG tRNS is not supported for this color type"),
        };
        int expectedLength = sourceChannels == 1 ? 2 : 6;
        if (data.Length != expectedLength)
            throw new InvalidDataException($"PNG tRNS must contain {expectedLength} bytes for this color type");
        if (bitDepth == 8 && HasNonzeroHighByte(data))
            throw new InvalidDataException("PNG tRNS sample exceeds the image bit depth");

        long expandedLength = (long)width * height * (sourceChannels + 1) * (bitDepth / 8);
        if (expandedLength > PngReader.MaxDecodedBytes)
            throw new InvalidDataException(
                $"PNG transparency-expanded payload exceeds the {PngReader.MaxDecodedBytes}-byte allocation cap");

        return sourceChannels == 1
            ? new PngTransparency(1, Read16(data, 0), 0, 0, 0)
            : new PngTransparency(3, 0, Read16(data, 0), Read16(data, 2), Read16(data, 4));
    }

    public byte[] Expand(ReadOnlySpan<byte> source, int bitDepth)
    {
        int bytesPerSample = bitDepth / 8;
        int sourcePixelBytes = SourceChannels * bytesPerSample;
        int outputPixelBytes = OutputChannels * bytesPerSample;
        int pixelCount = source.Length / sourcePixelBytes;
        var output = new byte[pixelCount * outputPixelBytes];

        for (int pixel = 0; pixel < pixelCount; pixel++)
        {
            int sourceOffset = pixel * sourcePixelBytes;
            int outputOffset = pixel * outputPixelBytes;
            source.Slice(sourceOffset, sourcePixelBytes).CopyTo(output.AsSpan(outputOffset));
            bool transparent = Matches(source.Slice(sourceOffset, sourcePixelBytes), bitDepth);
            output.AsSpan(outputOffset + sourcePixelBytes, bytesPerSample).Fill(transparent ? (byte)0 : (byte)0xff);
        }
        return output;
    }

    private bool Matches(ReadOnlySpan<byte> pixel, int bitDepth)
    {
        if (SourceChannels == 1) return ReadSample(pixel, 0, bitDepth) == Grey;
        return ReadSample(pixel, 0, bitDepth) == Red
            && ReadSample(pixel, 1, bitDepth) == Green
            && ReadSample(pixel, 2, bitDepth) == Blue;
    }

    private static ushort ReadSample(ReadOnlySpan<byte> pixel, int channel, int bitDepth) => bitDepth == 8
        ? pixel[channel]
        : Read16(pixel, channel * 2);

    private static ushort Read16(ReadOnlySpan<byte> bytes, int offset) =>
        (ushort)((bytes[offset] << 8) | bytes[offset + 1]);

    private static bool HasNonzeroHighByte(ReadOnlySpan<byte> data)
    {
        for (int offset = 0; offset < data.Length; offset += 2)
            if (data[offset] != 0) return true;
        return false;
    }
}
