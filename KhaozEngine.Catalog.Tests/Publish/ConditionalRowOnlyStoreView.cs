using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;

namespace KhaozEngine.Tests.Catalog.Publish;

/// <summary>
/// A <see cref="RowOnlyStoreView"/> that also declares the guarded freeze companion, which is the shape of a
/// row-only provider that can still be published through. It deliberately keeps hiding the text companion, so a
/// publish over it takes the row route and the text representability checks stay exercised.
/// <para>
/// Its constructor requires an inner store that declares the companion, and both members forward there. Both
/// are recorded as calls, so a read-only check against <see cref="RowOnlyStoreView.WriteMembers"/> sees them.
/// </para>
/// </summary>
internal class ConditionalRowOnlyStoreView : RowOnlyStoreView, IContentConditionalDraftFreeze
{
    readonly IContentConditionalDraftFreeze _guarded;

    /// <summary>Wraps a store that declares the guarded freeze companion.</summary>
    /// <param name="inner">The real store every member is answered from.</param>
    /// <exception cref="ArgumentException"><paramref name="inner"/> does not declare the companion.</exception>
    public ConditionalRowOnlyStoreView(IContentAuthoringStore inner)
        : base(inner)
    {
        _guarded = inner as IContentConditionalDraftFreeze
            ?? throw new ArgumentException("a conditional row-only view's inner store must declare the guarded freeze companion.", nameof(inner));
    }

    /// <inheritdoc />
    public virtual Task<ContentDraft> FreezeDraftForBaseAsync(
        int expectedBaseVersion,
        CancellationToken cancellationToken = default)
    {
        Record(nameof(FreezeDraftForBaseAsync));
        return _guarded.FreezeDraftForBaseAsync(expectedBaseVersion, cancellationToken);
    }

    /// <inheritdoc />
    public virtual Task<bool> ReleaseDraftFreezeForBaseAsync(
        int frozenForBaseVersion,
        CancellationToken cancellationToken = default)
    {
        Record(nameof(ReleaseDraftFreezeForBaseAsync));
        return _guarded.ReleaseDraftFreezeForBaseAsync(frozenForBaseVersion, cancellationToken);
    }
}
