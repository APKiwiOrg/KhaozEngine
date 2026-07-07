using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Serialization;

namespace KhaozEngine.Identity;

/// <summary>File-backed token cache: obfuscated at rest (casual-read/tamper deterrence, not real security) with
/// owner-only permissions on unix. Swap for a keychain-backed <see cref="ITokenCache"/> for stronger at-rest
/// protection. The on-disk format is <c>KEID1:&lt;hex HMACSHA256&gt;:&lt;base64 JSON&gt;</c>; the HMAC key is fixed
/// in-assembly (this is deterrence, not a secret, since anyone with the assembly can recompute it), but the HMAC is
/// still checked as a real integrity/corruption check on load: a mismatched HMAC, a malformed envelope, or invalid
/// base64 all return null from <see cref="LoadAsync"/>, so the caller falls back to a clean re-sign-in rather than
/// trusting a tampered or corrupted payload.</summary>
public sealed class FileTokenCache : ITokenCache
{
    private const string FormatTag = "KEID1";
    private static readonly byte[] HmacKey = Encoding.UTF8.GetBytes("KhaozEngine-Identity-v1");

    private readonly string filePath;

    public FileTokenCache(string filePath) => this.filePath = filePath;

    public Task<CachedSession?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(filePath)) return Task.FromResult<CachedSession?>(null);
        string raw = File.ReadAllText(filePath);
        string? json = Decode(raw);
        if (json is null) return Task.FromResult<CachedSession?>(null);
        return Task.FromResult<CachedSession?>(JsonSerializer.Deserialize<CachedSession>(json, JsonDefaults.IndentedWrite));
    }

    public Task SaveAsync(CachedSession session, CancellationToken ct = default)
    {
        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string json = JsonSerializer.Serialize(session, JsonDefaults.IndentedWrite);
        string tmp = filePath + ".tmp";
        File.WriteAllText(tmp, Encode(json));
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, filePath, overwrite: true);
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        if (File.Exists(filePath)) File.Delete(filePath);
        return Task.CompletedTask;
    }

    private static string Encode(string json)
    {
        string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        string mac = Convert.ToHexStringLower(ComputeHmac(payload));
        return $"{FormatTag}:{mac}:{payload}";
    }

    private static string? Decode(string raw)
    {
        string[] parts = raw.Split(':', 3);
        if (parts.Length != 3 || parts[0] != FormatTag) return null;
        string payload = parts[2];
        byte[] expected = ComputeHmac(payload);
        byte[] got;
        try { got = Convert.FromHexString(parts[1]); }
        catch (FormatException) { return null; }
        if (got.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(got, expected)) return null;
        try
        {
            byte[] jsonBytes = Convert.FromBase64String(payload);
            return Encoding.UTF8.GetString(jsonBytes);
        }
        catch (FormatException) { return null; }
    }

    private static byte[] ComputeHmac(string payload)
    {
        using HMACSHA256 h = new(HmacKey);
        return h.ComputeHash(Encoding.UTF8.GetBytes(payload));
    }
}
