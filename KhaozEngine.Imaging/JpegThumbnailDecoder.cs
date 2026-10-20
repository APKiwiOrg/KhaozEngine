using System;
using System.IO;

namespace KhaozEngine.Imaging;

/// <summary>A row-major RGBA8 thumbnail with one pixel per 8x8 image region.</summary>
public readonly record struct JpegThumbnail(int Width, int Height, byte[] Rgba);

/// <summary>Decodes sequential Huffman JPEG thumbnails using only dequantized DC coefficients, without an inverse DCT.</summary>
public static class JpegThumbnailDecoder
{
    /// <summary>
    /// Decodes an 8-bit, one or three component JPEG at ceil(width / 8) by ceil(height / 8).
    /// Sampling factors of one or two, separate component scans and restart markers are supported.
    /// </summary>
    /// <exception cref="InvalidDataException">The stream is malformed, truncated, or an edge is outside 1 to 16384.</exception>
    /// <exception cref="NotSupportedException">The coding process, precision or component count is unsupported.</exception>
    public static JpegThumbnail DecodeEighthScale(ReadOnlySpan<byte> jpeg) => new Decoder(jpeg).Decode();

    private sealed class Component(byte id, int horizontal, int vertical, int quantization)
    {
        internal readonly byte Id = id;
        internal readonly int H = horizontal, V = vertical, Quantization = quantization;
        internal int Columns, Rows, Stride, DcTable, AcTable;
        internal long Prediction;
        internal byte[] Samples = Array.Empty<byte>();
        internal bool Decoded;
    }

    private sealed class Huffman
    {
        private readonly int[] _first = new int[17], _count = new int[17], _offset = new int[17];
        private readonly byte[] _symbols;

        internal Huffman(ReadOnlySpan<byte> counts, ReadOnlySpan<byte> symbols)
        {
            _symbols = symbols.ToArray();
            int code = 0, offset = 0;
            for (int length = 1; length <= 16; length++)
            {
                int count = counts[length - 1];
                // JPEG reserves the all-ones code for entropy padding.
                if (code + count >= 1 << length) throw Invalid("Oversubscribed Huffman table.");
                _first[length] = code;
                _count[length] = count;
                _offset[length] = offset;
                offset += count;
                code = (code + count) << 1;
            }
        }

        internal int Read(ref EntropyReader reader)
        {
            int code = 0;
            for (int length = 1; length <= 16; length++)
            {
                code = (code << 1) | reader.ReadBits(1);
                int index = code - _first[length];
                if (index >= 0 && index < _count[length]) return _symbols[_offset[length] + index];
            }
            throw Invalid("Unknown Huffman code.");
        }
    }

    private ref struct EntropyReader(ReadOnlySpan<byte> data, int position)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        internal int Position = position;
        private int _bits, _remaining;

        internal int ReadBits(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++)
            {
                if (_remaining == 0)
                {
                    _bits = Take();
                    if (_bits == 255 && Take() != 0) throw Invalid("Unexpected marker in entropy data.");
                    _remaining = 8;
                }
                value = (value << 1) | ((_bits >> --_remaining) & 1);
            }
            return value;
        }

        internal void Align()
        {
            if (_remaining != 0 && (_bits & ((1 << _remaining) - 1)) != (1 << _remaining) - 1)
                throw Invalid("Invalid entropy padding.");
            _remaining = 0;
        }

        internal void Restart(int expected)
        {
            Align();
            if (Take() != 255) throw Invalid("Missing restart marker.");
            int marker;
            do { marker = Take(); } while (marker == 255);
            if (marker != 0xD0 + expected) throw Invalid("Incorrect restart sequence.");
        }

        private int Take()
        {
            if ((uint)Position >= (uint)_data.Length) throw Invalid("Truncated entropy data.");
            return _data[Position++];
        }
    }

    private ref struct Decoder(ReadOnlySpan<byte> jpeg)
    {
        private readonly ReadOnlySpan<byte> _jpeg = jpeg;
        private int _position, _width, _height, _maxH, _maxV, _restartInterval;
        private Component[] _components = Array.Empty<Component>();
        private readonly int[] _quantizationDc = new int[4];
        private readonly Huffman?[] _huffman = new Huffman?[8];

        internal JpegThumbnail Decode()
        {
            if (Take() != 255 || Take() != 0xD8) throw Invalid("Missing JPEG SOI marker.");
            while (true)
            {
                int marker = ReadMarker();
                switch (marker)
                {
                    case 0xC0:
                    case 0xC1: ReadFrame(Segment()); break;
                    case 0xDB: ReadQuantization(Segment()); break;
                    case 0xC4: ReadHuffman(Segment()); break;
                    case 0xDD:
                        ReadOnlySpan<byte> interval = Segment();
                        if (interval.Length != 2) throw Invalid("Invalid restart interval.");
                        _restartInterval = UInt16(interval, 0);
                        break;
                    case 0xDA: ReadScan(Segment()); break;
                    case 0xD9:
                        if (_components.Length == 0) throw Invalid("Missing frame.");
                        foreach (Component component in _components)
                            if (!component.Decoded) throw Invalid("Missing component scan.");
                        return Thumbnail();
                    case 0xC2:
                    case 0xC3:
                    case 0xC5:
                    case 0xC6:
                    case 0xC7:
                    case 0xC8:
                    case 0xC9:
                    case 0xCA:
                    case 0xCB:
                    case 0xCC:
                    case 0xCD:
                    case 0xCE:
                    case 0xCF:
                        throw new NotSupportedException("Only sequential Huffman JPEG is supported.");
                    default:
                        if (marker is >= 0xE0 and <= 0xEF or 0xFE) { Segment(); break; }
                        throw Invalid("Unexpected JPEG marker.");
                }
            }
        }

        private void ReadFrame(ReadOnlySpan<byte> frame)
        {
            if (_components.Length != 0 || frame.Length < 6) throw Invalid("Invalid frame header.");
            if (frame[0] != 8) throw new NotSupportedException("Only 8-bit JPEG samples are supported.");
            _height = UInt16(frame, 1);
            _width = UInt16(frame, 3);
            // Validate both edges before any allocation whose size comes from the header.
            if (_width is < 1 or > 16384 || _height is < 1 or > 16384) throw Invalid("JPEG edge is outside 1 to 16384.");
            int count = frame[5];
            if (count is not (1 or 3)) throw new NotSupportedException("Only grayscale and YCbCr JPEG are supported.");
            if (frame.Length != 6 + count * 3) throw Invalid("Invalid frame component list.");
            _components = new Component[count];
            int blocks = 0;
            for (int i = 0; i < count; i++)
            {
                int p = 6 + i * 3, h = frame[p + 1] >> 4, v = frame[p + 1] & 15;
                if (h is < 1 or > 2 || v is < 1 or > 2 || frame[p + 2] > 3) throw Invalid("Invalid component sampling or table.");
                for (int j = 0; j < i; j++)
                    if (_components[j].Id == frame[p]) throw Invalid("Duplicate component identifier.");
                _components[i] = new Component(frame[p], h, v, frame[p + 2]);
                _maxH = Math.Max(_maxH, h);
                _maxV = Math.Max(_maxV, v);
                blocks += h * v;
            }
            if (blocks > 10) throw Invalid("Too many blocks per MCU.");
            foreach (Component c in _components)
            {
                c.Columns = DivideUp(_width * c.H, 8 * _maxH);
                c.Rows = DivideUp(_height * c.V, 8 * _maxV);
                c.Stride = DivideUp(_width, 8 * _maxH) * c.H;
                c.Samples = new byte[c.Stride * DivideUp(_height, 8 * _maxV) * c.V];
            }
        }

        private void ReadQuantization(ReadOnlySpan<byte> tables)
        {
            if (tables.IsEmpty) throw Invalid("Empty quantization segment.");
            while (!tables.IsEmpty)
            {
                int id = tables[0] & 15, precision = tables[0] >> 4;
                if (id > 3 || precision > 1) throw Invalid("Invalid quantization table selector.");
                int size = 64 * (precision + 1);
                if (tables.Length < size + 1) throw Invalid("Truncated quantization table.");
                for (int k = 0; k < 64; k++)
                {
                    int value = precision == 0 ? tables[k + 1] : UInt16(tables, 1 + k * 2);
                    if (value == 0) throw Invalid("Zero quantization value.");
                    if (k == 0) _quantizationDc[id] = value;
                }
                tables = tables[(size + 1)..];
            }
        }

        private void ReadHuffman(ReadOnlySpan<byte> tables)
        {
            if (tables.IsEmpty) throw Invalid("Empty Huffman segment.");
            while (!tables.IsEmpty)
            {
                if (tables.Length < 17) throw Invalid("Truncated Huffman table.");
                int id = tables[0] & 15, kind = tables[0] >> 4, count = 0;
                if (id > 3 || kind > 1) throw Invalid("Invalid Huffman table selector.");
                for (int i = 1; i <= 16; i++) count += tables[i];
                if (count is < 1 or > 256 || count > tables.Length - 17) throw Invalid("Invalid Huffman symbol count.");
                ReadOnlySpan<byte> symbols = tables.Slice(17, count);
                foreach (byte symbol in symbols)
                    if (kind == 0 ? symbol > 11 : (symbol & 15) > 10 || ((symbol & 15) == 0 && symbol is not (0 or 0xF0)))
                        throw Invalid("Invalid sequential Huffman symbol.");
                _huffman[kind * 4 + id] = new Huffman(tables.Slice(1, 16), symbols);
                tables = tables[(17 + count)..];
            }
        }

        private void ReadScan(ReadOnlySpan<byte> scan)
        {
            if (_components.Length == 0 || scan.Length < 4) throw Invalid("Missing frame or scan header.");
            int count = scan[0];
            if (count < 1 || count > _components.Length || scan.Length != 4 + count * 2) throw Invalid("Invalid scan component list.");
            if (scan[^3] != 0 || scan[^2] != 63 || scan[^1] != 0) throw Invalid("Invalid sequential scan parameters.");
            Span<int> indices = stackalloc int[3];
            for (int i = 0; i < count; i++)
            {
                int index = -1;
                for (int j = 0; j < _components.Length; j++)
                    if (_components[j].Id == scan[1 + i * 2]) index = j;
                if (index < 0) throw Invalid("Unknown scan component.");
                for (int j = 0; j < i; j++)
                    if (indices[j] == index) throw Invalid("Duplicate scan component.");
                indices[i] = index;
                Component c = _components[index];
                int selector = scan[2 + i * 2];
                c.DcTable = selector >> 4;
                c.AcTable = selector & 15;
                if (c.Decoded || c.DcTable > 3 || c.AcTable > 3 || _quantizationDc[c.Quantization] == 0 ||
                    _huffman[c.DcTable] == null || _huffman[4 + c.AcTable] == null) throw Invalid("Missing table or repeated component scan.");
                c.Prediction = 0;
            }
            bool interleaved = count > 1;
            Component first = _components[indices[0]];
            int columns = interleaved ? DivideUp(_width, 8 * _maxH) : first.Columns;
            int rows = interleaved ? DivideUp(_height, 8 * _maxV) : first.Rows;
            var reader = new EntropyReader(_jpeg, _position);
            int mcu = 0, restart = 0;
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < columns; x++, mcu++)
                {
                    if (_restartInterval > 0 && mcu > 0 && mcu % _restartInterval == 0)
                    {
                        reader.Restart(restart);
                        restart = (restart + 1) & 7;
                        foreach (Component component in _components) component.Prediction = 0;
                    }
                    for (int i = 0; i < count; i++)
                    {
                        Component c = _components[indices[i]];
                        int h = interleaved ? c.H : 1, v = interleaved ? c.V : 1;
                        for (int by = 0; by < v; by++)
                            for (int bx = 0; bx < h; bx++)
                                c.Samples[(y * v + by) * c.Stride + x * h + bx] = ReadBlock(c, ref reader);
                    }
                }
            reader.Align();
            _position = reader.Position;
            for (int i = 0; i < count; i++) _components[indices[i]].Decoded = true;
        }

        private byte ReadBlock(Component component, ref EntropyReader reader)
        {
            int size = _huffman[component.DcTable]!.Read(ref reader);
            int value = reader.ReadBits(size);
            component.Prediction += size != 0 && value < 1 << (size - 1) ? value - ((1 << size) - 1) : value;
            int coefficient = 1;
            while (coefficient < 64)
            {
                int symbol = _huffman[4 + component.AcTable]!.Read(ref reader);
                if (symbol == 0) break;
                int run = symbol >> 4;
                size = symbol & 15;
                coefficient += size == 0 ? 16 : run + 1;
                if (coefficient > 64) throw Invalid("AC run exceeds the block.");
                reader.ReadBits(size);
            }
            return Channel(component.Prediction * _quantizationDc[component.Quantization] / 8.0 + 128);
        }

        private JpegThumbnail Thumbnail()
        {
            int width = DivideUp(_width, 8), height = DivideUp(_height, 8);
            var rgba = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int p = (y * width + x) * 4;
                    double luma = Sample(_components[0], x, y);
                    double cb = _components.Length == 1 ? 0 : Sample(_components[1], x, y) - 128;
                    double cr = _components.Length == 1 ? 0 : Sample(_components[2], x, y) - 128;
                    rgba[p] = Channel(luma + 1.402 * cr);
                    rgba[p + 1] = Channel(luma - 0.344136 * cb - 0.714136 * cr);
                    rgba[p + 2] = Channel(luma + 1.772 * cb);
                    rgba[p + 3] = 255;
                }
            return new JpegThumbnail(width, height, rgba);
        }

        private byte Sample(Component c, int x, int y) => c.Samples[y * c.V / _maxV * c.Stride + x * c.H / _maxH];

        private int ReadMarker()
        {
            if (Take() != 255) throw Invalid("Missing JPEG marker prefix.");
            int marker;
            do { marker = Take(); } while (marker == 255);
            if (marker == 0) throw Invalid("Stuffed byte outside entropy data.");
            return marker;
        }

        private ReadOnlySpan<byte> Segment()
        {
            int length = (Take() << 8) | Take();
            if (length < 2 || length - 2 > _jpeg.Length - _position) throw Invalid("Invalid JPEG segment length.");
            ReadOnlySpan<byte> segment = _jpeg.Slice(_position, length - 2);
            _position += length - 2;
            return segment;
        }

        private int Take()
        {
            if ((uint)_position >= (uint)_jpeg.Length) throw Invalid("Truncated JPEG data.");
            return _jpeg[_position++];
        }
    }

    private static int DivideUp(int value, int divisor) => (value + divisor - 1) / divisor;
    private static int UInt16(ReadOnlySpan<byte> data, int offset) => (data[offset] << 8) | data[offset + 1];
    private static byte Channel(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
    private static InvalidDataException Invalid(string message) => new(message);
}
