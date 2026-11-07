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
        if (limit < 0) throw new MapDocumentException(MapDocumentRefusal.ValueOutsideRange,
            "surface embedding exceeds its limit, use tiled storage")
            { FieldPath = "maxBytes", ActualInteger = limit, MinimumInteger = 0 };
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
            // Only default-encoder ASCII output, raw JSON numbers or validated base64 reaches this operation.
            if (value[i] > 0x7F) throw new InvalidOperationException("non-ASCII encoded JSON token");
            if (_emit) _destination[Length + i] = (byte)value[i];
        }
        Length += value.Length;
    }

    internal void String(scoped ReadOnlySpan<char> value, bool packed = false)
    {
        // Every input char costs at least one output byte. Pay for both quotes before scanning.
        Reserve(2);
        if (value.Length > _limit - Length - 2) throw Limit((long)Length + 2 + value.Length);
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
                    // The default encoder needs at most 12 characters per scalar, so the scratch span admits progress.
                    throw new InvalidOperationException("unable to encode embedded JSON string");
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
                throw new MapDocumentException(MapDocumentRefusal.ValueNonfinite, "non-finite JSON number in embedded surface patch")
                {
                    RecordType = value.GetType(), SubjectKind = "embedded scalar",
                    ActualNumber = value switch { float f => f, double d => d, Half h => (double)h, _ => null },
                };
            case IUtf8SpanFormattable number when value is sbyte or byte or short or ushort or int or uint or long or ulong or
                Int128 or UInt128 or Half or float or double or decimal:
                Number(number);
                break;
            default:
                throw new MapDocumentException(MapDocumentRefusal.SchemaMalformed, "unsupported JSON scalar in embedded surface patch")
                    { RecordType = value.GetType(), SubjectKind = "embedded scalar" };
        }
    }

    void Number(IUtf8SpanFormattable value)
    {
        Span<byte> scratch = stackalloc byte[128];
        if (!value.TryFormat(scratch, out int written, default, CultureInfo.InvariantCulture))
            // Only built-in numeric types reach this method, and each invariant token fits the scratch span.
            throw new InvalidOperationException("unable to format embedded JSON number");
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
        if (length > _limit - Length) throw Limit((long)Length + length);
    }

    MapDocumentException Limit(long required) => new(MapDocumentRefusal.WorkCapacityExceeded,
        "surface embedding exceeds its limit, use tiled storage")
        { Budget = "MaxEmbeddingBytes", ActualByteCount = required, Limit = _limit };
}
