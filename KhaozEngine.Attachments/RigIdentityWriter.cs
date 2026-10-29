using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KhaozEngine.Attachments;

internal sealed class RigIdentityWriter : IDisposable
{
    readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    internal void Integer(int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    internal void Text(string value)
    {
        if (!AttachmentText.IsWellFormedUtf16(value))
            throw new InvalidDataException("Rig identity text must be well-formed UTF-16.");
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Integer(bytes.Length);
        hash.AppendData(bytes);
    }

    internal void Transform(JsonElement node)
    {
        bool matrix = node.TryGetProperty("matrix", out JsonElement value);
        if (matrix && (node.TryGetProperty("translation", out _) || node.TryGetProperty("rotation", out _) || node.TryGetProperty("scale", out _)))
            throw new InvalidDataException("A rest node must use matrix or TRS, not both.");
        Integer(matrix ? 1 : 0);
        if (matrix) Numbers(value, 16);
        else
        {
            Numbers(node, "translation", new[] { "0", "0", "0" });
            Numbers(node, "rotation", new[] { "0", "0", "0", "1e0" });
            Numbers(node, "scale", new[] { "1e0", "1e0", "1e0" });
        }
    }

    void Numbers(JsonElement node, string name, string[] defaults)
    {
        if (node.TryGetProperty(name, out JsonElement values)) Numbers(values, defaults.Length);
        else foreach (string value in defaults) Text(value);
    }

    void Numbers(JsonElement values, int count)
    {
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() != count)
            throw new InvalidDataException($"Rest transform must contain {count} finite numbers.");
        foreach (JsonElement number in values.EnumerateArray())
        {
            if (number.ValueKind != JsonValueKind.Number || !number.TryGetSingle(out float finite) || !float.IsFinite(finite))
                throw new InvalidDataException("Rest transform must contain finite numbers.");
            Text(CanonicalNumber(number.GetRawText()));
        }
    }

    static string CanonicalNumber(string token)
    {
        bool negative = token[0] == '-';
        int start = negative ? 1 : 0;
        int exponentAt = token.IndexOfAny(new[] { 'e', 'E' });
        string mantissa = token[start..(exponentAt < 0 ? token.Length : exponentAt)];
        int dot = mantissa.IndexOf('.');
        int fraction = dot < 0 ? 0 : mantissa.Length - dot - 1;
        string digits = mantissa.Replace(".", "", StringComparison.Ordinal).TrimStart('0');
        if (digits.Length == 0) return "0";
        int trimmed = digits.Length - digits.TrimEnd('0').Length;
        digits = digits[..(digits.Length - trimmed)];
        BigInteger exponent = exponentAt < 0 ? BigInteger.Zero
            : BigInteger.Parse(token.AsSpan(exponentAt + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        exponent += trimmed - fraction;
        return (negative ? "-" : "") + digits + "e" + exponent.ToString(CultureInfo.InvariantCulture);
    }

    internal void Accessor(AttachmentGlbAccessors.Accessor accessor, string type)
    {
        Descriptor(type, accessor.ComponentType, accessor.Count, accessor.Normalized);
        Span<byte> zeros = stackalloc byte[64];
        zeros.Clear();
        for (int i = 0; i < accessor.Count; i++)
        {
            ReadOnlySpan<byte> bytes = accessor.RawElement(i);
            hash.AppendData(bytes.IsEmpty ? zeros[..accessor.ElementSize] : bytes);
        }
    }

    internal void DefaultBinds(int count)
    {
        Descriptor("MAT4", 5126, count, false);
        Span<byte> identity = stackalloc byte[64];
        identity.Clear();
        for (int i = 0; i < 4; i++) BinaryPrimitives.WriteInt32LittleEndian(identity.Slice(i * 20, 4), 0x3f800000);
        for (int i = 0; i < count; i++) hash.AppendData(identity);
    }

    void Descriptor(string type, int component, int count, bool normalized)
    {
        Text(type);
        Integer(component);
        Integer(count);
        Integer(normalized ? 1 : 0);
    }

    internal string Finish() => "rig-v1:" + Convert.ToHexStringLower(hash.GetHashAndReset());
    public void Dispose() => hash.Dispose();
}
