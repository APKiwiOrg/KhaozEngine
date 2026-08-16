using System;
using System.IO;

namespace KhaozEngine.TileWorld.Editing;

/// <summary>One decoded greyscale image: the samples of a binary PGM, row-major with the TOP row first, which
/// is the order the file itself is written in.</summary>
/// <param name="Width">Columns in the image, always positive.</param>
/// <param name="Height">Rows in the image, always positive.</param>
/// <param name="MaxValue">The file's maxval, the sample value that means white, 1 to 65535.</param>
/// <param name="Samples">Width times Height samples, row-major, the top row first.</param>
public readonly record struct PgmImage(int Width, int Height, int MaxValue, ushort[] Samples)
{
    /// <summary>The sample at column x of row y, row 0 being the TOP row of the image.</summary>
    public ushort Sample(int x, int y) => Samples[y * Width + x];
}

/// <summary>Reads binary PGM (netpbm P5) greyscale images, 8 or 16 bit, which is how a heightmap painted in
/// any terrain or image tool reaches this engine. PGM rather than PNG because the format is a header of ASCII
/// decimals followed by raw big-endian samples, so reading it is the parser below rather than a deflate
/// decoder the engine does not have.
///
/// Every malformed file is refused with a <see cref="TileWorldException"/> naming the file and the problem,
/// never decoded on a guess: a heightmap that silently reads a byte out of step is a terrain nobody can tell
/// is wrong until it is authored on top of. Trailing bytes after the raster are the one thing tolerated,
/// because a file with a stray newline at the end is still a whole image.</summary>
public static class PgmReader
{
    /// <summary>Reads the binary PGM at <paramref name="path"/>, naming that path in any error.</summary>
    public static PgmImage Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new TileWorldException($"{path}: cannot read the heightmap file. {ex.Message}", ex);
        }
        return Read(bytes, path);
    }

    /// <summary>Reads a binary PGM already in memory, naming <paramref name="name"/> (or "&lt;bytes&gt;" when
    /// there is none) in any error.</summary>
    public static PgmImage Read(ReadOnlySpan<byte> bytes, string? name = null)
    {
        string file = string.IsNullOrEmpty(name) ? "<bytes>" : name;
        if (bytes.Length < 2 || bytes[0] != (byte)'P' || bytes[1] != (byte)'5')
            throw new TileWorldException(
                $"{file}: not a binary PGM. The first two bytes must be the magic P5, so a P2 ascii greymap or a P6 colour pixmap is refused rather than read as one.");

        int at = 2;
        int width = ReadHeaderInt(bytes, ref at, file, "width");
        int height = ReadHeaderInt(bytes, ref at, file, "height");
        int maxValue = ReadHeaderInt(bytes, ref at, file, "maxval");

        if (width <= 0 || height <= 0)
            throw new TileWorldException(
                $"{file}: the header claims a {width} by {height} image, and both dimensions must be positive.");
        if (maxValue < 1 || maxValue > 65535)
            throw new TileWorldException(
                $"{file}: maxval is {maxValue}, outside the 1 to 65535 a PGM sample can carry.");

        // Exactly ONE whitespace byte closes the header. Skipping every whitespace byte here would eat the
        // first sample of any raster that happens to begin with a byte worth 9 to 13 or 32.
        if (at >= bytes.Length || !IsWhitespace(bytes[at]))
            throw new TileWorldException(
                $"{file}: maxval must be followed by exactly one whitespace byte and then the raster.");
        at++;

        int bytesPerSample = maxValue < 256 ? 1 : 2;
        long needed = (long)width * height * bytesPerSample;
        long available = bytes.Length - at;
        // Compared BEFORE anything is allocated, so a header claiming a 100000 by 100000 image is refused on the
        // bytes it does not have rather than after asking for 20 GB of samples. A hostile or corrupt header must
        // cost a string, not the process.
        if (available < needed)
            throw new TileWorldException(
                $"{file}: the header claims a {width} by {height} {bytesPerSample * 8} bit raster, which needs {needed} bytes, and only {available} follow the header.");

        // Safe as an int now: the check above puts width times height times the sample size inside the byte
        // count, which is itself an int.
        int count = (int)((long)width * height);
        var samples = new ushort[count];
        if (bytesPerSample == 1)
            for (int i = 0; i < count; i++) samples[i] = bytes[at + i];
        else
            for (int i = 0; i < count; i++) samples[i] = (ushort)((bytes[at + 2 * i] << 8) | bytes[at + 2 * i + 1]);
        return new PgmImage(width, height, maxValue, samples);
    }

    // One ASCII decimal of the header, with the separator that must precede it. Every field is read the same
    // way, so a comment or a run of newlines is legal between any two of them.
    static int ReadHeaderInt(ReadOnlySpan<byte> bytes, ref int at, string file, string field)
    {
        if (!SkipSeparators(bytes, ref at))
            throw new TileWorldException($"{file}: the header needs whitespace or a comment before {field}.");
        if (at >= bytes.Length || !IsDigit(bytes[at]))
            throw new TileWorldException(
                $"{file}: the header ends or turns non-numeric where {field} should be. A binary PGM header is P5, then width, height and maxval as decimals.");
        long value = 0;
        while (at < bytes.Length && IsDigit(bytes[at]))
        {
            value = value * 10 + (bytes[at] - '0');
            if (value > int.MaxValue)
                throw new TileWorldException($"{file}: {field} is too large to be a real image.");
            at++;
        }
        return (int)value;
    }

    // Whitespace and hash comments (to the end of the line) between two header fields. Returns false when
    // neither was there, which is what stops "2 2255" from reading as a height and a maxval.
    static bool SkipSeparators(ReadOnlySpan<byte> bytes, ref int at)
    {
        int start = at;
        while (at < bytes.Length)
        {
            if (IsWhitespace(bytes[at])) { at++; continue; }
            if (bytes[at] == (byte)'#')
            {
                while (at < bytes.Length && bytes[at] != (byte)'\n' && bytes[at] != (byte)'\r') at++;
                continue;
            }
            break;
        }
        return at > start;
    }

    static bool IsWhitespace(byte b) => b is 0x20 or 0x09 or 0x0A or 0x0B or 0x0C or 0x0D;

    static bool IsDigit(byte b) => b >= (byte)'0' && b <= (byte)'9';
}
