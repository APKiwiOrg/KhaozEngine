# DeterministicRng.CreateDerived — design

Batch 2, item 9. Add a named-stream factory to the existing `KhaozEngine.Ecs.DeterministicRng`
so each game subsystem can pull its own reproducible RNG stream without interfering with
others. Ports the determinism semantics of Nullwake's `GameRng` into the engine's
higher-quality generator.

## Background

`KhaozEngine.Ecs.DeterministicRng` is a xorshift128+ generator seeded via splitmix64
(`KhaozEngine.Ecs/DeterministicRng.cs`). It is reproducible across .NET versions and
platforms (unlike `System.Random`). The constructor takes a `ulong seed`, immediately
expands it into the `(_s0, _s1)` xorshift state, and discards the seed. It has no concept
of named sub-streams.

Nullwake's `GameRng` (`Nullwake.Core/Engine/GameRng.cs`) solves the named-stream problem
for a different generator: it holds an `int` player seed and `Create(name)` returns a
`System.Random` seeded with `playerSeed ^ StableHash(name)`. `StableHash` is a
platform-stable DJB2-xor hash (it deliberately avoids `string.GetHashCode`, which is
randomized per process). A separate Batch 2 effort migrates Nullwake onto
`DeterministicRng`; this item only adds the engine capability it will depend on.

## Goal

`DeterministicRng.CreateDerived(string systemName)` returns a new `DeterministicRng` whose
stream is a stable, well-isolated function of the parent's seed and `systemName`.

## Derivation

The constructor retains its seed in a new `private readonly ulong _seed` field. The derived
seed is:

```
derivedSeed = _seed ^ (ulong)(uint)StableHash(systemName)
return new DeterministicRng(derivedSeed)
```

`StableHash` is ported verbatim from Nullwake, kept `private`, returns `int`:

```
int hash = 5381;
for each char c in s:  hash = ((hash << 5) + hash) ^ c;   // unchecked
```

The derived seed is run through the existing splitmix64 expansion in the constructor, whose
avalanche fully decorrelates the derived xorshift state from the parent and from sibling
streams (a single-bit seed difference produces an unrelated stream).

### Why derive from the retained seed, not from live `(_s0, _s1)` state

"Stable per name" must hold regardless of *when* `CreateDerived` is called. Deriving from
the live xorshift state would make a derived stream depend on how many numbers the parent
had already drawn — an order-dependent footgun. Deriving from the immutable construction
seed mirrors Nullwake's semantics exactly: order-independent, isolated per name,
reproducible. The cost is one `ulong` field.

`_seed` is set only in the constructor; the existing `State` setter (used for save/resume)
does not touch it. Consequence: `CreateDerived` always derives from the seed passed to the
constructor, not from a `State`-restored value. This is documented in the XML comment. A
game that persists and restores a *root* RNG should reconstruct it from the same seed (the
normal pattern), so this is not a practical limitation.

## Public API

```csharp
/// <summary>
/// Returns a new generator whose stream is a stable function of this generator's
/// construction seed and <paramref name="systemName"/> ... (order-independent;
/// derives from the construction seed, not from State).
/// </summary>
public DeterministicRng CreateDerived(string systemName);
```

`systemName` is a stable identifier (e.g. `"combat"`, `"oreField"`). Changing it shifts the
stream — same contract as Nullwake. Null `systemName` throws `ArgumentNullException`
(`StableHash` would otherwise NRE on `s.Length`); empty string is permitted and hashes to
the `5381` seed constant.

## Testing

Headless tests added to the existing `KhaozEngine.Tests/DeterministicRngTests.cs`:

1. **Same name → identical stream** — two parents with the same seed, derive the same name,
   compare N draws.
2. **Different names → different streams** — `"combat"` vs `"oreField"` from one parent.
3. **Different parent seeds, same name → different streams.**
4. **Stream independence** — deriving and draining one named stream does not perturb another
   named stream taken from a fresh parent (mirrors Nullwake's isolation test).
5. **Order independence** — drawing from the parent before `CreateDerived` does not change
   the derived stream (proves derive-from-seed, not derive-from-state).
6. **Known-vector pin** — `new DeterministicRng(42).CreateDerived("combat")` produces a
   hard-coded `ulong[]`, locking the entire derivation (hash + combine + splitmix +
   xorshift) against drift across runs/platforms. Vector captured from the first
   implementation run, same pattern as the existing `KnownVectorLocksAlgorithm` test.
7. **Null name throws** `ArgumentNullException`.

## Scope

- Modify `KhaozEngine.Ecs/DeterministicRng.cs` in place (add `_seed`, `CreateDerived`,
  `StableHash`).
- Add tests to `KhaozEngine.Tests/DeterministicRngTests.cs`.
- No new package, no `.slnx`/`.csproj` wiring, no `<Version>`/CHANGELOG/`dotnet pack`
  (the coordinating chat owns the batched 3.3.0 release).

## Items surfaced to the coordinator (not decided here)

- **The exact derivation is now a public contract.** `derivedSeed = constructionSeed ^
  (uint)StableHash(name)`, `StableHash` = DJB2-xor (`5381`, `((h<<5)+h)^c`). The
  Nullwake-migration effort depends on this exact derivation.
- **Derived streams will NOT byte-match Nullwake's old `System.Random` streams** — different
  generator algorithm. The determinism contract is preserved, but any golden values or
  save-state compatibility in Nullwake that depended on actual sequences will shift when it
  migrates. Flag to whoever does that migration.
- **Open question:** keep `StableHash` `private` (recommended, YAGNI) or expose it as a
  `public static` for reuse elsewhere?
