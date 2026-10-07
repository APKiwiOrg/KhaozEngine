using System;
using System.Buffers;
using System.Globalization;
using System.Text.Encodings.Web;

namespace KhaozEngine.MapDoc.Storage;

/// <summary>The same bounded token operations count bytes or emit into a fixed measured destination.</summary>
internal ref struct MapSurfaceEmbeddedTokenWriter
{
    readonly Span<byte> _destination;
    readonly int _limit;
    readonly bool _emit;
    internal int Length { get; private set; }

    internal MapSurfaceEmbeddedTokenWriter(int limit)
    {
        if (limit < 0) throw Limit();
        _destination = default;
        _limit = limit;
        _emit = false;
        Length = 0;
    }

    internal MapSurfaceEmbeddedTokenWriter(byte[] destination)
    {
        _destination = destination;
        _limit = destination.Length;
        _emit = true;
        Length = 0;
    }

    internal void Byte(byte value)
    {
        Reserve(1);
        if (_emit) _destination[Length] = value;
        Length++;
    }

    internal void Bytes(scoped ReadOnlySpan<byte> value)
    {
        Reserve(value.Length);
        if (_emit) value.CopyTo(_destination[Length..]);
        Length += value.Length;
    }

    internal void Ascii(scoped ReadOnlySpan<char> value)
    {
        Reserve(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] > 0x7F) throw new MapDocumentException("non-ASCII encoded JSON token");
            if (_emit) _destination[Length + i] = (byte)value[i];
        }
        Length += value.Length;
    }

    internal void String(scoped ReadOnlySpan<char> value, bool packed = false)
    {
        // Every input char costs at least one output byte. Pay for both quotes before scanning.
        Reserve(2);
        if (value.Length > _limit - Length - 2) throw Limit();
        Byte((byte)'"');
        if (packed && IsBase64(value)) Ascii(value);
        else
        {
            // Default escaping is ASCII and needs at most 12 chars for one supplementary scalar.
            Span<char> scratch = stackalloc char[128];
            while (!value.IsEmpty)
            {
                OperationStatus status = JavaScriptEncoder.Default.Encode(value, scratch,
                    out int consumed, out int written, isFinalBlock: true);
                if (status is not (OperationStatus.Done or OperationStatus.DestinationTooSmall) || consumed == 0)
                    throw new MapDocumentException("unable to encode embedded JSON string");
                Ascii(scratch[..written]);
                value = value[consumed..];
            }
        }
        Byte((byte)'"');
    }

    internal void Scalar(object? value, bool packed)
    {
        switch (value)
        {
            case null: Bytes("null"u8); break;
            case string text: String(text.AsSpan(), packed); break;
            case char character: String(character.ToString().AsSpan(), packed); break;
            case bool boolean: Bytes(boolean ? "true"u8 : "false"u8); break;
            case float single when !float.IsFinite(single):
            case double floating when !double.IsFinite(floating):
            case Half half when !Half.IsFinite(half):
                throw new MapDocumentException("non-finite JSON number in embedded surface patch");
            case IUtf8SpanFormattable number when value is sbyte or byte or short or ushort or int or uint or long or ulong or
                Int128 or UInt128 or Half or float or double or decimal:
                Number(number);
                break;
            default:
                throw new MapDocumentException("unsupported JSON scalar in embedded surface patch");
        }
    }

    void Number(IUtf8SpanFormattable value)
    {
        Span<byte> scratch = stackalloc byte[128];
        if (!value.TryFormat(scratch, out int written, default, CultureInfo.InvariantCulture))
            throw new MapDocumentException("unable to format embedded JSON number");
        Bytes(scratch[..written]);
    }

    static bool IsBase64(ReadOnlySpan<char> value)
    {
        if (value.Length % 4 != 0) return false;
        if (!value.IsEmpty && value[^1] == '=') value = value[..^1];
        if (!value.IsEmpty && value[^1] == '=') value = value[..^1];
        foreach (char c in value)
            if (c is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/')) return false;
        return true;
    }

    void Reserve(int length)
    {
        if (length > _limit - Length) throw Limit();
    }

    static MapDocumentException Limit() => new("surface embedding exceeds its limit, use tiled storage");
}
