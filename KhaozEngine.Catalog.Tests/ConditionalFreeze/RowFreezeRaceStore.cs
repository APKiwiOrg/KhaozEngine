using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;

namespace KhaozEngine.Tests.Catalog.ConditionalFreeze;

/// <summary>
/// A race participant that is a row store plus the upgrade ledger and deliberately NOT a text store, so a
/// runner over it freezes through the row-only path whatever its plan carries.
/// <para>
/// It publishes one of two ways. Built over itself, a publish takes the row route, which is the console
/// publisher whose freeze step reads the draft and then overwrites the marker. Forwarded, a publish runs on
/// the inner engine store's own pipeline, which is the shape of a game's upgrade wrapper that adds the ledger
/// to a store and leaves publishing to it.
/// </para>
/// </summary>
/// <param name="inner">The participant's engine store, which must keep the upgrade ledger.</param>
/// <param name="ids">The id persistence matching <paramref name="inner"/>.</param>
/// <param name="registry">The catalog's registry.</param>
/// <param name="packs">The pack target a publish built over this decorator writes to.</param>
/// <param name="forwardPublish">Whether a publish is forwarded to the inner store rather than built over this one.</param>
internal sealed class RowFreezeRaceStore(
    IContentAuthoringStore inner,
    IContentIdPersistence ids,
    ContentTypeRegistry registry,
    IPackStore packs,
    bool forwardPublish)
    : FreezeRaceParticipant(inner, ids, registry, packs)
{
    /// <summary>Whether a publish is forwarded to the inner store rather than built over this decorator.</summary>
    public bool ForwardsPublish => forwardPublish;

    /// <inheritdoc />
    public override Task<ContentPublishResult> PublishAsync(
        ContentPublishRequest request,
        CancellationToken cancellationToken = default)
        => forwardPublish
            ? Inner.PublishAsync(request, cancellationToken)
            : PublishOverSelfAsync(request, cancellationToken);
}
