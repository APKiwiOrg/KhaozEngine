using System;
using System.Security.Cryptography;
using System.Text;

namespace KhaozEngine.Movement;

/// <summary>Binds identity, container evidence, frozen profile bytes, candidate bytes and route facts.</summary>
internal static class TerrainWorkload
{
    internal static byte[] Digest(ReadOnlySpan<byte> identity, ReadOnlySpan<byte> evidence, string name,
        ReadOnlySpan<byte> profile, ReadOnlySpan<byte> facts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("terrain-drop-workload-v2"u8);
        hash.AppendData(SHA256.HashData(identity)); hash.AppendData(SHA256.HashData(evidence));
        var label = new NavBakeWriter(); label.WriteShortString(Encoding.UTF8.GetBytes(name)); hash.AppendData(label.WrittenSpan);
        hash.AppendData(SHA256.HashData(profile));
        hash.AppendData(SHA256.HashData(facts));
        return hash.GetHashAndReset();
    }
}
