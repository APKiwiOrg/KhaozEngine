using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

/// <summary>Binds the recipe, complete container evidence, frozen profile bytes and ordered candidates.</summary>
internal static class TerrainWorkload
{
    internal static byte[] Digest(ReadOnlySpan<byte> identity, ReadOnlySpan<byte> evidence, string name,
        ReadOnlySpan<byte> profile, IEnumerable<NavLink> candidates, out int count)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("terrain-drop-workload-v1"u8);
        hash.AppendData(SHA256.HashData(identity)); hash.AppendData(SHA256.HashData(evidence));
        var label = new NavBakeWriter(); label.WriteShortString(Encoding.UTF8.GetBytes(name)); hash.AppendData(label.WrittenSpan);
        hash.AppendData(SHA256.HashData(profile));
        Span<byte> bytes = stackalloc byte[25]; count = 0;
        foreach (var link in candidates)
        {
            if (count == Array.MaxLength) throw new InvalidOperationException("The terrain workload exceeds representable candidates.");
            count++;
            BinaryPrimitives.WriteInt32LittleEndian(bytes, link.FromLayer); BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], link.FromX);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[8..], link.FromZ); BinaryPrimitives.WriteInt32LittleEndian(bytes[12..], link.ToLayer);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[16..], link.ToX); BinaryPrimitives.WriteInt32LittleEndian(bytes[20..], link.ToZ);
            bytes[24] = (byte)link.Kind; hash.AppendData(bytes);
        }
        return hash.GetHashAndReset();
    }
}
