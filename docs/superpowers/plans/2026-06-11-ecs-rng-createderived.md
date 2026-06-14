# DeterministicRng.CreateDerived Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `DeterministicRng.CreateDerived(string systemName)` to `KhaozEngine.Ecs`, returning a new generator whose stream is a stable, isolated function of the parent's construction seed and the name.

**Architecture:** Retain the constructor seed in a new `private readonly ulong _seed` field. `CreateDerived` computes `_seed ^ (ulong)(uint)StableHash(systemName)` and constructs a fresh `DeterministicRng` from it; the existing splitmix64 expansion decorrelates the derived stream. `StableHash` is a platform-stable DJB2-xor hash ported verbatim from Nullwake's `GameRng` (deliberately not `string.GetHashCode`, which is per-process randomized).

**Tech Stack:** C# / net10.0, xUnit. No new dependencies.

**Spec:** `docs/superpowers/specs/2026-06-11-ecs-rng-createderived-design.md`

**Working dir:** worktree `.claude/worktrees/ecs-rng-createderived` on branch `worktree-ecs-rng-createderived`. Baseline: 268 tests pass.

**Release discipline (Batch 2):** Do NOT edit `Directory.Build.props` `<Version>`, do NOT touch `CHANGELOG.md`, do NOT `dotnet pack`. The coordinating chat owns the 3.3.0 release.

---

## File Structure

- **Modify** `KhaozEngine.Ecs/DeterministicRng.cs` - add `_seed` field, `CreateDerived` method, `StableHash` helper.
- **Modify** `KhaozEngine.Tests/DeterministicRngTests.cs` - add the named-stream tests.

No other files change. The known-vector value in Task 6 is captured from a real run, not guessed.

---

### Task 1: Retain the seed and add `CreateDerived` (behavioral tests first)

These five behavioral tests do not depend on exact numbers, so they are written and made to
pass together before pinning the known vector in Task 2.

**Files:**
- Modify: `KhaozEngine.Ecs/DeterministicRng.cs`
- Test: `KhaozEngine.Tests/DeterministicRngTests.cs`

- [ ] **Step 1: Write the failing tests**

Add these five tests to the `DeterministicRngTests` class in
`KhaozEngine.Tests/DeterministicRngTests.cs` (the file already has `using System.Linq;`,
`using KhaozEngine.Ecs;`, `using Xunit;`):

```csharp
    [Fact]
    public void CreateDerived_SameNameSameStream()
    {
        var a = new DeterministicRng(2024).CreateDerived("combat");
        var b = new DeterministicRng(2024).CreateDerived("combat");
        var sa = Enumerable.Range(0, 16).Select(_ => a.NextULong()).ToArray();
        var sb = Enumerable.Range(0, 16).Select(_ => b.NextULong()).ToArray();
        Assert.Equal(sa, sb);
    }

    [Fact]
    public void CreateDerived_DifferentNamesDifferentStreams()
    {
        var parent = new DeterministicRng(2024);
        var combat = parent.CreateDerived("combat");
        var ore = parent.CreateDerived("oreField");
        var sc = Enumerable.Range(0, 16).Select(_ => combat.NextULong()).ToArray();
        var so = Enumerable.Range(0, 16).Select(_ => ore.NextULong()).ToArray();
        Assert.NotEqual(sc, so);
    }

    [Fact]
    public void CreateDerived_DifferentParentSeedsDifferentStreams()
    {
        var a = new DeterministicRng(1).CreateDerived("combat");
        var b = new DeterministicRng(2).CreateDerived("combat");
        var sa = Enumerable.Range(0, 16).Select(_ => a.NextULong()).ToArray();
        var sb = Enumerable.Range(0, 16).Select(_ => b.NextULong()).ToArray();
        Assert.NotEqual(sa, sb);
    }

    [Fact]
    public void CreateDerived_StreamsAreIndependent()
    {
        // Drain one named stream from one parent; an untouched parent's same-named
        // stream must be unaffected, and a sibling name must match across both parents.
        var p1 = new DeterministicRng(42);
        var combat1 = p1.CreateDerived("combat");
        var ore1 = p1.CreateDerived("oreField");
        for (int i = 0; i < 50; i++) combat1.NextULong();

        var p2 = new DeterministicRng(42);
        var ore2 = p2.CreateDerived("oreField");

        var s1 = Enumerable.Range(0, 32).Select(_ => ore1.NextULong()).ToArray();
        var s2 = Enumerable.Range(0, 32).Select(_ => ore2.NextULong()).ToArray();
        Assert.Equal(s1, s2);
    }

    [Fact]
    public void CreateDerived_IsOrderIndependent()
    {
        // Derivation comes from the construction seed, not live draw state:
        // draining the parent first must not change the derived stream.
        var early = new DeterministicRng(7).CreateDerived("combat");

        var parent = new DeterministicRng(7);
        for (int i = 0; i < 100; i++) parent.NextULong();
        var late = parent.CreateDerived("combat");

        var se = Enumerable.Range(0, 16).Select(_ => early.NextULong()).ToArray();
        var sl = Enumerable.Range(0, 16).Select(_ => late.NextULong()).ToArray();
        Assert.Equal(se, sl);
    }

    [Fact]
    public void CreateDerived_NullNameThrows()
    {
        Assert.Throws<System.ArgumentNullException>(
            () => new DeterministicRng(1).CreateDerived(null!));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~CreateDerived"`
Expected: BUILD FAILURE - `'DeterministicRng' does not contain a definition for 'CreateDerived'`.

- [ ] **Step 3: Implement `_seed`, `CreateDerived`, and `StableHash`**

In `KhaozEngine.Ecs/DeterministicRng.cs`:

Change the field line:
```csharp
    private ulong _s0, _s1;
```
to:
```csharp
    private readonly ulong _seed;
    private ulong _s0, _s1;
```

In the constructor, capture the seed as the first statement (before the splitmix expansion):
```csharp
    public DeterministicRng(ulong seed)
    {
        _seed = seed;
        ulong z = seed;
        _s0 = SplitMix(ref z);
        _s1 = SplitMix(ref z);
        if ((_s0 | _s1) == 0) _s1 = 1;   // xorshift state must not be all-zero
    }
```

Add the new method and helper just after `Next(int minInclusive, int maxExclusive)`
(line 49) and before the `private static ulong SplitMix` helper:
```csharp
    /// <summary>
    /// Returns a new generator whose stream is a stable function of THIS generator's
    /// construction seed and <paramref name="systemName"/>. The same construction seed and
    /// name always yield the same stream; different names (or different parent seeds) yield
    /// decorrelated streams. Derivation uses the construction seed, not the live draw state,
    /// so the result is independent of how many numbers this generator has drawn and is NOT
    /// affected by a <see cref="State"/> restore. Lets each subsystem own an isolated,
    /// reproducible stream (e.g. "combat", "oreField").
    /// </summary>
    /// <param name="systemName">
    /// Stable subsystem identifier. Changing it shifts the stream. Empty string is allowed;
    /// must not be null.
    /// </param>
    public DeterministicRng CreateDerived(string systemName)
    {
        ArgumentNullException.ThrowIfNull(systemName);
        ulong derivedSeed = _seed ^ (ulong)(uint)StableHash(systemName);
        return new DeterministicRng(derivedSeed);
    }

    /// <summary>
    /// Platform-stable string hash (DJB2 xor variant). Deterministic across runs, .NET
    /// versions, and platforms - unlike <see cref="string.GetHashCode()"/>, which is
    /// randomized per process.
    /// </summary>
    private static int StableHash(string s)
    {
        unchecked
        {
            int hash = 5381;
            for (int i = 0; i < s.Length; i++)
                hash = ((hash << 5) + hash) ^ s[i];
            return hash;
        }
    }
```

Note: `ArgumentNullException` is in the `System` namespace; the file has no `using System;`
but `ArgumentNullException.ThrowIfNull` resolves via the implicit global usings of net10.0.
If the build reports it unresolved, add `using System;` at the top.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~CreateDerived"`
Expected: PASS, 6 tests (the 5 behavioral + the null-throw).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Ecs/DeterministicRng.cs KhaozEngine.Tests/DeterministicRngTests.cs
git commit -m "Add DeterministicRng.CreateDerived named-stream factory"
```

---

### Task 2: Pin the derivation with a known-value vector

Locks the full derivation (hash + combine + splitmix + xorshift) so it can never silently
drift. Same pattern as the existing `KnownVectorLocksAlgorithm` test: capture the real output
once, then assert it.

**Files:**
- Test: `KhaozEngine.Tests/DeterministicRngTests.cs`

- [ ] **Step 1: Add the pin test with a placeholder vector**

Add to `DeterministicRngTests`:
```csharp
    [Fact]
    public void CreateDerived_KnownVectorLocksDerivation()
    {
        // Captured from the implementation: DeterministicRng(42).CreateDerived("combat").
        // Locks hash (DJB2-xor) + combine (seed ^ (uint)hash) + splitmix64 + xorshift128+.
        var r = new DeterministicRng(42).CreateDerived("combat");
        ulong[] expected = { 0, 0, 0 };   // placeholder - replaced in Step 2
        Assert.Equal(expected, new[] { r.NextULong(), r.NextULong(), r.NextULong() });
    }
```

- [ ] **Step 2: Capture the real vector and replace the placeholder**

Run the test once to print the actual values, then paste them into `expected`:

```bash
dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj \
  --filter "FullyQualifiedName~CreateDerived_KnownVectorLocksDerivation" \
  --logger "console;verbosity=detailed" 2>&1 | grep -A3 "Assert.Equal"
```

The failure message prints the actual `ulong[]`. Copy those three values into the `expected`
array, replacing `{ 0, 0, 0 }`. (Alternatively, temporarily change the assert to
`Assert.Equal(new ulong[]{1,2,3}, ...)` to force a print, or read the three values off the
failure diff.) Do not hand-compute - capture from the run.

- [ ] **Step 3: Run to verify the pin passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~CreateDerived_KnownVectorLocksDerivation"`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add KhaozEngine.Tests/DeterministicRngTests.cs
git commit -m "Pin DeterministicRng.CreateDerived derivation with known vector"
```

---

### Task 3: Full-suite verification

**Files:** none (verification only).

- [ ] **Step 1: Run the entire test suite**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS, **275 total** (268 baseline + 7 new), 0 failed, 0 skipped.

- [ ] **Step 2: Confirm no release-discipline files were touched**

Run: `git diff --name-only origin/main`
Expected: exactly these four -
```
KhaozEngine.Ecs/DeterministicRng.cs
KhaozEngine.Tests/DeterministicRngTests.cs
docs/superpowers/specs/2026-06-11-ecs-rng-createderived-design.md
docs/superpowers/plans/2026-06-11-ecs-rng-createderived.md
```
No `Directory.Build.props`, `CHANGELOG.md`, `docs/CONSUMERS.md`, or `.slnx` changes.

---

## Self-Review

- **Spec coverage:** derivation formula (Task 1 Step 3), retain-seed semantics (Task 1 Step 3
  + order-independence test), all 7 spec tests mapped (Task 1: same-name, different-names,
  different-seeds, independence, order-independence, null-throws; Task 2: known-vector).
  Scope guard (Task 3 Step 2). Covered.
- **Placeholders:** the only placeholder is the deliberate `{ 0, 0, 0 }` vector, captured
  from a real run in Task 2 Step 2 (mirrors the existing `KnownVectorLocksAlgorithm` workflow).
  No prose placeholders.
- **Type consistency:** `CreateDerived(string) : DeterministicRng`, `StableHash(string) : int`,
  `_seed : ulong` used consistently across all tasks. `ArgumentNullException` namespace noted.
- **Open item (not blocking):** `StableHash` stays `private` per spec recommendation; the
  coordinator may later choose to expose it as `public static`.
