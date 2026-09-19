using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog;

/// <summary>
/// The version numbers an AUTHORING database holds, which is the second and third arm of boot step 2's
/// precedence (spec 10.7): the pinned version when it is not null, otherwise the active version.
/// <para>
/// <b>It is optional, and that is the deployment this design recommends.</b> A version pinned in the SERVER'S
/// OWN CONFIG wins always, so a server configured with a pin and a pack store needs no authoring database at
/// boot at all: the authoring database is a TOOLING dependency. This interface is what a server that does read
/// one implements, and <c>IContentAuthoringStore</c> INHERITS it, so a host that boots off its authoring
/// database hands the boot the store itself rather than an adapter of its own
/// (https://github.com/APKiwiOrg/KhaozEngine/issues/1015).
/// </para>
/// </summary>
public interface IContentVersionDirectory
{
    /// <summary>
    /// The operator's HOLD, or null for the ordinary no-pin state. A server's own config pin wins over this
    /// one, always, and the active version is the fallback below both.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<int?> GetPinnedVersionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The active version, which is the one the last publish committed, or 0 when nothing has been published.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<int> GetActiveVersionAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// How a boot turns a version NUMBER into the manifest hash to fetch: the <c>versions/&lt;n&gt;</c> pointer of
/// publish step 9, which is the one object in a content-addressed store not named by its own hash.
/// <para>
/// The read side's store seam declares no pointer member, deliberately, so that a read-only provider cannot be
/// a half-working publish target. This is the READ half of that pointer and nothing else:
/// <see cref="FileSystemPackStore"/> implements it, a store that does not is handed one through
/// <see cref="ContentBootOptions.Pointers"/>, and a boot with neither refuses at step 3 rather than guessing.
/// </para>
/// </summary>
public interface IContentVersionPointerSource
{
    /// <summary>The version's two manifest hashes, or NULL when the pointer is absent or malformed.</summary>
    /// <param name="versionNumber">The version to read the pointer of.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<PackVersionPointer?> GetVersionPointerAsync(
        int versionNumber,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One place a world document names content, which boot step 11 resolves by KEY against the loaded version
/// (spec 3.10). The world document never carries a content ID, because ids are allocated by the authoring
/// store and a world file naming id 17 would break the moment a content database was rebuilt from a bundle.
/// </summary>
/// <param name="Source">What names it, for the operator's line: a world file path, an archetype, a marker.</param>
/// <param name="TypeKey">The content type's stable key, such as <c>item</c>.</param>
/// <param name="ContentKey">The row's stable key, which is immutable once published.</param>
public readonly record struct ContentWorldKeyReference(string Source, string TypeKey, string ContentKey);

/// <summary>
/// Everything <see cref="ContentBoot"/> needs, handed in rather than reached for, so a boot reads no ambient
/// static, no environment variable and no file of its own.
/// </summary>
public sealed class ContentBootOptions
{
    /// <summary>
    /// The registry, with step 1 already done: every content type registered. The boot FREEZES it at step 6,
    /// which is the moment the first pack loads.
    /// </summary>
    public required ContentTypeRegistry Registry { get; init; }

    /// <summary>Where the manifest and every chunk are fetched from.</summary>
    public required IPackStore Store { get; init; }

    /// <summary>The one field the active runtime is published into at step 9.</summary>
    public required ContentRuntimeHolder Holder { get; init; }

    /// <summary>
    /// This build's own server build number, checked against the version's <c>minimumServerBuild</c> at step 5.
    /// Required rather than defaulted, because a build number that quietly defaults to 0 refuses every pack
    /// that names a minimum at all.
    /// </summary>
    public required int ServerBuild { get; init; }

    /// <summary>
    /// The version pinned in the SERVER'S OWN CONFIG, which wins always. With one set, a boot needs no
    /// <see cref="Directory"/> at all.
    /// </summary>
    public int? ConfiguredVersion { get; init; }

    /// <summary>The authoring database's pinned and active versions, or null when the server reads neither.</summary>
    public IContentVersionDirectory? Directory { get; init; }

    /// <summary>
    /// Where the version pointer is read from, or null to use <see cref="Store"/> itself when it is one.
    /// </summary>
    public IContentVersionPointerSource? Pointers { get; init; }

    /// <summary>
    /// What the operator's manifest line calls the store, since a hash on its own does not say WHERE it was
    /// looked for.
    /// </summary>
    public string StoreName { get; init; } = "the pack store";

    /// <summary>
    /// Every content key the world document names, which the host loads at step 10 and the boot resolves at
    /// step 11. Empty is legal and is a world that names no content.
    /// </summary>
    public IReadOnlyList<ContentWorldKeyReference> WorldKeys { get; init; } = [];
}
