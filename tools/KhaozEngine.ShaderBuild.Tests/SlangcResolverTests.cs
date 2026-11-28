using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.ShaderBuild;
using Xunit;

namespace KhaozEngine.Tests.ShaderBuild;

public sealed class SlangcResolverTests : IDisposable
{
    private const string Version = "2026.23";
    private const string Rid = "test-rid";
    private static readonly Uri AssetUrl = new("https://downloads.example.invalid/slang-test.tar.gz");

    private readonly string _cacheRoot =
        Path.Combine(Path.GetTempPath(), "khaozengine-slangc-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_cacheRoot))
            Directory.Delete(_cacheRoot, recursive: true);
    }

    [Fact]
    public void Downloads_verifies_and_caches_then_reuses_without_network()
    {
        var archive = Archive();
        var pins = Pins(archive);

        var first = Resolve(pins, new ServingHandler(archive));
        var reused = Resolve(pins, new FailingHandler());

        Assert.Equal(Path.Combine(_cacheRoot, Version, Rid, "bin", "slangc"), first);
        Assert.True(File.Exists(first));
        if (!OperatingSystem.IsWindows())
            Assert.True(File.GetUnixFileMode(first).HasFlag(UnixFileMode.UserExecute));
        Assert.Equal(first, reused);
    }

    [Fact]
    public void A_damaged_cache_folder_is_replaced_only_under_the_publish_lock()
    {
        var archive = Archive();
        var pins = Pins(archive);
        var damaged = Path.Combine(_cacheRoot, Version, Rid);
        Directory.CreateDirectory(Path.Combine(damaged, "lib"));

        using (new FileStream(Path.Combine(_cacheRoot, SlangcCacheLock.FileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var refusal = Assert.Throws<ShaderBuildException>(
                () => SlangcResolver.Resolve(pins, null, _cacheRoot, new ServingHandler(archive), _ => Version, Rid, TimeSpan.Zero));

            Assert.Equal(ShaderBuildFailure.ToolchainMissing, refusal.Failure);
            Assert.True(Directory.Exists(Path.Combine(damaged, "lib")));
            Assert.False(File.Exists(Path.Combine(damaged, "bin", "slangc")));
        }

        var path = Resolve(pins, new ServingHandler(archive));

        Assert.True(File.Exists(path));
        Assert.False(Directory.Exists(Path.Combine(damaged, "lib")));
        AssertCacheHoldsOnlyThePublishedFolder();
    }

    [Fact]
    public void Refuses_an_archive_whose_digest_differs_and_caches_nothing()
    {
        var pins = Pins(Archive());
        var tampered = Archive("tampered");

        var refusal = Assert.Throws<ShaderBuildException>(() => Resolve(pins, new ServingHandler(tampered)));

        Assert.Equal(ShaderBuildFailure.DigestMismatch, refusal.Failure);
        AssertNothingCached();
    }

    [Fact]
    public void A_failed_download_names_SLANGC_and_leaves_no_partial_cache()
    {
        var pins = Pins(Archive());

        var refusal = Assert.Throws<ShaderBuildException>(() => Resolve(pins, new FailingHandler()));

        Assert.Equal(ShaderBuildFailure.DownloadFailed, refusal.Failure);
        Assert.Equal("SLANGC", refusal.OverrideVariable);
        Assert.Equal(AssetUrl, refusal.AssetUrl);
        AssertNothingCached();
    }

    [Fact]
    public async Task Concurrent_resolves_into_an_empty_cache_agree_on_one_complete_folder()
    {
        var archive = Archive();
        var pins = Pins(archive);
        var handler = new RendezvousHandler(archive, parties: 2);

        var paths = await Task.WhenAll(
            Task.Run(() => Resolve(pins, handler)),
            Task.Run(() => Resolve(pins, handler)));

        Assert.Equal(paths[0], paths[1]);
        Assert.True(File.Exists(paths[0]));
        AssertCacheHoldsOnlyThePublishedFolder();
    }

    [Fact]
    public void An_override_with_the_wrong_version_is_refused()
    {
        var pins = Pins(Archive());

        var refusal = Assert.Throws<ShaderBuildException>(
            () => Resolve(pins, new FailingHandler(), slangcOverride: "/opt/slang/bin/slangc", probe: _ => "2026.22\n"));

        Assert.Equal(ShaderBuildFailure.ToolchainVersion, refusal.Failure);
    }

    [Fact]
    public void A_matching_override_is_used_without_the_network()
    {
        var pins = Pins(Archive());

        var path = Resolve(pins, new FailingHandler(), slangcOverride: "/opt/slang/bin/slangc", probe: _ => "2026.23\n");

        Assert.Equal("/opt/slang/bin/slangc", path);
        AssertNothingCached();
    }

    [Theory]
    [InlineData("other-rid")]
    [InlineData(null)]
    public void A_host_the_pin_does_not_cover_names_SLANGC(string? hostRid)
    {
        var pins = new SlangcPins(Version, new Dictionary<string, SlangcAsset>
        {
            ["other-host"] = new(AssetUrl, new string('0', 64), "bin/slangc"),
        });

        var refusal = Assert.Throws<ShaderBuildException>(
            () => SlangcResolver.Resolve(pins, null, _cacheRoot, new FailingHandler(), _ => Version, hostRid, SlangcCacheLock.DefaultTimeout));

        Assert.Equal(ShaderBuildFailure.ToolchainMissing, refusal.Failure);
        Assert.Equal("SLANGC", refusal.OverrideVariable);
    }

    [Fact]
    public void The_committed_pin_covers_every_supported_host()
    {
        var pins = SlangcPins.Load(Path.Combine(AppContext.BaseDirectory, "slangc.pins.json"));

        Assert.Equal(Version, pins.Version);
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["linux-arm64"] = "bin/slangc",
                ["linux-x64"] = "bin/slangc",
                ["osx-arm64"] = "bin/slangc",
                ["win-x64"] = "bin/slangc.exe",
            },
            pins.Assets.ToDictionary(pair => pair.Key, pair => pair.Value.Executable));
    }

    [Theory]
    [InlineData("""{ "version": "2026.23", "assets": { "osx-arm64": { "url": "https://h/a.tar.gz", "sha256": "abc", "executable": "bin/slangc" } } }""")]
    [InlineData("""{ "version": "2026.23", "assets": { "osx-arm64": { "url": "a.tar.gz", "sha256": "0000000000000000000000000000000000000000000000000000000000000000", "executable": "bin/slangc" } } }""")]
    [InlineData("""{ "version": "2026.23", "assets": { "osx-arm64": { "url": "https://h/a.tar.gz", "sha256": "0000000000000000000000000000000000000000000000000000000000000000", "executable": "../slangc" } } }""")]
    [InlineData("""{ "version": "2026.23", "assets": {} }""")]
    [InlineData("""{ "assets": { "osx-arm64": { "url": "https://h/a.tar.gz", "sha256": "0000000000000000000000000000000000000000000000000000000000000000", "executable": "bin/slangc" } } }""")]
    [InlineData("not json")]
    public void A_malformed_pin_file_is_refused(string json)
    {
        Directory.CreateDirectory(_cacheRoot);
        var path = Path.Combine(_cacheRoot, "slangc.pins.json");
        File.WriteAllText(path, json);

        var refusal = Assert.Throws<ShaderBuildException>(() => SlangcPins.Load(path));

        Assert.Equal(ShaderBuildFailure.ManifestInvalid, refusal.Failure);
    }

    private string Resolve(
        SlangcPins pins, HttpMessageHandler handler, string? slangcOverride = null, Func<string, string>? probe = null)
        => SlangcResolver.Resolve(pins, slangcOverride, _cacheRoot, handler, probe ?? (_ => Version), Rid, SlangcCacheLock.DefaultTimeout);

    private void AssertCacheHoldsOnlyThePublishedFolder()
    {
        Assert.Equal(
            [Path.Combine(_cacheRoot, SlangcCacheLock.FileName), Path.Combine(_cacheRoot, Version)],
            Directory.GetFileSystemEntries(_cacheRoot).Order(StringComparer.Ordinal));
        Assert.Equal([Path.Combine(_cacheRoot, Version, Rid)], Directory.GetFileSystemEntries(Path.Combine(_cacheRoot, Version)));
    }

    private void AssertNothingCached()
    {
        var entries = Directory.Exists(_cacheRoot)
            ? Directory.GetFileSystemEntries(_cacheRoot, "*", SearchOption.AllDirectories)
            : [];
        Assert.Empty(entries);
    }

    private static SlangcPins Pins(byte[] archive) => new(Version, new Dictionary<string, SlangcAsset>
    {
        [Rid] = new(AssetUrl, Convert.ToHexStringLower(SHA256.HashData(archive)), "bin/slangc"),
    });

    private static byte[] Archive(string payload = "slangc")
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "bin/")
            {
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            });
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "bin/slangc")
            {
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(payload)),
            });
        }
        return buffer.ToArray();
    }

    private sealed class ServingHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(new HttpRequestException("network unreachable"));
    }

    // Holds every download until all parties have asked, so both resolves miss the cache and race the publish.
    private sealed class RendezvousHandler(byte[] body, int parties) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _arrived) == parties)
                _allArrived.SetResult();
            await _allArrived.Task.WaitAsync(TimeSpan.FromMinutes(1), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        }
    }
}
