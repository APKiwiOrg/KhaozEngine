using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using KhaozEngine.Updates;
using Xunit;

namespace KhaozEngine.Tests.Updates;

public sealed class HttpUpdateSourceTests
{
    private const string DefaultPath = "api/updates/latest?platform={platform}";

    private sealed class StubHandler : System.Net.Http.HttpMessageHandler
    {
        private readonly byte[] body;
        public StubHandler(byte[] body) => this.body = body;
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
            => Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.ByteArrayContent(body)
            });
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), "ke-cap-" + Guid.NewGuid().ToString("N"));

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

        byte[]? result = await src.DownloadBytesAsync("https://attacker.com/manifest.json", long.MaxValue);

        Assert.Null(result);
    }

    [Fact]
    public async Task DownloadBytes_NonHttps_ReturnsNull()
    {
        HttpUpdateSource src = Source("https://updates.example.com/");

        byte[]? result = await src.DownloadBytesAsync("http://updates.example.com/manifest.json", long.MaxValue);

        Assert.Null(result);
    }

    [Fact]
    public async Task DownloadBytes_OverCap_ReturnsNull()
    {
        var src = new HttpUpdateSource(new HttpUpdateSourceOptions { ServerBaseUrl = "https://updates.example.com/" },
            new HttpClient(new StubHandler(new byte[100])));
        byte[]? result = await src.DownloadBytesAsync("https://updates.example.com/manifest.json", 10);
        Assert.Null(result);
    }

    [Fact]
    public async Task DownloadBytes_WithinCap_ReturnsBytes()
    {
        var body = new byte[] { 1, 2, 3, 4 };
        var src = new HttpUpdateSource(new HttpUpdateSourceOptions { ServerBaseUrl = "https://updates.example.com/" },
            new HttpClient(new StubHandler(body)));
        byte[]? result = await src.DownloadBytesAsync("https://updates.example.com/manifest.json", 1024);
        Assert.Equal(body, result);
    }

    [Fact]
    public async Task CheckLatestVersion_OverCap_ReturnsNull()
    {
        var src = new HttpUpdateSource(
            new HttpUpdateSourceOptions { ServerBaseUrl = "https://updates.example.com/", MaxLatestVersionBytes = 16 },
            new HttpClient(new StubHandler(new byte[64 * 1024])));

        LatestVersionInfo? result = await src.CheckLatestVersionAsync("win-x64");

        Assert.Null(result);
    }

    [Fact]
    public async Task CheckLatestVersion_WithinCap_DeserializesInfo()
    {
        byte[] body = System.Text.Encoding.UTF8.GetBytes(
            "{\"version\":\"1.2.3\",\"buildVersion\":\"build-7\",\"manifestUrl\":\"https://updates.example.com/m.json\",\"required\":true}");
        var src = new HttpUpdateSource(
            new HttpUpdateSourceOptions { ServerBaseUrl = "https://updates.example.com/" },
            new HttpClient(new StubHandler(body)));

        LatestVersionInfo? result = await src.CheckLatestVersionAsync("win-x64");

        Assert.NotNull(result);
        Assert.Equal("1.2.3", result!.Version);
        Assert.Equal("build-7", result.BuildVersion);
        Assert.Equal("https://updates.example.com/m.json", result.ManifestUrl);
        Assert.True(result.Required);
    }

    [Fact]
    public async Task DownloadFile_OffOriginHost_ReturnsFalse()
    {
        HttpUpdateSource src = Source("https://updates.example.com/");

        bool ok = await src.DownloadFileAsync("https://attacker.com/game.dll", Path.Combine(Path.GetTempPath(), "x.bin"), long.MaxValue);

        Assert.False(ok);
    }

    [Fact]
    public async Task DownloadFile_OverCap_ReturnsFalseAndLeavesNoFile()
    {
        byte[] body = new byte[10];
        var src = new HttpUpdateSource(
            new HttpUpdateSourceOptions { ServerBaseUrl = "https://updates.example.com/" },
            new HttpClient(new StubHandler(body)));
        string dest = TempPath();
        try
        {
            bool ok = await src.DownloadFileAsync("https://updates.example.com/game.dll", dest, maxBytes: 4);

            Assert.False(ok);
            Assert.False(File.Exists(dest));
        }
        finally
        {
            if (File.Exists(dest)) File.Delete(dest);
        }
    }

    [Fact]
    public async Task DownloadFile_ExactlyAtCap_ReturnsTrueAndWritesBytes()
    {
        byte[] body = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var src = new HttpUpdateSource(
            new HttpUpdateSourceOptions { ServerBaseUrl = "https://updates.example.com/" },
            new HttpClient(new StubHandler(body)));
        string dest = TempPath();
        try
        {
            bool ok = await src.DownloadFileAsync("https://updates.example.com/game.dll", dest, maxBytes: 10);

            Assert.True(ok);
            Assert.True(File.Exists(dest));
            Assert.Equal(body, await File.ReadAllBytesAsync(dest));
        }
        finally
        {
            if (File.Exists(dest)) File.Delete(dest);
        }
    }

    [Fact]
    public async Task DownloadFile_UnderCap_ReturnsTrue()
    {
        byte[] body = new byte[] { 1, 2, 3 };
        var src = new HttpUpdateSource(
            new HttpUpdateSourceOptions { ServerBaseUrl = "https://updates.example.com/" },
            new HttpClient(new StubHandler(body)));
        string dest = TempPath();
        try
        {
            bool ok = await src.DownloadFileAsync("https://updates.example.com/game.dll", dest, maxBytes: 100);

            Assert.True(ok);
            Assert.True(File.Exists(dest));
        }
        finally
        {
            if (File.Exists(dest)) File.Delete(dest);
        }
    }
}
