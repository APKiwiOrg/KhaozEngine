# DeterministicFpScope: cross-platform canonical FP environment

Date: 2026-06-21
Status: approved (design)
Worktree/branch: `feature/deterministic-fp`

## Problem

All three consumer games (SpaceGame, Hardpoint, Nullwake) run deterministic fixed-tick host
sims plus a lockstep/replay model. SpaceGame's determinism tripwire is flaky: for a fixed seed
+ fixed scripted input (seed 99173, 3600 ticks), the final state is non-deterministic *across
process invocations on the same machine and build* (FinalHealth 81.68 vs 61.68; two distinct
hashes). The same isolated test produced 81.68 ten times in one window and 61.68 ten times in a
later window with no code/build/seed/input change.

Ruled out by direct experiment in SpaceGame: config-load races, RNG (DeterministicRng is pure),
shared static buffers, thread identity, per-World component-id registry, and JIT tiering alone
(`DOTNET_TieredCompilation=0` did not stabilize it).

Conclusion: the sim's FP math gives different low-bit results depending on uncontrolled
runtime/CPU FP state. Leading hypothesis is per-thread FP control-register state (ARM64 FPCR
flush-to-zero/rounding; x86 MXCSR). The sim damps pilot velocity toward zero each tick, so
near-denormal intermediates are plausible, and FTZ differences compound over 3600 ticks into a
~20 HP swing. This is also a latent multiplayer lockstep desync risk (host vs client on
different machines/threads).

This is generic, cross-platform determinism infra every current and future game needs. It is
engine work, not a per-game fix.

## Goal

Provide a cheap, allocation-free way to force the floating-point environment into a single
canonical state (round-to-nearest-even, FTZ/DAZ off, default exception masking) for the duration
of a sim tick or sim-thread, and restore the prior state afterward. Plus an FP audit of the
engine's deterministic path and a repro harness that proves the fix.

## Mechanism decision

There is no managed API for MXCSR (x64) / FPCR (arm64), so any solution touches native. Three
routes were considered:

1. **Embedded machine-code stub** (hand-written instruction bytes run via function pointer).
   *Rejected.* Spiked on the arm64 dev Mac: `mmap(MAP_JIT)` succeeded but
   `pthread_jit_write_protect_np` SIGBUS'd (exit 138). macOS hardened-runtime JIT entitlement
   gymnastics would be pushed onto every consuming game's apphost. Too fragile.
2. **Native shim per RID** (tiny C compiled to .dylib/.so/.dll, bundled like GLFW).
   *Rejected for now.* The package-publishing CI (`ci.yml`) runs ubuntu-only and packs there;
   a shim forces a new multi-OS build-natives-and-upload pipeline AND changes the release ritual
   (local `dotnet pack` to `local-feed` from the Mac would carry only the Mac native). Heavy
   permanent machinery for ~4 instructions. Kept as the documented fallback if a platform ever
   misbehaves: the public API does not change if internals swap to a shim.
3. **libc/ucrt `fenv` P/Invoke** (`fegetenv`/`fesetenv`/`fesetround`). **Chosen.** Pure managed,
   no native build, packs through the existing ubuntu pipeline and into `local-feed` from the Mac
   with zero cross-compilation. The IEEE default FP environment each platform's libc restores *is*
   the required canonical state (round-to-nearest, denormals not flushed, FP traps off).

We cannot read FPCR directly to prove a bit flips (that needs the crashing codegen path), and we
do not need to: the repro harness is the proof. Run the mini-sim N times with the scope active
and assert one identical hash. Green = fixed. If it stays flaky, the cause is JIT codegen
variance, which the register fix cannot cover and which the FP-audit guidance addresses instead.

## Package

New `KhaozEngine.Determinism` package.

- Keeps `KhaozEngine.Primitives` zero-dependency / System.Numerics-only (it would otherwise gain
  P/Invoke).
- References `KhaozEngine.Primitives` only (for any shared hashing helper if needed; otherwise
  zero deps beyond the BCL).
- Added to the `Foundation`, `Game2D`, and `Game3D` umbrella metapackages so all three games get
  it transitively. (Not `Server` unless a server sim needs it — include it too since servers run
  authoritative lockstep sims. Decision: include in all four umbrellas.)
- `<Version>$(KhaozEngineVersion)</Version>`, rides the shared version line.

## Public API

`DeterministicFpScope`, allocation-free. The saved environment is held inline in a
`readonly struct` token (no heap allocation).

```csharp
namespace KhaozEngine.Determinism;

public readonly struct DeterministicFpScope : IDisposable
{
    // RAII per-tick / per-run. Saves current FP env, applies canonical, restores on Dispose.
    public static DeterministicFpScope Enter();

    public void Dispose();   // restores the saved environment
}

public static class DeterministicFp
{
    // Set-once-per-thread-entry form. Applies canonical state and returns a token holding the
    // prior environment so the caller can restore explicitly later (e.g. on thread teardown).
    public static FpEnvToken SetCanonical();

    // Restore a previously captured environment.
    public static void Restore(in FpEnvToken token);

    // True if FP control is supported on this platform/arch (else Enter()/SetCanonical() are
    // safe no-ops that still restore correctly). Lets games assert in debug.
    public static bool IsSupported { get; }
}

public readonly struct FpEnvToken { /* opaque, holds saved fenv_t bytes inline */ }
```

Usage:

```csharp
using (DeterministicFpScope.Enter()) { sim.Tick(dt); }     // per tick / per run

var prior = DeterministicFp.SetCanonical();                // per sim-thread entry
try { RunSimLoop(); } finally { DeterministicFp.Restore(prior); }
```

## Internals

P/Invoke `fegetenv` / `fesetenv` / `fesetround` from `libc` (mac/linux) / `ucrtbase` (windows).
`fenv_t` is stored inline as a fixed-size byte buffer sized to the largest platform `fenv_t`
(arm64 macOS = 8 bytes; x64 glibc = 28 bytes; ucrt differs) — pick a safe upper bound (e.g. 64
bytes) so the struct is layout-stable across platforms.

Canonical state applied per-arch:

- **arm64** (primary dev machine): a zeroed `fenv_t` is canonical (FPCR=0 -> round-to-nearest,
  FTZ off, FP traps off). No symbol resolution needed; directly testable on the dev Mac via the
  harness. Apply with `fesetenv(&zeroed)`.
- **x64**: `fesetenv(FE_DFL_ENV)` restores default MXCSR (FTZ/DAZ off, masked exceptions, RN).
  `FE_DFL_ENV` is a per-platform pointer: Linux glibc/musl = `(fenv_t*)-1`; Windows = resolved
  from ucrt or a constructed default as fallback. Plus `fesetround(FE_TONEAREST)` belt-and-braces.

`IsSupported` is false on any arch/platform where the canonical state cannot be applied; on those
`Enter()`/`SetCanonical()` are no-ops whose Dispose/Restore are also no-ops (never corrupt state).

All calls are plain libc function calls (no JIT memory), so they do not hit the SIGBUS path the
stub spike exposed.

## FP audit (deliverable)

Review and document the engine's deterministic per-tick FP path:

- `DeterministicRng`: pure integer xorshift128+; only FP op is `NextDouble`'s single multiply by a
  constant (`(NextULong() >> 11) * (1.0/2^53)`). Deterministic; FTZ-irrelevant (no denormals).
  Note in the audit as already safe.
- `MathUtil` (`Clamp01`/`Lerp`/`InverseLerp`): simple deterministic scalar ops. Safe.
- Identify any other engine code that does per-tick FP a consumer sim hits.

Add a "Deterministic floating point" section to `docs/USING-KHAOZENGINE.md`:
- What `DeterministicFpScope` controls (register state) and what it does NOT (JIT codegen
  variance: FMA contraction via `MathF.FusedMultiplyAdd`, auto-vectorization differences).
- Guidance for game sim code: wrap the tick/run in the scope; prefer separate-op IEEE semantics
  over fused ops in sim math; avoid relying on `System.Numerics.Vector*` reductions whose lane
  order/codegen can vary, for state that must be bit-reproducible.

## Repro harness (deliverable, test)

`KhaozEngine.Tests` headless test mirroring SpaceGame's failure shape:

- A fixed-seed, fixed-input mini host-sim that damps a velocity toward zero each tick (drives
  near-denormal intermediates), runs a fixed tick count, and hashes the final state.
- Assert byte-identical hash across:
  - repeated runs in-process (loop N times),
  - main thread vs thread-pool thread vs dedicated thread,
  all with `DeterministicFpScope` active.
- A control path WITHOUT the scope documents the pre-fix behavior (kept as a non-asserting
  observation or a skipped/explanatory test, since the flakiness is environment-dependent and may
  not reproduce in CI — it must NOT make CI flaky).
- The `DOTNET_TieredCompilation` 0 vs 1 matrix is exercised via a CI step that runs the
  determinism test under both env values (not via an in-test toggle, which is not possible).

## Acceptance

- Repro harness produces a byte-identical hash across repeated runs, main vs pool vs dedicated
  threads, and `DOTNET_TieredCompilation` 0 and 1, with the scope active.
- `DeterministicFpScope` works on arm64 (verified on dev Mac) and x64.
- No regression to existing engine tests.
- New API reachable from `Game2D`/`Game3D`/`Foundation` (and `Server`) umbrellas.

## Release (per CLAUDE.md ritual)

Minor (additive) bump of `<KhaozEngineVersion>`. CHANGELOG + CHANGENOTES in the same commit.
Update the three guard-checked declarations (`docs/CONSUMERS.md` engine current version,
`docs/ROADMAP.md` current released version, `README.md` PackageReference example). New package
wired into the four umbrellas. `dotnet pack -c Release -o ./local-feed`, commit, tag `vX.Y.Z`,
push main + tag.

## Out of scope (consumer-side, separate session)

SpaceGame pins the new version, wraps its host-sim tick/run in `DeterministicFpScope`, re-captures
the determinism baseline ONCE under the now-stable FP, re-tightens RichScenario bands, adds a
PLAY_CHANGELOG note. Until then neither 81.68 nor 61.68 is trustworthy. SpaceGame commit ebc8301
(atomic config snapshot) is unrelated and stays.
