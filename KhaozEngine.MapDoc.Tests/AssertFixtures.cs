using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace KhaozEngine.Tests.MapDoc;

internal static class AssertFixtures
{
    internal static (string Path, string Sha256)[] Files(string directory, string? excludedRelativePath = null) =>
        Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Where(path => Path.GetRelativePath(directory, path) != excludedRelativePath)
            .Select(path => (Path.GetRelativePath(directory, path), Sha256(path))).ToArray();

    internal static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}
