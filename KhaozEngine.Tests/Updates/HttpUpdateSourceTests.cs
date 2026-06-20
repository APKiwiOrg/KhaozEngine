using System;
using System.IO;
using System.Threading.Tasks;
using KhaozEngine.Updates;
using Xunit;

namespace KhaozEngine.Tests.Updates;

public sealed class HttpUpdateSourceTests
{
    private const string DefaultPath = "api/updates/latest?platform={platform}";

    [Fact]
    public void BuildLatestVersionUrl_DefaultPath_InjectsPlatform()
    {
        Uri? url = HttpUpdateSource.BuildLatestVersionUrl("https://host.example.com", DefaultPath, "win-x64");

        Assert.Equal("https://host.example.com/api/updates/latest?platform=win-x64", url!.ToString());
    }

    [Fact]
    public void BuildLatestVersionUrl_BareHost_AssumesHttps()
    {
        Uri? url = HttpUpdateSource.BuildLatestVersionUrl("host.example.com", DefaultPath, "linux-x64");

        Assert.Equal("https", url!.Scheme);
        Assert.Equal("host.example.com", url.Host);
    }

    [Fact]
    public void BuildLatestVersionUrl_TrailingSlash_NoDoubleSlash()
    {
        Uri? url = HttpUpdateSource.BuildLatestVersionUrl("https://host/", DefaultPath, "osx-arm64");

        Assert.DoesNotContain("//api", url!.AbsolutePath);
    }

    [Fact]
    public void BuildLatestVersionUrl_CustomEndpoint_IsHostAgnostic()
    {
        Uri? url = HttpUpdateSource.BuildLatestVersionUrl(
            "https://downloads.mygame.io", "releases/check/{platform}.json", "osx-x64");

        Assert.Equal("https://downloads.mygame.io/releases/check/osx-x64.json", url!.ToString());
    }

    [Fact]
    public void BuildLatestVersionUrl_EmptyBase_ReturnsNull()
    {
        Assert.Null(HttpUpdateSource.BuildLatestVersionUrl("", DefaultPath, "win-x64"));
        Assert.Null(HttpUpdateSource.BuildLatestVersionUrl("   ", DefaultPath, "win-x64"));
    }

    [Fact]
    public void ResolveFileUrl_PlacesFileAsSiblingOfManifest()
    {
        var source = new HttpUpdateSource(new HttpUpdateSourceOptions { ServerBaseUrl = "https://host" });
        var latest = new LatestVersionInfo("1.2.3", "1.2.3", "https://blob/releases/1.2.3/win-x64/manifest.json", false);

        Assert.Equal("https://blob/releases/1.2.3/win-x64/game.dll", source.ResolveFileUrl(latest, "game.dll"));
        Assert.Equal("https://blob/releases/1.2.3/win-x64/data/x.bin", source.ResolveFileUrl(latest, "data/x.bin"));
    }

    private static HttpUpdateSource Source(string baseUrl)
        => new(new HttpUpdateSourceOptions { ServerBaseUrl = baseUrl });

    [Fact]
    public async Task DownloadBytes_OffOriginHost_ReturnsNull()
    {
        HttpUpdateSource src = Source("https://updates.example.com/");

        byte[]? result = await src.DownloadBytesAsync("https://attacker.com/manifest.json");

        Assert.Null(result);
    }

    [Fact]
    public async Task DownloadBytes_NonHttps_ReturnsNull()
    {
        HttpUpdateSource src = Source("https://updates.example.com/");

        byte[]? result = await src.DownloadBytesAsync("http://updates.example.com/manifest.json");

        Assert.Null(result);
    }

    [Fact]
    public async Task DownloadFile_OffOriginHost_ReturnsFalse()
    {
        HttpUpdateSource src = Source("https://updates.example.com/");

        bool ok = await src.DownloadFileAsync("https://attacker.com/game.dll", Path.Combine(Path.GetTempPath(), "x.bin"), long.MaxValue);

        Assert.False(ok);
    }
}
