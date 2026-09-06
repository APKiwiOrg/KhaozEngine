using System;
using System.IO;
using System.IO.Compression;

namespace KhaozEngine.Imaging
{
    /// <summary>
    /// Minimal, dependency-free PNG encoder for 8-bit RGBA buffers (the row-major, top-to-bottom layout the
    /// headless snapshot helpers return). Uses only the BCL (<see cref="ZLibStream"/> for the IDAT zlib stream +
    /// a CRC-32 table), so a consumer gains no image-library dependency. Tooling / test helper - not a general
    /// image library (no palette, interlace, or non-RGBA support).
    /// </summary>
    public static class PngWriter
    {
        static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        /// <summary>
        /// Encodes a top-to-bottom 8-bit RGBA buffer (<paramref name="rgba"/>, length = width*height*4) into a
        /// PNG byte stream. Each scanline uses filter type 0 (none). Throws if the buffer length is wrong.
        /// </summary>
        public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "width/height must be positive.");
            int expected = width * height * 4;
            if (rgba.Length != expected)
                throw new ArgumentException($"rgba length {rgba.Length} != width*height*4 ({expected}).", nameof(rgba));

            using var outStream = new MemoryStream();
            outStream.Write(Signature, 0, Signature.Length);

            // IHDR: width, height, bitDepth=8, colorType=6 (RGBA), compression=0, filter=0, interlace=0.
            var ihdr = new byte[13];
            WriteBE(ihdr, 0, (uint)width);
            WriteBE(ihdr, 4, (uint)height);
            ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
            WriteChunk(outStream, "IHDR", ihdr);

            // IDAT: zlib stream of filtered scanlines (filter byte 0 + raw RGBA row, top-to-bottom).
            byte[] idat;
            using (var raw = new MemoryStream())
            {
                int stride = width * 4;
                using (var zlib = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
                {
                    var zero = new byte[] { 0 };
                    var row = new byte[stride];
                    for (int y = 0; y < height; y++)
                    {
                        zlib.Write(zero, 0, 1);                       // filter type: none
                        rgba.Slice(y * stride, stride).CopyTo(row);
                        zlib.Write(row, 0, stride);
                    }
                }
                idat = raw.ToArray();
            }
            WriteChunk(outStream, "IDAT", idat);

            WriteChunk(outStream, "IEND", Array.Empty<byte>());
            return outStream.ToArray();
        }

        /// <summary>Encodes <paramref name="rgba"/> via <see cref="Encode"/> and writes it to <paramref name="path"/>.</summary>
        public static void Save(string path, ReadOnlySpan<byte> rgba, int width, int height) =>
            File.WriteAllBytes(path, Encode(rgba, width, height));

        static void WriteChunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4];
            WriteBE(len, 0, (uint)data.Length);
            s.Write(len, 0, 4);

            var typeBytes = new byte[4];
            for (int i = 0; i < 4; i++) typeBytes[i] = (byte)type[i];
            s.Write(typeBytes, 0, 4);
            s.Write(data, 0, data.Length);

            // CRC-32 over the chunk type + data.
            uint crc = PngCrc.Compute(typeBytes, data);
            var crcBytes = new byte[4];
            WriteBE(crcBytes, 0, crc);
            s.Write(crcBytes, 0, 4);
        }

        static void WriteBE(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

    }
}
