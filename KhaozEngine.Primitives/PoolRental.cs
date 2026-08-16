namespace KhaozEngine.Primitives;

/// <summary>
/// ONE RENTAL of one pooled item, as opposed to one SLOT of the pool. Handed out by
/// <see cref="ObjectPool{T}.TryRent"/> and handed back to <see cref="ObjectPool{T}.Return(in PoolRental{T})"/>
/// or <see cref="ObjectPool{T}.TryReturn"/>.
/// <para>
/// WHY THE CALLER HAS TO HOLD THIS, and why a generation stamped on the item cannot replace it. A pool reuses
/// the same object for successive rentals, so after <c>Return(a)</c> and a fresh <c>Rent()</c> the caller's
/// stale <c>a</c> variable and the pool's newly rented item are THE SAME REFERENCE, byte for byte. Nothing
/// read off the object can tell the finished rental from the live one, including a generation counter written
/// onto the object, because the fresh rental overwrote it. The distinguishing information has to sit outside
/// the object, in the caller's hand, which is what this value type is. See
/// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/149">#149</see>.
/// </para>
/// <para>
/// It is a <c>readonly struct</c> passed by <c>in</c>, so a rent-use-return cycle allocates nothing and the
/// pool stays usable on the zero-allocation hot paths it exists for. Copying one is free and harmless: every
/// copy names the same rental, and the first successful return retires all of them.
/// </para>
/// </summary>
/// <typeparam name="T">The pooled item type.</typeparam>
public readonly struct PoolRental<T> where T : class, IPoolable
{
    private readonly ObjectPool<T>? owner;
    private readonly T? item;

    /// <summary>Slot in the low 32 bits, rental generation in the high 32. See <see cref="Pack"/>.</summary>
    private readonly long token;

    internal PoolRental(ObjectPool<T> owner, T item, long token)
    {
        this.owner = owner;
        this.item = item;
        this.token = token;
    }

    /// <summary>
    /// The rented item, or <c>null</c> for the empty rental that <see cref="ObjectPool{T}.TryRent"/> writes
    /// when it returns <c>false</c>. Non-null whenever <c>TryRent</c> returned <c>true</c>.
    /// </summary>
    public T? Item => item;

    /// <summary>
    /// True for the empty rental (the <c>default</c> value, and what a failed <c>TryRent</c> writes). An empty
    /// rental is refused by both return paths rather than silently released.
    /// </summary>
    public bool IsEmpty => owner is null;

    /// <summary>The pool slot this rental came from, or 0 for the empty rental (whose generation never matches).</summary>
    internal int Slot => unchecked((int)(uint)token);

    /// <summary>The generation this rental was stamped with. Odd while the rental is live, see <see cref="ObjectPool{T}"/>.</summary>
    internal int Generation => unchecked((int)(token >> 32));

    /// <summary>True when this rental came from <paramref name="pool"/>. Two pools can hand out the same
    /// slot and generation pair, so the owner check is what keeps a cross-pool return from landing.</summary>
    internal bool BelongsTo(ObjectPool<T> pool) => ReferenceEquals(owner, pool);

    /// <summary>Packs a slot plus generation into the single 64-bit token. Unchecked so a generation that has
    /// wrapped past <see cref="int.MaxValue"/> packs and unpacks to the same bits it came in as.</summary>
    internal static long Pack(int slot, int generation) => unchecked(((long)generation << 32) | (uint)slot);
}
