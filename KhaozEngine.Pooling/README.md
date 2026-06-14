# KhaozEngine.Pooling

Game-agnostic free-list object pool. Zero dependencies.

- **`ObjectPool<T>`** where `T : class, IPoolable` - fixed-capacity pool built up front from a factory.
  O(1) `Rent()` (returns `null` when exhausted) and `Return(item)`. Active items are kept compacted via
  swap-removal, so `GetActive(i)` over `ActiveCount` visits every live item with no gaps.
- **`IPoolable`** - `PoolIndex` (owned by the pool) + `Reset()` (called on return).

```csharp
sealed class Particle : IPoolable
{
    public int PoolIndex { get; set; } = -1;
    public void Reset() { /* clear per-rental state */ }
}

var pool = new ObjectPool<Particle>(() => new Particle(), prewarmCount: 256);

Particle? p = pool.Rent();          // null if exhausted
// ... use p ...
for (int i = 0; i < pool.ActiveCount; i++) Update(pool.GetActive(i));
pool.Return(p!);                    // resets and frees the slot
```

Part of [KhaozEngine](https://github.com/APKiwi/KhaozEngine).
