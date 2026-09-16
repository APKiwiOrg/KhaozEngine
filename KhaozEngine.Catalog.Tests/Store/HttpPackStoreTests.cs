using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Store;

/// <summary>
/// The read-only cloud provider of spec 8.3, over a real socket rather than a method call, because the
/// facts worth pinning are the URL it builds and what it does with a response that is not a 200.
/// <para>
/// It lays the SAME two-level shard out under an HTTP base address as the file system provider does on
/// disk, so one tree serves both, and it VERIFIES NOTHING: the address check is the reader's, one layer up,
/// which is what keeps a store a transport and a reader the integrity chain.
/// </para>
/// </summary>
public class HttpPackStoreTests
{
    [Fact]
    public async Task GetAsync_fetches_by_the_same_two_level_shard_as_the_file_system_store()
    {
        using var origin = await PackHttpHost.StartAsync();
        CatalogPack pack = CatalogPack.Build();
        await pack.WriteAsync(origin.Store);
        using HttpClient client = origin.Client();
        var store = new HttpPackStore(client, origin.BaseAddress);

        ReadOnlyMemory<byte>? bytes = await store.GetAsync(pack.TagChunk.Hash);

        Assert.NotNull(bytes);
        Assert.Equal(pack.TagChunk.StoredFile, bytes.Value.ToArray());
        string hash = pack.TagChunk.Hash;
        Assert.Equal(
            "GET /" + hash[..2] + "/" + hash.Substring(2, 2) + "/" + hash + ".kec",
            Assert.Single(origin.Requests));
    }

    [Fact]
    public async Task GetAsync_answers_null_for_a_404_rather_than_throwing()
    {
        using var origin = await PackHttpHost.StartAsync();
        using HttpClient client = origin.Client();
        var store = new HttpPackStore(client, origin.BaseAddress);

        Assert.Null(await store.GetAsync(new string('a', 64)));
        Assert.False(await store.ExistsAsync(new string('a', 64)));
    }

    [Fact]
    public async Task GetAsync_answers_null_when_the_body_arrives_truncated()
    {
        using var origin = await PackHttpHost.StartAsync();
        CatalogPack pack = CatalogPack.Build();
        await pack.WriteAsync(origin.Store);
        origin.TruncateBodies = true;
        using HttpClient client = origin.Client();
        var store = new HttpPackStore(client, origin.BaseAddress);

        Assert.Null(await store.GetAsync(pack.TagChunk.Hash));
    }

    // A store is a TRANSPORT. Bytes that arrived whole are handed back whatever they say, and the content
    // address is checked one layer up, by the reader or by the caching decorator. A store that verified would
    // decompress every fetch twice and would still not be the thing the integrity chain hangs on.
    [Fact]
    public async Task GetAsync_verifies_nothing_because_the_address_check_is_the_readers()
    {
        using var origin = await PackHttpHost.StartAsync();
        CatalogPack pack = CatalogPack.Build();
        await pack.WriteAsync(origin.Store);
        byte[] tampered = pack.TagChunk.StoredFile.ToArray();
        tampered[^1] ^= 0xFF;
        origin.Serve(pack.TagChunk.Hash, tampered);
        using HttpClient client = origin.Client();
        var store = new HttpPackStore(client, origin.BaseAddress);

        ReadOnlyMemory<byte>? bytes = await store.GetAsync(pack.TagChunk.Hash);

        Assert.NotNull(bytes);
        Assert.Equal(tampered, bytes.Value.ToArray());
        Assert.False(ContentPackReader.TryVerify(bytes.Value.Span, pack.TagChunk.Hash, out string? reason));
        Assert.Equal(ContentPackReader.ReasonHashMismatch, reason);
    }

    [Fact]
    public async Task ExistsAsync_asks_with_a_HEAD_and_never_downloads_the_body()
    {
        using var origin = await PackHttpHost.StartAsync();
        CatalogPack pack = CatalogPack.Build();
        await pack.WriteAsync(origin.Store);
        using HttpClient client = origin.Client();
        var store = new HttpPackStore(client, origin.BaseAddress);

        Assert.True(await store.ExistsAsync(pack.TagChunk.Hash));
        Assert.StartsWith("HEAD ", Assert.Single(origin.Requests), StringComparison.Ordinal);
        Assert.Equal(0, origin.BytesServed);
    }

    [Fact]
    public async Task A_name_that_is_not_a_content_address_never_becomes_a_request()
    {
        using var origin = await PackHttpHost.StartAsync();
        using HttpClient client = origin.Client();
        var store = new HttpPackStore(client, origin.BaseAddress);

        Assert.Null(await store.GetAsync("../../etc/passwd"));
        Assert.False(await store.ExistsAsync("../../etc/passwd"));
        Assert.Empty(origin.Requests);
    }

    // What makes it OBVIOUSLY a fetch path rather than a half-working publish target. Both throw on the CALL
    // rather than on enumeration, so a publisher pointed at a CDN by mistake finds out at the first write.
    [Fact]
    public async Task PutAsync_and_ListAsync_throw_NotSupportedException()
    {
        using var origin = await PackHttpHost.StartAsync();
        using HttpClient client = origin.Client();
        var store = new HttpPackStore(client, origin.BaseAddress);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => store.PutAsync(new string('a', 64), new byte[] { 1 }));
        Assert.Throws<NotSupportedException>(() => store.ListAsync(1));
    }

    [Fact]
    public async Task GetVersionPointerAsync_reads_the_versions_pointer_by_the_same_rule_as_the_file_store()
    {
        using var origin = await PackHttpHost.StartAsync();
        CatalogPack pack = CatalogPack.Build();
        await pack.WriteAsync(origin.Store);
        using HttpClient client = origin.Client();
        var store = new HttpPackStore(client, origin.BaseAddress);

        PackVersionPointer? pointer = await store.GetVersionPointerAsync(CatalogPack.VersionNumber);

        Assert.NotNull(pointer);
        Assert.Equal(pack.ServerManifestHash, pointer.ServerManifestHash);
        Assert.Equal(pack.ClientManifestHash, pointer.ClientManifestHash);
        Assert.Equal("GET /versions/7", Assert.Single(origin.Requests));
    }

    [Fact]
    public async Task GetVersionPointerAsync_answers_null_when_the_pointer_is_absent_or_is_not_two_addresses()
    {
        using var origin = await PackHttpHost.StartAsync();
        using HttpClient client = origin.Client();
        var store = new HttpPackStore(client, origin.BaseAddress);

        Assert.Null(await store.GetVersionPointerAsync(7));
        Assert.Null(await store.GetVersionPointerAsync(0));

        origin.ServePath("/versions/7", System.Text.Encoding.UTF8.GetBytes("not-a-hash\nalso-not\n"));
        Assert.Null(await store.GetVersionPointerAsync(7));
    }

    // Spec 13.4: a redirect from a content-addressed store is either a misconfiguration or a redirection
    // attack, and the hash check would catch the latter only after the request was made. The engine's own
    // client factory is what makes that true rather than a comment.
    [Fact]
    public async Task The_clients_the_store_builds_follow_no_redirect()
    {
        using var origin = await PackHttpHost.StartAsync();
        CatalogPack pack = CatalogPack.Build();
        await pack.WriteAsync(origin.Store);
        origin.RedirectEverythingTo("/elsewhere");
        using HttpClient client = HttpPackStore.CreateClient(origin.BaseAddress);
        var store = new HttpPackStore(client);

        Assert.Equal(origin.BaseAddress, store.BaseAddress);
        Assert.Null(await store.GetAsync(pack.TagChunk.Hash));
        Assert.Single(origin.Requests);
    }

    [Fact]
    public void A_store_with_no_base_address_anywhere_refuses_to_be_built()
    {
        using var client = new HttpClient();
        Assert.Throws<ArgumentException>(() => new HttpPackStore(client));
        Assert.Throws<ArgumentNullException>(() => new HttpPackStore(null!, new Uri("http://example/")));
    }

    /// <summary>
    /// A local HTTP endpoint over a <see cref="FileSystemPackStore"/> root, which is the CDN's stand-in and
    /// nothing more: content-addressed GETs, no range support, no caching headers. It is started and stopped
    /// INSIDE the test that uses it, so nothing outlives the assertion.
    /// </summary>
    sealed class PackHttpHost : IDisposable
    {
        readonly HttpListener _listener = new();
        readonly ConcurrentQueue<string> _requests = new();
        readonly Dictionary<string, byte[]> _overrides = new(StringComparer.Ordinal);
        readonly TemporaryRoot _root = new();
        Task? _loop;
        long _bytesServed;

        PackHttpHost(int port)
        {
            BaseAddress = new Uri(FormattableString.Invariant($"http://127.0.0.1:{port}/"));
            _listener.Prefixes.Add(BaseAddress.AbsoluteUri);
            Store = new FileSystemPackStore(_root.Path);
        }

        /// <summary>The store the endpoint serves, which is an ordinary sharded tree on disk.</summary>
        public FileSystemPackStore Store { get; }

        /// <summary>The base the pack store is pointed at.</summary>
        public Uri BaseAddress { get; }

        /// <summary>Every request served, as <c>"&lt;method&gt; &lt;path&gt;"</c>, in arrival order.</summary>
        public IReadOnlyCollection<string> Requests => _requests;

        /// <summary>How many body bytes went out, which is 0 for a HEAD.</summary>
        public long BytesServed => Interlocked.Read(ref _bytesServed);

        /// <summary>Whether every response declares more bytes than it sends and then drops the connection.</summary>
        public bool TruncateBodies { get; set; }

        string? _redirectTo;

        public static async Task<PackHttpHost> StartAsync()
        {
            var host = new PackHttpHost(FreePort());
            host._listener.Start();
            host._loop = Task.Run(host.AcceptAsync);
            await Task.Yield();
            return host;
        }

        /// <summary>Serves these bytes under that hash, whatever the store on disk holds.</summary>
        public void Serve(string hash, byte[] bytes) =>
            ServePath("/" + hash[..2] + "/" + hash.Substring(2, 2) + "/" + hash + ".kec", bytes);

        /// <summary>Serves these bytes at that exact path.</summary>
        public void ServePath(string path, byte[] bytes)
        {
            lock (_overrides)
            {
                _overrides[path] = bytes;
            }
        }

        /// <summary>Answers every request with a 302 to <paramref name="path"/>, spec 13.4's case.</summary>
        public void RedirectEverythingTo(string path) => _redirectTo = path;

        public void Dispose()
        {
            if (_listener.IsListening)
            {
                _listener.Stop();
            }

            try
            {
                _loop?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // The accept loop ends with the listener.
            }

            _listener.Close();
            _root.Dispose();
        }

        /// <summary>A client with the endpoint as its base, and a short timeout so a hang is a failure.</summary>
        public HttpClient Client() => new() { BaseAddress = BaseAddress, Timeout = TimeSpan.FromSeconds(10) };

        static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        async Task AcceptAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (HttpListenerException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (InvalidOperationException)
                {
                    return;
                }

                await ServeAsync(context).ConfigureAwait(false);
            }
        }

        async Task ServeAsync(HttpListenerContext context)
        {
            string path = context.Request.Url?.AbsolutePath ?? string.Empty;
            _requests.Enqueue(context.Request.HttpMethod + " " + path);

            if (_redirectTo is string target)
            {
                context.Response.StatusCode = 302;
                context.Response.RedirectLocation = target;
                context.Response.Close();
                return;
            }

            byte[]? bytes = Read(path);
            if (bytes is null)
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            if (context.Request.HttpMethod == "HEAD")
            {
                context.Response.ContentLength64 = bytes.Length;
                context.Response.Close();
                return;
            }

            context.Response.ContentLength64 = TruncateBodies ? bytes.Length + 64 : bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            Interlocked.Add(ref _bytesServed, bytes.Length);
            if (TruncateBodies)
            {
                context.Response.Abort();
                return;
            }

            context.Response.Close();
        }

        byte[]? Read(string path)
        {
            lock (_overrides)
            {
                if (_overrides.TryGetValue(path, out byte[]? served))
                {
                    return served;
                }
            }

            string relative = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            string file = Path.Combine(Store.Root, relative);
            return File.Exists(file) ? File.ReadAllBytes(file) : null;
        }
    }
}
