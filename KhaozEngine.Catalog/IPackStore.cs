using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog;

/// <summary>
/// How hard a provider works to keep a written file across a power cut, spec 8.2. The default is the cheap
/// mode DELIBERATELY: a pack file lost to a power cut is refetchable from its own hash, and a world file is
/// not, which is why this enum exists beside <c>MapSaveDurability</c> rather than sharing its default.
/// </summary>
public enum PackDurability
{
    /// <summary>Write and move, with no flush to disk. The ordinary mode.</summary>
    Default = 0,

    /// <summary>Flush to disk before the move, for a store that is the only copy of a version.</summary>
    PowerFail = 1,
}

/// <summary>
/// The two manifest hashes of one published version, read from the <c>versions/&lt;n&gt;</c> pointer of
/// publish step 9 (spec 6.9). It is the ONE object in a store that is not named by its own hash, and it
/// exists because a content-addressed store has no version index and cannot derive one.
/// <para>
/// A client NEVER reads it. A client learns its manifest hash from the connect door, which is the
/// authenticated channel the whole trust chain hangs on, so a mutable name in the store is never in the
/// integrity path and a pointer an attacker rewrote costs the publisher's own sweep and nothing else.
/// </para>
/// </summary>
/// <param name="ServerManifestHash">The version's server manifest hash, lower hex.</param>
/// <param name="ClientManifestHash">The version's client manifest hash, lower hex, never equal to the server one.</param>
public sealed record PackVersionPointer(string ServerManifestHash, string ClientManifestHash)
{
    /// <summary>
    /// Reads a pointer file: the two manifest hashes, one per line. Null for anything else, which is what
    /// makes an absent or malformed pointer answer the same way as an unreadable one, because a listing
    /// built on half a pointer is how a publish sweep deletes a live pack.
    /// <para>
    /// It lives HERE, on the pointer, because both providers read the same file: the local store off disk
    /// and the HTTP store off a <c>versions/&lt;n&gt;</c> GET. One writer and two readers is exactly the
    /// shape a second copy of the parse would rot in.
    /// </para>
    /// </summary>
    /// <param name="file">The pointer file's bytes, UTF-8.</param>
    public static PackVersionPointer? TryRead(ReadOnlySpan<byte> file)
    {
        string text;
        try
        {
            text = System.Text.Encoding.UTF8.GetString(file);
        }
        catch (ArgumentException)
        {
            return null;
        }

        string[] lines = text.Split('\n');
        if (lines.Length < 2)
        {
            return null;
        }

        string server = lines[0].TrimEnd('\r');
        string client = lines[1].TrimEnd('\r');
        return FileSystemPackStore.IsContentAddress(server) && FileSystemPackStore.IsContentAddress(client)
            ? new PackVersionPointer(server, client)
            : null;
    }
}

/// <summary>
/// The content-addressed pack store of spec 8.1: four members and no more, so a provider is a fetch path, a
/// publish target or both, and nothing in between.
/// <para>
/// <c>hash</c> is the 64 character lower hex of whatever the object's own digest rule is (spec 7.8), so the
/// name IS the content and a provider that will write anything under any name is not content addressed.
/// </para>
/// <para>
/// <b>There is no delete here</b>, deliberately. Pruning is <see cref="IPackStorePruning"/>, which a provider
/// MAY implement, so a read-only provider cannot be asked to prune and a misconfigured one cannot delete a
/// production pack through the common interface.
/// </para>
/// </summary>
public interface IPackStore
{
    /// <summary>Whether the store already holds this hash, which is what makes a republish of an unchanged chunk free.</summary>
    Task<bool> ExistsAsync(string hash, CancellationToken cancellationToken = default);

    /// <summary>
    /// The object's bytes, or NULL for absent rather than a throw, so a sweep or a validation pass is never
    /// taken down by one lookup.
    /// </summary>
    Task<ReadOnlyMemory<byte>?> GetAsync(string hash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the object. IDEMPOTENT: putting a hash that exists is a no-op, and a provider may verify rather
    /// than rewrite.
    /// <para>
    /// <b>It VERIFIES the digest of the bytes it was handed</b> and throws <see cref="ContentPackException"/>
    /// on a mismatch. That is the check that makes every later verify-on-read meaningful.
    /// </para>
    /// </summary>
    /// <exception cref="ContentPackException">The bytes do not digest to <paramref name="hash"/>.</exception>
    Task PutAsync(string hash, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every hash ONE version names: its two manifest hashes and every hash named inside either manifest.
    /// It answers from the <c>versions/&lt;n&gt;</c> pointer (spec 6.9) rather than by walking the store,
    /// because a store's job is to say what a version contains and not what happens to be on disk.
    /// <para>
    /// An EMPTY sequence means the listing failed, which is the publish sweep's own skip condition (spec
    /// 6.12): a provider that cannot enumerate, a pointer that is absent or unreadable, and a manifest the
    /// pointer names that cannot be read all answer the same way. Deleting files on the authority of a
    /// listing that failed is how a bad publish turns into a lost pack.
    /// </para>
    /// </summary>
    IAsyncEnumerable<string> ListAsync(int versionNumber, CancellationToken cancellationToken = default);
}

/// <summary>
/// The pruning half of a store, which a provider implements only when deleting from it is safe. The publish
/// sweep of spec 6.12 needs BOTH halves: <see cref="IPackStore.ListAsync"/> gives it the keep set, one
/// version at a time, and <see cref="EnumerateAsync"/> gives it what is actually there, so the difference is
/// the orphan set.
/// <para>
/// It is a SEPARATE interface rather than two more members on <see cref="IPackStore"/> so that a read-only
/// provider cannot be asked to prune at all, which is a compile-time fact rather than a runtime throw.
/// </para>
/// </summary>
public interface IPackStorePruning
{
    /// <summary>Every hash the store actually holds, in no defined order. This is the half that finds orphans.</summary>
    IAsyncEnumerable<string> EnumerateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes one object, answering whether it was there. A version pointer is never a candidate, because a
    /// pointer is how the NEXT sweep finds its version again.
    /// </summary>
    Task<bool> DeleteAsync(string hash, CancellationToken cancellationToken = default);
}
