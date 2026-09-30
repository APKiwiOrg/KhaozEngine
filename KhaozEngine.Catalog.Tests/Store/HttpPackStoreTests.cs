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

    /// <summary>
    /// The host finds its port by opening a probe on port 0, releasing it, and then binding a listener to
    /// that number, so anything that takes the port in between wins it and the bind throws
    /// <c>Address already in use</c>. That turned a whole selective run red once, on a diff that touches
    /// none of this.
    /// <para>
    /// Here the first port handed out is one this test is HOLDING, which is that race with the timing taken
    /// out of it. The start has to probe and bind again rather than throw, and what comes back has to be a
    /// working endpoint on a different port rather than merely an object.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_start_that_loses_its_probed_port_takes_a_fresh_one()
    {
        var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        int taken = ((IPEndPoint)occupied.LocalEndpoint).Port;
        try
        {
            int handed = 0;
            using PackHttpHost origin = await PackHttpHost.StartAsync(
                () => Interlocked.Increment(ref handed) == 1 ? taken : PackHttpHost.FreePort());

            Assert.Equal(2, handed);
            Assert.NotEqual(taken, origin.BaseAddress.Port);

            using HttpClient client = origin.Client();
            var store = new HttpPackStore(client, origin.BaseAddress);
            Assert.Null(await store.GetAsync(new string('a', 64)));
        }
        finally
        {
            occupied.Stop();
        }
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
    /// The transfer ceiling of spec 7.6 and 8.4, which is the ONE bound that applies before a byte is
    /// buffered. Every other length check in the format runs inside <c>ContentPackReader.TryVerify</c>,
    /// which the caching store calls only once the whole body is already in hand, so a store that buffers
    /// first has no bound at all: a hostile origin declaring 256 MiB gets 256 MiB allocated, times the
    /// bounded concurrency of four.
    /// <para>
    /// Joins <c>AllocSensitive</c> because the declared-oversize case reads
    /// <see cref="GC.GetTotalAllocatedBytes(bool)"/>, which is process wide.
    /// </para>
    /// </summary>
    [Collection("AllocSensitive")]
    public class Ceiling
    {
        // The reproduction, spec 8.4 and 13.4. The first request in a process warms several megabytes of
        // client machinery, so a control fetch runs first and the measurement is of the SECOND request: an
        // origin that declares 256 MiB and serves 64 MiB of it used to cost about 316 MB of buffer growth,
        // answering null at the end of it, and four of those run at once.
        [Fact]
        public async Task A_declared_oversize_body_answers_null_without_allocating_it()
        {
            using var origin = await PackHttpHost.StartAsync();
            string control = new('e', 64);
            string oversize = new('a', 64);
            origin.ServePlan(control, declaredLength: 4096, actualBytes: 4096);
            origin.ServePlan(oversize, declaredLength: 256L * 1024 * 1024, actualBytes: 64L * 1024 * 1024);
            using HttpClient client = origin.Client();
            var store = new HttpPackStore(client, origin.BaseAddress);
            Assert.NotNull(await store.GetAsync(control));

            long before = GC.GetTotalAllocatedBytes(precise: true);
            ReadOnlyMemory<byte>? bytes = await store.GetAsync(oversize);
            long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

            Assert.Null(bytes);
            Assert.True(
                allocated < 4L * 1024 * 1024,
                FormattableString.Invariant($"A refused 256 MiB body allocated {allocated} bytes."));
        }

        // A chunked response declares no length at all, so the header check cannot be the only bound: the
        // read itself stops at the ceiling and answers the same null.
        [Fact]
        public async Task A_chunked_oversize_body_answers_null()
        {
            using var origin = await PackHttpHost.StartAsync();
            string hash = new('b', 64);
            origin.ServePlan(hash, declaredLength: -1, actualBytes: ContentPackFormat.MaxObjectBytes + 4096L);
            using HttpClient client = origin.Client();
            var store = new HttpPackStore(client, origin.BaseAddress);

            Assert.Null(await store.GetAsync(hash));
        }

        // The ceiling is a ceiling and not a budget: the largest object the format can legally produce still
        // fetches whole, which is what keeps the bound from becoming a refusal of real content.
        [Fact]
        public async Task A_body_exactly_at_the_ceiling_still_fetches()
        {
            using var origin = await PackHttpHost.StartAsync();
            string hash = new('c', 64);
            origin.ServePlan(
                hash,
                ContentPackFormat.MaxObjectBytes,
                ContentPackFormat.MaxObjectBytes);
            using HttpClient client = origin.Client();
            var store = new HttpPackStore(client, origin.BaseAddress);

            ReadOnlyMemory<byte>? bytes = await store.GetAsync(hash);

            Assert.NotNull(bytes);
            Assert.Equal(ContentPackFormat.MaxObjectBytes, bytes.Value.Length);
        }

        // The ceiling is the format's own, not a number this store invented: the largest uncompressed chunk
        // plus the largest fixed header any pack file carries. A stored body is never larger than the
        // uncompressed bytes it decompresses to, because every codec keeps the canonical file when Brotli
        // does not shrink it.
        [Fact]
        public void The_ceiling_is_the_chunk_ceiling_plus_the_largest_fixed_header()
        {
            Assert.Equal(
                ContentPackFormat.MaxChunkUncompressedBytes + ContentManifestCodec.FixedHeaderBytes,
                ContentPackFormat.MaxObjectBytes);
            Assert.Equal(ContentPackFormat.MaxObjectBytes, HttpPackStore.MaxObjectBytes);
            Assert.True(ContentManifestCodec.FixedHeaderBytes >= ContentPackFormat.ChunkHeaderBytes);
            Assert.True(ContentManifestCodec.FixedHeaderBytes >= ContentPackFormat.RuleHeaderBytes);
            Assert.True(
                ContentManifestCodec.FixedHeaderBytes
                >= ContentPackFormat.TextHeaderFixedBytes + ContentTextChunkCodec.MaxLanguageTagBytes);
        }
    }

    /// <summary>
    /// A local HTTP endpoint over a <see cref="FileSystemPackStore"/> root, which is the CDN's stand-in and
    /// nothing more: content-addressed GETs, no range support, no caching headers. It is started and stopped
    /// INSIDE the test that uses it, so nothing outlives the assertion.
    /// </summary>
    sealed class PackHttpHost : IDisposable
    {
        /// <summary>
        /// How many ports a start will try before it gives up. A handful covers losing the race to another
        /// suite on the same machine, and stopping short of forever keeps a machine with nothing free
        /// reporting that rather than spinning.
        /// </summary>
        const int BindAttempts = 8;

        readonly HttpListener _listener = new();
        readonly ConcurrentQueue<string> _requests = new();
        readonly Dictionary<string, byte[]> _overrides = new(StringComparer.Ordinal);
        readonly Dictionary<string, BodyPlan> _plans = new(StringComparer.Ordinal);
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

        public static Task<PackHttpHost> StartAsync() => StartAsync(FreePort);

        /// <summary>
        /// The same start over a named source of ports, so the race test can hand out one it is HOLDING and
        /// see what a start that loses the port does.
        /// <para>
        /// The probe and the bind are ONE unit here. A port the probe found free is not reserved: it is
        /// released before the listener can take it, and anything on the machine may win it in between. So
        /// a bind that fails takes a FRESH port rather than retrying the one that lost, up to
        /// <see cref="BindAttempts"/> times, and the last attempt throws the way it always did rather than
        /// hiding a machine with no free port at all.
        /// </para>
        /// </summary>
        /// <param name="ports">Where each attempt's port comes from.</param>
        public static async Task<PackHttpHost> StartAsync(Func<int> ports)
        {
            ArgumentNullException.ThrowIfNull(ports);

            for (int attempt = 1; ; attempt++)
            {
                var host = new PackHttpHost(ports());
                try
                {
                    host._listener.Start();
                }
                catch (HttpListenerException) when (attempt < BindAttempts)
                {
                    host.Dispose();
                    continue;
                }
                catch (HttpListenerException)
                {
                    // The last attempt throws the way it always did, but the host it built already owns a
                    // temporary root, and a throw past it leaves that directory behind for the rest of the
                    // run. Dispose first, then let the throw stand.
                    host.Dispose();
                    throw;
                }

                host._loop = Task.Run(host.AcceptAsync);
                await Task.Yield();
                return host;
            }
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

        /// <summary>
        /// Serves a body of zeros under that hash by PLAN rather than from an array, so a test can name a
        /// size no test process should ever hold. <paramref name="declaredLength"/> is what the response
        /// declares, negative for a chunked response that declares nothing, and
        /// <paramref name="actualBytes"/> is what the socket actually carries.
        /// </summary>
        public void ServePlan(string hash, long declaredLength, long actualBytes)
        {
            string path = "/" + hash[..2] + "/" + hash.Substring(2, 2) + "/" + hash + ".kec";
            lock (_plans)
            {
                _plans[path] = new BodyPlan(declaredLength, actualBytes);
            }
        }

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

            try
            {
                _listener.Close();
            }
            catch (HttpListenerException)
            {
                // A listener that never bound has nothing to close, which is exactly the host a start
                // disposes after losing the port race. The close is what threw the day this was reported.
            }

            _root.Dispose();
        }

        /// <summary>A client with the endpoint as its base, and a short timeout so a hang is a failure.</summary>
        public HttpClient Client() => new() { BaseAddress = BaseAddress, Timeout = TimeSpan.FromSeconds(10) };

        /// <summary>
        /// A port nothing held at the moment it was asked for. It is not a RESERVATION: the probe has to
        /// release the port before the listener can bind it, so anything on the machine may take it in
        /// between, which is why a start that loses it probes again.
        /// </summary>
        public static int FreePort()
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

            if (Plan(path) is BodyPlan plan && context.Request.HttpMethod != "HEAD")
            {
                await ServePlanAsync(context, plan).ConfigureAwait(false);
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

        /// <summary>
        /// Writes the plan's zeros in blocks. A client that stops reading at its own ceiling breaks the
        /// write, which is the fact under test rather than a failure of the endpoint.
        /// </summary>
        async Task ServePlanAsync(HttpListenerContext context, BodyPlan plan)
        {
            if (plan.DeclaredLength < 0)
            {
                context.Response.SendChunked = true;
            }
            else
            {
                context.Response.ContentLength64 = plan.DeclaredLength;
            }

            byte[] block = new byte[64 * 1024];
            long written = 0;
            try
            {
                while (written < plan.ActualBytes)
                {
                    int take = (int)Math.Min(block.Length, plan.ActualBytes - written);
                    await context.Response.OutputStream.WriteAsync(block.AsMemory(0, take)).ConfigureAwait(false);
                    // Flushed, so the headers reach the client before an abort can take the connection with
                    // them. A declared length nobody ever reads would test nothing.
                    await context.Response.OutputStream.FlushAsync().ConfigureAwait(false);
                    written += take;
                }
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }

            Interlocked.Add(ref _bytesServed, written);
            if (plan.DeclaredLength >= 0 && written < plan.DeclaredLength)
            {
                // Fewer bytes than declared, so the connection dies rather than completing a short response.
                context.Response.Abort();
                return;
            }

            context.Response.Close();
        }

        BodyPlan? Plan(string path)
        {
            lock (_plans)
            {
                return _plans.TryGetValue(path, out BodyPlan plan) ? plan : null;
            }
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

        /// <summary>A body described by its two lengths, so a huge one costs the test no array.</summary>
        /// <param name="DeclaredLength">What the response declares, negative for chunked.</param>
        /// <param name="ActualBytes">What the socket carries.</param>
        readonly record struct BodyPlan(long DeclaredLength, long ActualBytes);
    }
}
