using System;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The GUARDED row freeze companion of <see cref="IContentAuthoringStore"/>: a freeze conditioned on the base version
/// its publisher expects, and a release that clears only the marker that publisher recorded. Both members are atomic
/// inside the store's own gate or transaction, so no read outside the call decides its write.
/// <para>
/// <b>It is the guarded alternative to the legacy pair.</b> <see cref="IContentAuthoringStore.FreezeDraftAsync"/>
/// overwrites any marker without comparing the base, and <see cref="IContentAuthoringStore.ClearDraftFreezeAsync"/>
/// clears any marker, so an older publisher could overwrite or release a newer publisher's freeze. These members
/// cannot: the freeze refuses a base the store has moved past and the release leaves a marker naming another base.
/// </para>
/// <para>
/// <b>A declaring type guarantees both contracts for every instance.</b> A decorator declares this interface only
/// when its constructor requires an inner store that declares it, and it forwards both members to that store.
/// </para>
/// </summary>
public interface IContentConditionalDraftFreeze : IContentAuthoringStore
{
    /// <summary>
    /// The row-only freeze, guarded. In one atomic step and in this order it compares
    /// <paramref name="expectedBaseVersion"/> with the active version, checks the open draft holds row work, runs the
    /// row-only representability check, writes the marker and reads the draft back.
    /// <para>
    /// <b>A refusal writes nothing.</b> Neither the marker nor the draft's update time moves, even when the draft holds
    /// a STALE marker naming an older base. Unlike the text freeze, this member runs no stale-marker sweep. A call that
    /// passes every check replaces whatever marker stood, a stale one included, and a marker already naming
    /// <paramref name="expectedBaseVersion"/> is written with the same value, leaving its update time alone.
    /// </para>
    /// <para>
    /// <b>A lost acknowledgement is possible.</b> A cancelled or faulted call either wrote nothing or wrote the marker
    /// and lost its answer, so a caller records <paramref name="expectedBaseVersion"/> before the call and releases it
    /// through <see cref="ReleaseDraftFreezeForBaseAsync"/> on every exit path.
    /// </para>
    /// </summary>
    /// <param name="expectedBaseVersion">The base version the publisher stands on. Base 0 is valid, because the first publish freezes at 0.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// The open draft exactly as <see cref="IContentAuthoringStore.GetOpenDraftAsync"/> would return it after the write,
    /// read in the same step. It is never null, its <see cref="ContentDraft.FrozenForBaseVersion"/> equals
    /// <paramref name="expectedBaseVersion"/>, and on a text-capable store it carries the draft's text state.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="expectedBaseVersion"/> is negative.</exception>
    /// <exception cref="ContentAuthoringException">
    /// <see cref="ContentAuthoringException.BaseVersionMovedReason"/> when the store does not stand at
    /// <paramref name="expectedBaseVersion"/>. <see cref="ContentAuthoringException.NoOpenDraftReason"/> when no draft is
    /// open or the open draft holds no row edit, including a draft holding only text.
    /// <see cref="ContentAuthoringException.TextUnrepresentedReason"/> when the draft holds text intents or language
    /// introductions, or forks a row that holds text at the active version.
    /// </exception>
    Task<ContentDraft> FreezeDraftForBaseAsync(int expectedBaseVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// One atomic compare-and-clear: the marker is cleared if and only if a draft is open and its marker equals
    /// <paramref name="frozenForBaseVersion"/>. It compares the marker alone and never reads the active version, so a
    /// stale marker is released by the base it names.
    /// <para>
    /// It never touches the draft's base version, row edits, text state, language introductions or opener, and it
    /// writes no audit row. The draft's update time moves only when the call clears a marker.
    /// </para>
    /// </summary>
    /// <param name="frozenForBaseVersion">The base the caller's own freeze recorded. Base 0 is valid.</param>
    /// <param name="cancellationToken">Cancels the call. The engine pipelines pass <see cref="CancellationToken.None"/>.</param>
    /// <returns>True when this call cleared a marker, false otherwise, including on any repeat.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="frozenForBaseVersion"/> is negative.</exception>
    Task<bool> ReleaseDraftFreezeForBaseAsync(int frozenForBaseVersion, CancellationToken cancellationToken = default);
}
