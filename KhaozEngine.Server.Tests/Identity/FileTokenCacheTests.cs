using System;
using System.IO;
using System.Threading.Tasks;
using KhaozEngine.Identity;
using Xunit;

namespace KhaozEngine.Tests.Identity;

public class FileTokenCacheTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"keid-{Guid.NewGuid():N}.dat");

    [Fact]
    public async Task Saves_and_loads_and_clears()
    {
        FileTokenCache cache = new(path);
        Assert.Null(await cache.LoadAsync());
        ProviderCredential cred = new("oidc", "tok", "refresh", DateTimeOffset.UtcNow.AddHours(1));
        CachedSession s = new(cred, "session-tok", DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow, "user-42");
        await cache.SaveAsync(s);
        CachedSession? loaded = await cache.LoadAsync();
        Assert.NotNull(loaded);
        Assert.Equal("tok", loaded!.Value.Credential.CredentialToken);
        Assert.Equal("session-tok", loaded.Value.SessionToken);
        Assert.Equal("user-42", loaded.Value.Subject);
        await cache.ClearAsync();
        Assert.Null(await cache.LoadAsync());
    }

    [Fact]
    public async Task Persisted_file_is_not_plain_json()
    {
        FileTokenCache cache = new(path);
        ProviderCredential cred = new("oidc", "secret-token-value", null, DateTimeOffset.UtcNow.AddHours(1));
        CachedSession s = new(cred, null, null, DateTimeOffset.UtcNow, null);
        await cache.SaveAsync(s);
        string raw = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("secret-token-value", raw, StringComparison.Ordinal);
        Assert.StartsWith("KEID1:", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tampered_file_returns_null()
    {
        FileTokenCache cache = new(path);
        ProviderCredential cred = new("oidc", "tok", null, DateTimeOffset.UtcNow.AddHours(1));
        CachedSession s = new(cred, null, null, DateTimeOffset.UtcNow, null);
        await cache.SaveAsync(s);
        string raw = await File.ReadAllTextAsync(path);
        string[] parts = raw.Split(':', 3);
        string tampered = $"{parts[0]}:{new string('0', parts[1].Length)}:{parts[2]}";
        await File.WriteAllTextAsync(path, tampered);
        CachedSession? loaded = await cache.LoadAsync();
        Assert.Null(loaded);
    }

    [Fact]
    public async Task Malformed_file_returns_null()
    {
        await File.WriteAllTextAsync(path, "not-a-valid-cache-file");
        FileTokenCache cache = new(path);
        Assert.Null(await cache.LoadAsync());
    }

    /// <summary>#172: the save wrote the temp file first and chmodded it second, so the encoded session sat at
    /// the predictable "&lt;path&gt;.tmp" with the umask's mode (typically 0644) for the width of the write. The
    /// write now creates the file already owner-only, which means it must also refuse to write through anything
    /// it did not create: a pre-planted symlink at that predictable name used to carry the token straight into
    /// the attacker's file (and left the cache itself pointing there).</summary>
    [Fact]
    public async Task SaveAsync_does_not_write_the_session_through_a_pre_planted_tmp_symlink()
    {
        if (OperatingSystem.IsWindows()) return;

        string probe = path + ".probe";
        await File.WriteAllTextAsync(probe, "not-the-token");
        File.CreateSymbolicLink(path + ".tmp", probe);
        try
        {
            FileTokenCache cache = new(path);
            ProviderCredential cred = new("oidc", "secret-token-value", null, DateTimeOffset.UtcNow.AddHours(1));
            CachedSession s = new(cred, null, null, DateTimeOffset.UtcNow, null);

            await cache.SaveAsync(s);

            Assert.Equal("not-the-token", await File.ReadAllTextAsync(probe));
            Assert.False(File.Exists(path + ".tmp"));
            Assert.StartsWith("KEID1:", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
            Assert.Null(File.ResolveLinkTarget(path, returnFinalTarget: false));
        }
        finally
        {
            if (File.Exists(probe)) File.Delete(probe);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
    }

    [Fact]
    public async Task Saved_file_is_owner_only_on_unix()
    {
        FileTokenCache cache = new(path);
        ProviderCredential cred = new("oidc", "tok", null, DateTimeOffset.UtcNow.AddHours(1));
        CachedSession s = new(cred, null, null, DateTimeOffset.UtcNow, null);

        await cache.SaveAsync(s);

        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    /// <summary>A temp file left behind by a crashed earlier save must not have its mode inherited by the next
    /// one: UnixCreateMode only applies to a file the open creates, so the stale one has to be unlinked.</summary>
    [Fact]
    public async Task SaveAsync_replaces_a_stale_permissive_tmp_file()
    {
        if (OperatingSystem.IsWindows()) return;

        string tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, "leftover");
        File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite
            | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        FileTokenCache cache = new(path);
        ProviderCredential cred = new("oidc", "tok", null, DateTimeOffset.UtcNow.AddHours(1));
        await cache.SaveAsync(new CachedSession(cred, null, null, DateTimeOffset.UtcNow, null));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.NotNull(await cache.LoadAsync());
    }

    public void Dispose()
    {
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        if (File.Exists(path + ".probe")) File.Delete(path + ".probe");
    }
}
