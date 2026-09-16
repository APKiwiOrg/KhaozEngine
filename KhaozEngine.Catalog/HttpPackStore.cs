using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog;

/// <summary>
/// The read-only cloud provider of spec 8.3, over ONE injected <see cref="HttpClient"/>. The base address is
/// any HTTP-addressable container: a blob container with public read or a shared-access signature, a bucket,
/// or a CDN in front of either.
/// <para>
/// <c>GetAsync</c> issues <c>GET &lt;base&gt;/&lt;hash[0..2]&gt;/&lt;hash[2..4]&gt;/&lt;hash&gt;.kec</c>, the
/// SAME two-level shard as <see cref="FileSystemPackStore"/>, so one tree serves both: a publisher writes
/// locally and uploads the directory as it stands.
/// </para>
/// <para>
/// <b>No cloud SDK, and that is a package decision rather than a preference.</b> Taking a blob SDK would put
/// a third-party dependency into a <c>Foundation</c> package and force every client of every game to carry
/// it. Every blob service worth using serves an HTTP GET, and the WRITE side is the publisher's, which runs
/// on a server that can implement <see cref="IPackStore"/> over whatever SDK the game already has. So
/// <see cref="PutAsync"/> and <see cref="ListAsync"/> throw <see cref="NotSupportedException"/>, which is
/// what makes this obviously a fetch path rather than a half-working publish target.
/// </para>
/// <para>
/// <b>It verifies NOTHING</b>, deliberately. A store is a transport, and the content address is checked one
/// layer up by <see cref="ContentPackReader"/> or by <see cref="CachingPackStore"/>. A 404, a 5xx, a
/// redirect and a connection that died mid body all answer NULL rather than throwing, because the caller's
/// next move is the same for all four: try another source, or retry.
/// </para>
/// </summary>
public sealed class HttpPackStore : IPackStore, IContentVersionPointerSource
{
    /// <summary>
    /// The most bytes one stored object may be, and the point a fetch stops reading (spec 8.4, 13.4). It is
    /// <see cref="ContentPackFormat.MaxChunkUncompressedBytes"/> plus
    /// <see cref="ContentManifestCodec.FixedHeaderBytes"/>, the largest fixed header any pack file carries,
    /// because a stored body is never larger than the uncompressed bytes it decompresses to: every codec
    /// keeps the canonical file when Brotli does not shrink it, and the uncompressed ceiling is the one the
    /// decoders already enforce from the header alone.
    /// <para>
    /// A store is the ONLY layer that can apply it. Every other length check in the format runs inside
    /// <see cref="ContentPackReader.TryVerify"/>, which <see cref="CachingPackStore"/> calls once the whole
    /// body is already buffered, so an origin that declares 256 MiB gets 256 MiB allocated before one of
    /// those checks can see a byte of it, across the bounded concurrency of four.
    /// </para>
    /// </summary>
    public const int MaxObjectBytes =
        ContentPackFormat.MaxChunkUncompressedBytes + ContentManifestCodec.FixedHeaderBytes;

    /// <summary>Where a body that declared no length starts, before it grows toward the ceiling.</summary>
    const int UndeclaredStartBytes = 64 * 1024;

    readonly HttpClient client;

    /// <summary>Points the store at a container.</summary>
    /// <param name="client">
    /// The client every request goes through, which the CALLER owns and disposes.
    /// <see cref="CreateClient"/> builds one with the redirect and credential rules spec 13.4 requires.
    /// </param>
    /// <param name="baseAddress">The container's base, or null to take the client's own.</param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is null.</exception>
    /// <exception cref="ArgumentException">There is no absolute base address on either.</exception>
    public HttpPackStore(HttpClient client, Uri? baseAddress = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        Uri? resolved = baseAddress ?? client.BaseAddress;
        if (resolved is null || !resolved.IsAbsoluteUri)
        {
            throw new ArgumentException(
                "A pack store needs an absolute base address, on the store or on the client.",
                nameof(baseAddress));
        }

        this.client = client;
        BaseAddress = resolved.AbsoluteUri.EndsWith('/') ? resolved : new Uri(resolved.AbsoluteUri + "/");
    }

    /// <summary>The container the shard tree hangs off, always ending in a slash.</summary>
    public Uri BaseAddress { get; }

    /// <summary>
    /// A client for a pack container, spec 13.4: it follows NO redirect and sets NO credential. A redirect
    /// from a content-addressed store is either a misconfiguration or a redirection attack, and the hash
    /// check would catch the latter only after the request was made. The caller owns and disposes it.
    /// </summary>
    /// <param name="baseAddress">The container's base.</param>
    /// <exception cref="ArgumentNullException"><paramref name="baseAddress"/> is null.</exception>
    public static HttpClient CreateClient(Uri baseAddress)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseDefaultCredentials = false,
        };
        return new HttpClient(handler, disposeHandler: true) { BaseAddress = baseAddress };
    }

    /// <summary>The address one hash is served at, which is derived from the hash and never looked up.</summary>
    /// <exception cref="ArgumentException"><paramref name="hash"/> is not a content address.</exception>
    public Uri UriFor(string hash)
    {
        if (!FileSystemPackStore.IsContentAddress(hash))
        {
            throw new ArgumentException(
                FormattableString.Invariant($"'{hash}' is not a content address, which is 64 lower hex characters."),
                nameof(hash));
        }

        return new Uri(
            BaseAddress,
            hash[..2] + "/" + hash.Substring(2, 2) + "/" + hash + FileSystemPackStore.FileExtension);
    }

    /// <summary>The address one version's pointer is served at, outside the shard tree.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="versionNumber"/> is not positive.</exception>
    public Uri VersionPointerUriFor(int versionNumber)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(versionNumber);
        return new Uri(
            BaseAddress,
            FileSystemPackStore.VersionDirectoryName + "/" + versionNumber.ToString(CultureInfo.InvariantCulture));
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string hash, CancellationToken cancellationToken = default)
    {
        if (!FileSystemPackStore.IsContentAddress(hash))
        {
            return false;
        }

        using var request = new HttpRequestMessage(HttpMethod.Head, UriFor(hash));
        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<ReadOnlyMemory<byte>?> GetAsync(string hash, CancellationToken cancellationToken = default)
    {
        // A name that is not a content address never becomes a request, because a hash arrives from a
        // manifest a remote peer may have written and a name that is not an address must never become a path
        // segment.
        if (!FileSystemPackStore.IsContentAddress(hash))
        {
            return null;
        }

        return await FetchAsync(UriFor(hash), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Always throws. The write side of a pack store is the PUBLISHER's, and a publisher runs on a server
    /// that can implement <see cref="IPackStore"/> over whatever SDK its container already has.
    /// </summary>
    /// <exception cref="NotSupportedException">Always.</exception>
    public Task PutAsync(string hash, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "HttpPackStore is a fetch path. Publish through the store the publisher owns, and upload the shard tree it wrote.");

    /// <summary>
    /// Always throws, on the CALL rather than on enumeration, so a publisher pointed at a CDN by mistake
    /// finds out at the first sweep. The sweep is the publisher's and runs against the publisher's own store.
    /// </summary>
    /// <exception cref="NotSupportedException">Always.</exception>
    public IAsyncEnumerable<string> ListAsync(int versionNumber, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "HttpPackStore is a fetch path. The publish sweep lists and prunes the store the publisher owns.");

    /// <summary>
    /// The version pointer at <c>&lt;base&gt;/versions/&lt;n&gt;</c>, or null when it is absent, unreadable
    /// or not two content addresses. It is the one object here not named by its own hash, which is why a
    /// client never reads it: a client learns its manifest hash from the connect door instead.
    /// </summary>
    public async Task<PackVersionPointer?> GetVersionPointerAsync(
        int versionNumber,
        CancellationToken cancellationToken = default)
    {
        if (versionNumber <= 0)
        {
            return null;
        }

        ReadOnlyMemory<byte>? file = await FetchAsync(VersionPointerUriFor(versionNumber), cancellationToken)
            .ConfigureAwait(false);
        return file is null ? null : PackVersionPointer.TryRead(file.Value.Span);
    }

    async Task<ReadOnlyMemory<byte>?> FetchAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await client
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // A 404, a 5xx and a 3xx are one answer: this source does not hold it. A redirect is never
                // followed (spec 13.4), because a redirect off a content-addressed store is either a
                // misconfiguration or a redirection attack.
                return null;
            }

            long? declared = response.Content.Headers.ContentLength;
            if (declared > MaxObjectBytes)
            {
                // A REFUSAL rather than a throw, and taken from the header alone: the caller's next move is
                // the same as for a 404, and nothing is read, so a hostile declaration costs one round trip.
                return null;
            }

            using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await ReadBoundedAsync(body, declared, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // A connection that died mid body is a body that did not arrive, which is the same answer as an
            // absent object: the caller retries or tries another source either way.
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the body under <see cref="MaxObjectBytes"/>. A DECLARED length allocates exactly that (spec
    /// 8.4), and a chunked response, which declares nothing, grows to the ceiling and then answers null for
    /// the first byte past it: an origin that omits the header must not be the one origin with no bound.
    /// </summary>
    static async Task<ReadOnlyMemory<byte>?> ReadBoundedAsync(
        Stream body,
        long? declaredLength,
        CancellationToken cancellationToken)
    {
        if (declaredLength is long length)
        {
            byte[] exact = new byte[length];
            await body.ReadExactlyAsync(exact, cancellationToken).ConfigureAwait(false);
            return new ReadOnlyMemory<byte>(exact);
        }

        byte[] buffer = new byte[UndeclaredStartBytes];
        int filled = 0;
        while (true)
        {
            if (filled == buffer.Length)
            {
                if (filled >= MaxObjectBytes)
                {
                    // One byte past the ceiling settles it, and the null is written as a statement rather
                    // than as a conditional branch: a null in a conditional beside a ReadOnlyMemory binds to
                    // the implicit operator from an array and hands back an EMPTY body instead.
                    byte[] probe = new byte[1];
                    if (await body.ReadAsync(probe, cancellationToken).ConfigureAwait(false) != 0)
                    {
                        return null;
                    }

                    return new ReadOnlyMemory<byte>(buffer, 0, filled);
                }

                Array.Resize(ref buffer, (int)Math.Min((long)buffer.Length * 2, MaxObjectBytes));
            }

            int read = await body.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return new ReadOnlyMemory<byte>(buffer, 0, filled);
            }

            filled += read;
        }
    }
}
