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
        CachedSession s = new(cred, "session-tok", DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow);
        await cache.SaveAsync(s);
        CachedSession? loaded = await cache.LoadAsync();
        Assert.NotNull(loaded);
        Assert.Equal("tok", loaded!.Value.Credential.CredentialToken);
        Assert.Equal("session-tok", loaded.Value.SessionToken);
        await cache.ClearAsync();
        Assert.Null(await cache.LoadAsync());
    }

    [Fact]
    public async Task Persisted_file_is_not_plain_json()
    {
        FileTokenCache cache = new(path);
        ProviderCredential cred = new("oidc", "secret-token-value", null, DateTimeOffset.UtcNow.AddHours(1));
        CachedSession s = new(cred, null, null, DateTimeOffset.UtcNow);
        await cache.SaveAsync(s);
        string raw = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("secret-token-value", raw, StringComparison.Ordinal);
        Assert.StartsWith("KEID1:", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tampered_file_still_loads_leniently()
    {
        FileTokenCache cache = new(path);
        ProviderCredential cred = new("oidc", "tok", null, DateTimeOffset.UtcNow.AddHours(1));
        CachedSession s = new(cred, null, null, DateTimeOffset.UtcNow);
        await cache.SaveAsync(s);
        string raw = await File.ReadAllTextAsync(path);
        string[] parts = raw.Split(':', 3);
        string tampered = $"{parts[0]}:{new string('0', parts[1].Length)}:{parts[2]}";
        await File.WriteAllTextAsync(path, tampered);
        CachedSession? loaded = await cache.LoadAsync();
        Assert.NotNull(loaded);
        Assert.Equal("tok", loaded!.Value.Credential.CredentialToken);
    }

    [Fact]
    public async Task Malformed_file_returns_null()
    {
        await File.WriteAllTextAsync(path, "not-a-valid-cache-file");
        FileTokenCache cache = new(path);
        Assert.Null(await cache.LoadAsync());
    }

    public void Dispose() { if (File.Exists(path)) File.Delete(path); }
}
