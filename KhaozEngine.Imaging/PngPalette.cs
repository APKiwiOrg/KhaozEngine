using System;
using System.IO;

namespace KhaozEngine.Imaging;

/// <summary>
/// The PLTE entries of an indexed-colour PNG and the optional per-entry alpha from its tRNS chunk. Expansion
/// turns one index byte per pixel into 8-bit RGB, or RGBA when tRNS is present. Entries tRNS does not cover
/// are opaque.
/// </summary>
internal sealed class PngPalette
{
    private readonly byte[] _rgb;
    private byte[]? _alpha;

    private PngPalette(byte[] rgb) => _rgb = rgb;

    public int Entries => _rgb.Length / 3;

    public int OutputChannels => _alpha is null ? 3 : 4;

    public static PngPalette Parse(ReadOnlySpan<byte> data, int bitDepth)
    {
        if (data.Length == 0 || data.Length % 3 != 0 || data.Length > 256 * 3)
            throw new InvalidDataException("PNG PLTE must hold 1 to 256 RGB entries");
        if (data.Length / 3 > 1 << bitDepth)
            throw new InvalidDataException($"PNG PLTE holds more entries than {bitDepth}-bit indices can address");
        return new PngPalette(data.ToArray());
    }

    public void SetTransparency(ReadOnlySpan<byte> data)
    {
        if (data.Length > Entries)
            throw new InvalidDataException("PNG tRNS holds more entries than the palette");
        _alpha = data.ToArray();
    }

    public byte[] Expand(ReadOnlySpan<byte> indices)
    {
        int channels = OutputChannels;
        var output = new byte[indices.Length * channels];
        for (int pixel = 0; pixel < indices.Length; pixel++)
        {
            int entry = indices[pixel];
            if (entry >= Entries) throw new InvalidDataException("PNG palette index is out of range");
            int o = pixel * channels;
            output[o] = _rgb[entry * 3];
            output[o + 1] = _rgb[entry * 3 + 1];
            output[o + 2] = _rgb[entry * 3 + 2];
            if (_alpha is { } alpha) output[o + 3] = entry < alpha.Length ? alpha[entry] : (byte)0xff;
        }
        return output;
    }
}
