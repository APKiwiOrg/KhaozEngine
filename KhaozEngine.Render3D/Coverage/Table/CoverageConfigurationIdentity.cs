using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace KhaozEngine.Render3D;

/// <summary>Exact authored binding words for one part. Socket is the attachment's local transform before alignment.</summary>
public readonly record struct CoverageConfigurationPart(uint Id, string HostJoint, Matrix4x4 Socket, Matrix4x4 BodyAlignment);

/// <summary>One complete authored item. Digests identify exact attachment and companion bytes, including their commands.</summary>
public sealed record CoverageConfigurationItem(string AttachmentSha256, string CompanionSha256,
    IReadOnlyList<CoverageConfigurationPart> Parts);

/// <summary>Canonical configuration identity shared by runtime binding and offline tooling. All work here is cold.</summary>
public static class CoverageConfigurationIdentity
{
    /// <summary>Hash items sorted by ordinal attachment digest then companion digest, parts by unsigned ID, then
    /// skin-order inverse binds. Strings are length-prefixed UTF-8, counts and IDs are little-endian uint32 and
    /// matrices are row-major binary32 words. Paths, poses, incarnations and transition weights never enter.
    /// Repeated attachment/companion pairs and nonpositive or duplicate part IDs are invalid parameters.</summary>
    public static string Compute(IReadOnlyList<CoverageConfigurationItem> items, ReadOnlySpan<Matrix4x4> inverseBind)
    {
        ArgumentNullException.ThrowIfNull(items);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var ordered = items.OrderBy(i => i.AttachmentSha256, StringComparer.Ordinal)
            .ThenBy(i => i.CompanionSha256, StringComparer.Ordinal).ToArray();
        Text("KhaozEngine.CoverageConfiguration/2");
        Word((uint)ordered.Length);
        string? previousAttachment = null, previousCompanion = null;
        foreach (var item in ordered)
        {
            Digest(item.AttachmentSha256); Digest(item.CompanionSha256);
            if (item.AttachmentSha256 == previousAttachment && item.CompanionSha256 == previousCompanion)
                throw new ArgumentException("A configuration cannot repeat an attachment and companion identity.", nameof(items));
            previousAttachment = item.AttachmentSha256; previousCompanion = item.CompanionSha256;
            ArgumentNullException.ThrowIfNull(item.Parts);
            var parts = item.Parts.OrderBy(p => p.Id).ToArray();
            Word((uint)parts.Length);
            uint previous = 0;
            foreach (var part in parts)
            {
                if (part.Id == 0 || part.Id == previous) throw new ArgumentException("Part IDs must be unique and positive.", nameof(items));
                previous = part.Id;
                Word(part.Id); Text(part.HostJoint); Matrix(part.Socket); Matrix(part.BodyAlignment);
            }
        }
        Word((uint)inverseBind.Length);
        foreach (var matrix in inverseBind) Matrix(matrix);
        return Convert.ToHexStringLower(hash.GetHashAndReset());

        void Word(uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            hash.AppendData(bytes);
        }
        void Text(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(value);
            Word((uint)bytes.Length); hash.AppendData(bytes);
        }
        void Digest(string value)
        {
            if (value is not { Length: 64 } || value.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                throw new ArgumentException("Configuration digests must be lowercase SHA-256.", nameof(items));
            Text(value);
        }
        void Matrix(Matrix4x4 matrix)
        {
            for (int row = 0; row < 4; row++)
                for (int column = 0; column < 4; column++)
                {
                    float value = matrix[row, column];
                    if (!float.IsFinite(value)) throw new ArgumentException("Configuration matrices must be finite.", nameof(items));
                    Word(BitConverter.SingleToUInt32Bits(value));
                }
        }
    }
}
