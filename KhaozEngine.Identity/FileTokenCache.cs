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
        WriteOwnerOnly(tmp, Encode(json));
        File.Move(tmp, filePath, overwrite: true);
        return Task.CompletedTask;
    }

    /// <summary>Writes <paramref name="contents"/> to a file that is BORN owner-only on unix, keeping the
    /// atomic write-temp-then-move shape. The write used to run first and the chmod after it, which left the
    /// encoded session (refresh token included) sitting at the predictable <c>&lt;path&gt;.tmp</c> with whatever
    /// the process umask gives, typically 0644, for the width of the write: a co-located local user could read
    /// it inside that window. <see cref="FileStreamOptions.UnixCreateMode"/> applies only to a file the open
    /// actually creates, so any leftover temp (a crashed earlier save, or a pre-planted file or symlink at that
    /// predictable name) is unlinked first and <see cref="FileMode.CreateNew"/> then refuses to write through
    /// anything this call did not create itself.</summary>
    private static void WriteOwnerOnly(string path, string contents)
    {
        File.Delete(path);

        FileStreamOptions open = new()
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            open.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using FileStream stream = File.Open(path, open);
        stream.Write(Encoding.UTF8.GetBytes(contents));
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
