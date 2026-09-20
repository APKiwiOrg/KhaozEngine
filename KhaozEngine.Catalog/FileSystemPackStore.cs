using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog;

/// <summary>
/// The local pack provider of spec 8.2: one file per hash under a two-level shard,
/// <c>&lt;root&gt;/&lt;hash[0..2]&gt;/&lt;hash[2..4]&gt;/&lt;hash&gt;.kec</c>.
/// <para>
/// Two levels of 256 because a flat directory of a thousand chunks times fifty versions is fine and a flat
/// directory of a million is not, and the shard is DERIVED from the hash so it needs no index. The cloud
/// provider lays the same tree out under an HTTP base address, so one tree serves both.
/// </para>
/// <para>
/// Writes go to a UNIQUELY NAMED temporary in the same shard directory and then
/// <c>File.Move(temp, final, overwrite: true)</c>, the map document's idiom. <c>overwrite: true</c> is
/// correct here PRECISELY because the name is the content: rewriting a hash with its own bytes is a no-op by
/// definition. An <c>fsync</c> before the move happens only under
/// <see cref="PackDurability.PowerFail"/>, because a pack file lost to a power cut is refetchable from its
/// hash and a lost world file is not.
/// </para>
/// <para>
/// <b>The temporary's name carries a random token rather than being the destination plus an extension</b>,
/// because two publishers writing ONE pack root write the same chunk hashes: the same-named temporary makes
/// the second writer's open fail, or its move find nothing where the first writer's move already took the
/// file. A per-write name makes concurrent puts of one hash two independent writes that both land on the
/// same correct bytes, which is what a content-addressed store is entitled to promise. The price is that a
/// crashed write leaves a uniquely named orphan rather than one the next write overwrites, so a successful
/// move deletes the temporaries of its OWN destination that are over an hour old.
/// </para>
/// <para>
/// <b>The version pointer lives OUTSIDE the shard tree</b>, under <c>versions/</c>, because a shard name is
/// derived from a hash and a version number is not one. Writing a pointer is provider-specific rather than a
/// fifth member on <see cref="IPackStore"/>, so a read-only provider still cannot be a half-working publish
/// target. The READ half of the pointer is <see cref="IContentVersionPointerSource"/>, which this provider
/// implements and a boot over a store that does not is handed separately.
/// </para>
/// </summary>
public sealed class FileSystemPackStore : IPackStore, IPackStorePruning, IContentVersionPointerSource
{
    /// <summary>The extension every stored object carries, whatever kind it is.</summary>
    public const string FileExtension = ".kec";

    /// <summary>The directory the version pointers live in, beside the shard tree rather than inside it.</summary>
    public const string VersionDirectoryName = "versions";

    const string TemporaryExtension = ".tmp";
    const int HashCharacters = 64;

    /// <summary>
    /// How old a sibling temporary has to be before a successful write of the same destination deletes it.
    /// An hour is far longer than any single chunk write and far shorter than forever, which is how long a
    /// crashed write's uniquely named orphan would otherwise sit there.
    /// </summary>
    static readonly TimeSpan StaleTemporaryAge = TimeSpan.FromHours(1);

    /// <summary>Creates the store, creating the root directory when it is not there yet.</summary>
    /// <param name="root">The directory the shard tree and the version pointers live under.</param>
    /// <param name="durability">Whether a write flushes to disk before the move.</param>
    /// <exception cref="ArgumentException"><paramref name="root"/> is null, empty or whitespace.</exception>
    public FileSystemPackStore(string root, PackDurability durability = PackDurability.Default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = root;
        Durability = durability;
        Directory.CreateDirectory(root);
    }

    /// <summary>The directory the shard tree hangs off.</summary>
    public string Root { get; }

    /// <summary>Whether a write flushes to disk before the move.</summary>
    public PackDurability Durability { get; }

    /// <summary>
    /// Whether a string is a content address at all: exactly 64 lower hex characters. Every member here
    /// checks it BEFORE building a path, because a hash arrives from a manifest a remote peer may have
    /// written and a name that is not an address must never become a path segment.
    /// </summary>
    public static bool IsContentAddress(string? hash)
    {
        if (hash is null || hash.Length != HashCharacters)
        {
            return false;
        }

        for (int i = 0; i < hash.Length; i++)
        {
            char character = hash[i];
            if (character is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The shard-tree path one hash lands at, RELATIVE to whatever the tree hangs off and always with
    /// forward slashes: <c>&lt;hash[0..2]&gt;/&lt;hash[2..4]&gt;/&lt;hash&gt;.kec</c>, lower case, no leading
    /// slash.
    /// <para>
    /// It is the ONE statement of the layout in the engine, which is why it is static and public. Three
    /// providers derive a name from a hash (this one a file path, <see cref="HttpPackStore"/> a URL and the
    /// blob provider a container key), and the whole point of the tree is that one publisher's output is
    /// another provider's input, so a second copy of the rule would be a copy that could drift.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="hash"/> is not a content address.</exception>
    public static string RelativeKeyFor(string hash)
    {
        if (!IsContentAddress(hash))
        {
            throw new ArgumentException(
                FormattableString.Invariant($"'{hash}' is not a content address, which is 64 lower hex characters."),
                nameof(hash));
        }

        return hash[..2] + "/" + hash.Substring(2, 2) + "/" + hash + FileExtension;
    }

    /// <summary>The file one hash lands at, which is derived and never looked up.</summary>
    /// <exception cref="ArgumentException"><paramref name="hash"/> is not a content address.</exception>
    public string PathFor(string hash)
        => Path.Combine(Root, RelativeKeyFor(hash).Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The file one version's pointer lands at, outside the shard tree.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="versionNumber"/> is not positive.</exception>
    public string VersionPointerPathFor(int versionNumber)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(versionNumber);
        return Path.Combine(
            Root,
            VersionDirectoryName,
            versionNumber.ToString(CultureInfo.InvariantCulture));
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string hash, CancellationToken cancellationToken = default)
        => Task.FromResult(IsContentAddress(hash) && File.Exists(PathFor(hash)));

    /// <inheritdoc />
    public async Task<ReadOnlyMemory<byte>?> GetAsync(string hash, CancellationToken cancellationToken = default)
    {
        if (!IsContentAddress(hash))
        {
            return null;
        }

        string path = PathFor(hash);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            return new ReadOnlyMemory<byte>(bytes);
        }
        catch (IOException)
        {
            // A file that went away between the check and the read is the same answer as one that was never
            // there, because the caller's next move is to fetch it from somewhere else either way.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task PutAsync(
        string hash,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hash);

        if (!IsContentAddress(hash))
        {
            throw new ContentPackException(
                FormattableString.Invariant(
                    $"'{hash}' is not a content address, and a store that files bytes under a name that is not their digest is not content addressed."),
                hash,
                ContentPackReader.ReasonHashMismatch);
        }

        if (!ContentPackReader.TryVerify(bytes.Span, hash, out string? reason))
        {
            throw new ContentPackException(
                FormattableString.Invariant(
                    $"The {bytes.Length} bytes offered under '{hash}' do not digest to it ({reason}). Every later verify-on-read is meaningless if this one is skipped."),
                hash,
                reason);
        }

        string path = PathFor(hash);
        if (File.Exists(path))
        {
            // Putting a hash that exists is a no-op by definition: the name IS the content, so the bytes on
            // disk are already the bytes being offered.
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await WriteThenMoveAsync(path, bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> ListAsync(
        int versionNumber,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> hashes = await ReadVersionSetAsync(versionNumber, cancellationToken)
            .ConfigureAwait(false);

        for (int i = 0; i < hashes.Count; i++)
        {
            yield return hashes[i];
        }
    }

    /// <summary>
    /// Publish step 9's version pointer: the version's two manifest hashes and nothing else, written with the
    /// same temp-then-move idiom as every other file so a reader never sees a half-written one.
    /// </summary>
    /// <exception cref="ContentPackException">Either hash is not a content address.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="versionNumber"/> is not positive.</exception>
    public async Task PutVersionPointerAsync(
        int versionNumber,
        string serverManifestHash,
        string clientManifestHash,
        CancellationToken cancellationToken = default)
    {
        string path = VersionPointerPathFor(versionNumber);
        RequireAddress(serverManifestHash, nameof(serverManifestHash));
        RequireAddress(clientManifestHash, nameof(clientManifestHash));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] bytes = Encoding.UTF8.GetBytes(serverManifestHash + "\n" + clientManifestHash + "\n");
        await WriteThenMoveAsync(path, bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The version pointer, or null when it is absent or is not two content addresses. Null is what makes
    /// <see cref="ListAsync"/> answer empty, which is the publish sweep's own skip condition.
    /// </summary>
    public async Task<PackVersionPointer?> GetVersionPointerAsync(
        int versionNumber,
        CancellationToken cancellationToken = default)
    {
        if (versionNumber <= 0)
        {
            return null;
        }

        string path = VersionPointerPathFor(versionNumber);
        if (!File.Exists(path))
        {
            return null;
        }

        byte[] file;
        try
        {
            file = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        // The parse lives on the pointer itself, because the HTTP provider reads the same file off a
        // versions/<n> GET and two copies of a format's reader is how the two drift.
        return PackVersionPointer.TryRead(file);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> EnumerateAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The walk is the ORPHAN half of the publish sweep and is deliberately not what ListAsync answers
        // from: a version says what it contains, and the tree says what is there.
        foreach (string path in Directory.EnumerateFiles(Root, "*" + FileExtension, SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = Path.GetFileNameWithoutExtension(path);
            if (IsContentAddress(name))
            {
                yield return name;
            }
        }
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string hash, CancellationToken cancellationToken = default)
    {
        if (!IsContentAddress(hash))
        {
            return Task.FromResult(false);
        }

        string path = PathFor(hash);
        if (!File.Exists(path))
        {
            return Task.FromResult(false);
        }

        File.Delete(path);
        return Task.FromResult(true);
    }

    static void RequireAddress(string hash, string parameterName)
    {
        if (!IsContentAddress(hash))
        {
            throw new ContentPackException(
                FormattableString.Invariant(
                    $"A version pointer holds two manifest content addresses, and {parameterName} is '{hash}'."),
                hash,
                ContentPackReader.ReasonHashMismatch);
        }
    }

    static bool TryAddManifest(
        ReadOnlyMemory<byte> file,
        ContentManifestSide side,
        HashSet<string> seen,
        List<string> hashes)
    {
        if (!ContentManifestCodec.TryDecode(file.Span, side, out ContentManifest? manifest, out _))
        {
            return false;
        }

        Add(seen, hashes, manifest.RemapRuleChunkHash);
        for (int t = 0; t < manifest.Types.Count; t++)
        {
            IReadOnlyList<ManifestChunkEntry> chunks = manifest.Types[t].Chunks;
            for (int c = 0; c < chunks.Count; c++)
            {
                Add(seen, hashes, chunks[c].Hash);
            }
        }

        for (int l = 0; l < manifest.Languages.Count; l++)
        {
            Add(seen, hashes, manifest.Languages[l].TextHash);
        }

        return true;
    }

    static void Add(HashSet<string> seen, List<string> hashes, string hash)
    {
        if (seen.Add(hash))
        {
            hashes.Add(hash);
        }
    }

    async Task WriteThenMoveAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        // Per WRITE rather than per destination, so two publishers filing one hash into one root do not
        // collide on the temporary. The name ends in the temporary extension and is never a content address,
        // so the orphan walk skips it.
        string temporary = FormattableString.Invariant(
            $"{path}.{Guid.NewGuid():n}{TemporaryExtension}");
        try
        {
            await using (var stream = new FileStream(
                temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                if (Durability == PackDurability.PowerFail)
                {
                    stream.Flush(flushToDisk: true);
                }
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            TryDeleteTemporary(temporary);
            throw;
        }

        SweepStaleTemporaries(path);
    }

    /// <summary>
    /// Deletes the temporaries of THIS destination that a crashed write left behind. A per-write name is
    /// what stops two publishers colliding, and the price of it is that a killed process leaves a uniquely
    /// named orphan where an overwrite used to take care of itself.
    /// <para>
    /// <b>Only ones older than an hour, and only after a successful move.</b> A temporary a second writer
    /// has open right now is seconds or minutes old, and deleting one would break a write that was going to
    /// succeed. Every error here is ignored: a leftover temporary is never read, because a reader only ever
    /// asks for a content address, so failing a completed write over one would be the worse answer.
    /// </para>
    /// <para>
    /// <b>The errors are ignored ONE FILE at a time.</b> A shared pack root can hold an orphan this process
    /// is not allowed to delete, and a sweep that abandoned the rest of the listing at the first of them
    /// would never catch up with the ones it can delete.
    /// </para>
    /// </summary>
    /// <param name="path">The destination whose siblings are swept.</param>
    static void SweepStaleTemporaries(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (directory is null)
        {
            return;
        }

        string[] siblings;
        try
        {
            siblings = Directory.GetFiles(
                directory, Path.GetFileName(path) + ".*" + TemporaryExtension);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        DateTime before = DateTime.UtcNow - StaleTemporaryAge;
        for (int i = 0; i < siblings.Length; i++)
        {
            try
            {
                if (File.GetLastWriteTimeUtc(siblings[i]) < before)
                {
                    File.Delete(siblings[i]);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    static void TryDeleteTemporary(string temporary)
    {
        try
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
        catch (IOException)
        {
            // A leftover temp file is never read, because a reader only ever asks for a content address.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    async Task<IReadOnlyList<string>> ReadVersionSetAsync(int versionNumber, CancellationToken cancellationToken)
    {
        PackVersionPointer? pointer = await GetVersionPointerAsync(versionNumber, cancellationToken)
            .ConfigureAwait(false);
        if (pointer is null)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var hashes = new List<string>();
        Add(seen, hashes, pointer.ServerManifestHash);
        Add(seen, hashes, pointer.ClientManifestHash);

        bool read = await TryAddManifestAsync(
            pointer.ServerManifestHash, ContentManifestSide.Server, seen, hashes, cancellationToken)
            .ConfigureAwait(false);
        if (read)
        {
            read = await TryAddManifestAsync(
                pointer.ClientManifestHash, ContentManifestSide.Client, seen, hashes, cancellationToken)
                .ConfigureAwait(false);
        }

        // A PARTIAL list is the dangerous answer, because the sweep deletes everything outside the keep set.
        // One unreadable manifest therefore empties the whole listing, which skips the sweep.
        return read ? hashes : [];
    }

    async Task<bool> TryAddManifestAsync(
        string hash,
        ContentManifestSide side,
        HashSet<string> seen,
        List<string> hashes,
        CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte>? file = await GetAsync(hash, cancellationToken).ConfigureAwait(false);
        return file is not null && TryAddManifest(file.Value, side, seen, hashes);
    }
}
