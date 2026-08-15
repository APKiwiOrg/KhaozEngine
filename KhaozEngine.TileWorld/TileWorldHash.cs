using System;
using System.Security.Cryptography;

namespace KhaozEngine.TileWorld;

/// <summary>The hashes that pin a world's files to its manifest.</summary>
public static class TileWorldHash
{
    /// <summary>Lower-hex SHA-256 of a region file's exact bytes.</summary>
    public static string OfRegionBytes(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
