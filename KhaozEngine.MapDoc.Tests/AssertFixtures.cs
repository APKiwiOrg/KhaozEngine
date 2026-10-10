using System;
using System.IO;
using System.Security.Cryptography;

namespace KhaozEngine.Tests.MapDoc;

internal static class AssertFixtures
{
    internal static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}
