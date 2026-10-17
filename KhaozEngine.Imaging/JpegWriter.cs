using System;
using System.Numerics;

namespace KhaozEngine.Imaging
{
    /// <summary>
    /// Minimal, dependency-free baseline JPEG encoder for 8-bit RGBA buffers (row-major, top to bottom). Writes a
    /// JFIF stream with full-range BT.601 YCbCr, 4:2:0 chroma subsampling, the ITU T.81 Annex K quantization tables
    /// scaled by the IJG quality rule and the Annex K standard Huffman tables. Alpha is ignored. The output is a pure
    /// function of the input, so two encodes of the same pixels are byte-identical.
    /// </summary>
    public static class JpegWriter
    {
        /// <summary>
        /// Encodes <paramref name="rgba"/> (length <c>width * height * 4</c>) at <paramref name="quality"/> 1 to 100
        /// and returns the JPEG byte stream.
        /// </summary>
        public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height, int quality)
        {
            if (width < 1 || width > 65535) throw new ArgumentOutOfRangeException(nameof(width), width, "width must be 1 to 65535.");
            if (height < 1 || height > 65535) throw new ArgumentOutOfRangeException(nameof(height), height, "height must be 1 to 65535.");
            if (quality < 1 || quality > 100) throw new ArgumentOutOfRangeException(nameof(quality), quality, "quality must be 1 to 100.");
            long expected = (long)width * height * 4;
            if (rgba.Length != expected)
                throw new ArgumentException($"rgba length {rgba.Length} != width*height*4 ({expected}).", nameof(rgba));

            byte[] luminance = JpegTables.Luminance(quality);
            byte[] chrominance = JpegTables.Chrominance(quality);
            var output = new JpegBitWriter((int)Math.Min((long)width * height / 2 + 1024, 16 << 20));

            WriteHeaders(output, width, height, luminance, chrominance);
            WriteScan(output, rgba, width, height,
                      JpegForwardDct.Reciprocals(luminance), JpegForwardDct.Reciprocals(chrominance));
            output.FlushBits();
            output.WriteByte(0xFF);
            output.WriteByte(0xD9);
            return output.ToArray();
        }

        static void WriteHeaders(JpegBitWriter output, int width, int height, byte[] luminance, byte[] chrominance)
        {
            output.WriteBytes(stackalloc byte[] { 0xFF, 0xD8 });

            // APP0 JFIF 1.01, aspect ratio density 1:1, no thumbnail.
            output.WriteBytes(stackalloc byte[]
            {
                0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00,
                0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
            });

            output.WriteBytes(stackalloc byte[] { 0xFF, 0xDB });
            output.WriteUInt16(2 + 2 * 65);
            WriteQuantization(output, 0, luminance);
            WriteQuantization(output, 1, chrominance);

            // SOF0: 8-bit samples, Y sampled 2x2 against table 0, Cb and Cr 1x1 against table 1.
            output.WriteBytes(stackalloc byte[] { 0xFF, 0xC0, 0x00, 0x11, 0x08 });
            output.WriteUInt16(height);
            output.WriteUInt16(width);
            output.WriteBytes(stackalloc byte[] { 0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01 });

            output.WriteBytes(stackalloc byte[] { 0xFF, 0xC4 });
            output.WriteUInt16(2 + HuffmanSize(JpegTables.DcLuminance) + HuffmanSize(JpegTables.AcLuminance)
                                 + HuffmanSize(JpegTables.DcChrominance) + HuffmanSize(JpegTables.AcChrominance));
            WriteHuffman(output, 0x00, JpegTables.DcLuminance);
            WriteHuffman(output, 0x10, JpegTables.AcLuminance);
            WriteHuffman(output, 0x01, JpegTables.DcChrominance);
            WriteHuffman(output, 0x11, JpegTables.AcChrominance);

            // SOS: all three components interleaved, Y on tables 0/0, Cb and Cr on 1/1, full spectral range.
            output.WriteBytes(stackalloc byte[]
            {
                0xFF, 0xDA, 0x00, 0x0C, 0x03, 0x01, 0x00, 0x02, 0x11, 0x03, 0x11, 0x00, 0x3F, 0x00,
            });
        }

        static void WriteQuantization(JpegBitWriter output, byte id, byte[] table)
        {
            output.WriteByte(id);
            for (int k = 0; k < 64; k++) output.WriteByte(table[JpegTables.ZigZag[k]]);
        }

        static int HuffmanSize(JpegTables.Huffman table) => 1 + 16 + table.Symbols.Length;

        static void WriteHuffman(JpegBitWriter output, byte classAndId, JpegTables.Huffman table)
        {
            output.WriteByte(classAndId);
            output.WriteBytes(table.Counts);
            output.WriteBytes(table.Symbols);
        }

        // One 16x16 MCU at a time: four Y blocks, then one Cb and one Cr block averaged over 2x2 pixels. Pixels past
        // the right and bottom edges repeat the last column and row.
        static void WriteScan(JpegBitWriter output, ReadOnlySpan<byte> rgba, int width, int height,
                              float[] luminance, float[] chrominance)
        {
            Span<float> y = stackalloc float[256];
            Span<float> cb = stackalloc float[64];
            Span<float> cr = stackalloc float[64];
            Span<int> coefficients = stackalloc int[64];
            int dcY = 0, dcCb = 0, dcCr = 0;

            for (int mcuY = 0; mcuY < height; mcuY += 16)
            {
                for (int mcuX = 0; mcuX < width; mcuX += 16)
                {
                    cb.Clear();
                    cr.Clear();
                    for (int py = 0; py < 16; py++)
                    {
                        int row = Math.Min(mcuY + py, height - 1) * width;
                        int blockRow = (py >> 3) * 128 + (py & 7) * 8;
                        int chromaRow = (py >> 1) * 8;
                        for (int px = 0; px < 16; px++)
                        {
                            int i = (row + Math.Min(mcuX + px, width - 1)) * 4;
                            float r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];
                            y[blockRow + (px >> 3) * 64 + (px & 7)] = 0.299f * r + 0.587f * g + 0.114f * b - 128f;
                            cb[chromaRow + (px >> 1)] += -0.168736f * r - 0.331264f * g + 0.5f * b;
                            cr[chromaRow + (px >> 1)] += 0.5f * r - 0.418688f * g - 0.081312f * b;
                        }
                    }
                    for (int k = 0; k < 64; k++)
                    {
                        cb[k] *= 0.25f;
                        cr[k] *= 0.25f;
                    }

                    for (int block = 0; block < 4; block++)
                        WriteBlock(output, y.Slice(block * 64, 64), luminance, coefficients, ref dcY,
                                   JpegTables.DcLuminance, JpegTables.AcLuminance);
                    WriteBlock(output, cb, chrominance, coefficients, ref dcCb, JpegTables.DcChrominance, JpegTables.AcChrominance);
                    WriteBlock(output, cr, chrominance, coefficients, ref dcCr, JpegTables.DcChrominance, JpegTables.AcChrominance);
                }
            }
        }

        static void WriteBlock(JpegBitWriter output, Span<float> block, float[] reciprocals, Span<int> coefficients,
                               ref int previousDc, JpegTables.Huffman dc, JpegTables.Huffman ac)
        {
            JpegForwardDct.Transform(block);
            for (int k = 0; k < 64; k++)
            {
                int n = JpegTables.ZigZag[k];
                coefficients[k] = (int)MathF.Round(block[n] * reciprocals[n], MidpointRounding.AwayFromZero);
            }

            int diff = coefficients[0] - previousDc;
            previousDc = coefficients[0];
            int size = Category(diff);
            output.WriteBits(dc.Codes[size], dc.Lengths[size]);
            if (size > 0) output.WriteBits(diff < 0 ? diff - 1 : diff, size);

            int run = 0;
            for (int k = 1; k < 64; k++)
            {
                int value = coefficients[k];
                if (value == 0)
                {
                    run++;
                    continue;
                }
                for (; run > 15; run -= 16) output.WriteBits(ac.Codes[0xF0], ac.Lengths[0xF0]);
                size = Category(value);
                int symbol = (run << 4) | size;
                output.WriteBits(ac.Codes[symbol], ac.Lengths[symbol]);
                output.WriteBits(value < 0 ? value - 1 : value, size);
                run = 0;
            }
            if (run > 0) output.WriteBits(ac.Codes[0x00], ac.Lengths[0x00]);
        }

        // The magnitude category of T.81 table F.1: the bit length of |value|.
        static int Category(int value) => value == 0 ? 0 : 32 - BitOperations.LeadingZeroCount((uint)Math.Abs(value));
    }
}
