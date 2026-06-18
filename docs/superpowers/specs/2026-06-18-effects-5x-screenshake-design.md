# Graduate KhaozEngine.Effects to 5.x + ScreenShake

**Date:** 2026-06-18
**Package:** `KhaozEngine.Effects` (graduating from the 4.x line to the 5.x line)
**Status:** design approved, ready for implementation plan

## Goal

Fifth slice of the camera feel layer: **screen shake**. The roadmap homes screen shake in
`KhaozEngine.Effects`, but that package is the dead 4.x MonoGame rect-particle system (superseded by 5.x
`KhaozEngine.Particles`, referenced only by its own tests, consumed by no game). So this slice has two parts:

1. **Graduate `KhaozEngine.Effects` to the 5.x line** — retire the dead particle code, drop MonoGame, move
   the package onto `<KhaozEngine5xVersion>`.
2. **Add `ScreenShake`** — a trauma-based, deterministic screen-shake offset generator.

Out of scope (last remaining feel-layer slice, separate spec): parallax background layers.

## Part 1 — Graduate `KhaozEngine.Effects` to 5.x

The 4.x `KhaozEngine.Effects` (`PackageId KhaozEngine.Effects`, refs `MonoGame.Framework.DesktopGL` +
`KhaozEngine.Graphics`) holds a pooled rect-particle system that the MonoGame-free 5.x `KhaozEngine.Particles`
package already supersedes. Nothing consumes it but its own tests.

- **Delete the dead particle code:** `KhaozEngine.Effects/ParticleSystem.cs`, `ParticleEmission.cs`,
  `ParticleEmitterConfig.cs`, `ParticlePresets.cs`, `ParticleView.cs`.
- **Delete its tests:** `KhaozEngine.Tests/ParticleSystemTests.cs`, `KhaozEngine.Tests/ParticlePresetsTests.cs`.
- **Rewrite `KhaozEngine.Effects/KhaozEngine.Effects.csproj`:** drop the `MonoGame.Framework.DesktopGL`
  `PackageReference` and the `KhaozEngine.Graphics` `ProjectReference`; add `<Version>$(KhaozEngine5xVersion)</Version>`;
  add `<InternalsVisibleTo Include="KhaozEngine.Tests" />` (consistency with the other 5.x packages, though
  `ScreenShake` is fully public); refresh the `<Description>` to describe the 5.x effects package. Keep the
  `README.md` pack entry (update README copy to match). The `KhaozEngine.Tests` project reference to Effects
  stays (now pointing at the 5.x Effects).
- `ScreenShake` is pure `System.Numerics` + BCL, so the graduated csproj needs **no** project/package
  references at all.

**Version-line membership docs** (move `Effects` from the 4.x list to the 5.x custom-stack list):
- `Directory.Build.props` — the 4.x comment (`(Effects/Graphics/Input/Screens/Sprites/Time/UI)` →
  `(Graphics/Input/Screens/Sprites/Time/UI)`) and the 5.x comment (`...Audio, Particles, Game)` →
  `...Audio, Particles, Effects, Game)`).
- `CLAUDE.md` — the 5.x custom-stack list (add `Effects`) and the 4.x list (drop `Effects`).
- `docs/ROADMAP.md` — lines listing the 4.x packages (drop `Effects`).
- `docs/CONSUMERS.md` — same 4.x-list edits, plus the version matrix note.

(The `check-doc-versions.sh` guard only checks the three 5.x *version* declarations, not these membership
lists, so this is correctness/hygiene, not a build gate.)

## Part 2 — `ScreenShake`

A standard trauma-model shake, as a **pure offset generator** — no `Camera2D` dependency. The game composes
the offset onto its render camera each frame; shake is a transient overlay, never baked into the base
position. This keeps `KhaozEngine.Effects` dependency-light (System.Numerics + BCL, like `Particles`).

```csharp
namespace KhaozEngine.Effects;

public sealed class ScreenShake
{
    /// Per-instance seed fixes the noise phases (deterministic, no System.Random / wall-clock).
    public ScreenShake(uint seed = 1);

    /// Current trauma, 0..1.
    public float Trauma { get; }

    public float MaxOffset      { get; set; } = 30f;   // world units at trauma 1
    public float MaxAngle       { get; set; } = 0.1f;  // radians at trauma 1 (0 = positional-only)
    public float DecayPerSecond { get; set; } = 1f;    // trauma drained per second
    public float Frequency      { get; set; } = 25f;   // oscillation speed

    /// Adds trauma (e.g. on an explosion/hit); the result is clamped to [0,1]. Negative amounts are ignored.
    public void Add(float amount);

    /// Drains trauma by DecayPerSecond*dt (floored at 0) and advances the internal noise time by dt*Frequency.
    public void Update(float dt);

    /// Positional offset this frame: trauma^2 * MaxOffset * noise(time), per axis. Zero at zero trauma.
    public Vector2 Offset { get; }

    /// Rotational offset this frame (radians): trauma^2 * MaxAngle * noise(time). Zero at zero trauma.
    public float Angle { get; }
}
```

### Model

- **Magnitude is `trauma^2`** — the classic non-linear falloff (small traumas barely register; big ones
  slam, then ease out as trauma decays).
- **Noise** is deterministic and smooth: per-axis summed sines of the internal time at distinct
  seed-derived phases, bounded to `[-1, 1]`. No `System.Random`, no wall-clock — reproducible and
  headless-testable, matching the `Particles` determinism convention. Concretely, for an internal time `t`
  (advanced by `Update`) and a per-channel phase `p`:
  `noise(t, p) = 0.6*sin(t + p) + 0.4*sin(2.13*t + 1.7*p)` (range `[-1, 1]`). Channels X / Y / Angle use
  three distinct phases derived from `seed`.
- **`Add`** clamps the accumulated trauma to `[0,1]` and ignores negative input. **`Update`** drains trauma
  (`max(0, Trauma - DecayPerSecond*dt)`) and advances `t` by `dt*Frequency`. **`Offset`/`Angle`** are
  computed on read from the current trauma and `t`.

### Composition (documented usage)

```csharp
follow.Update(target, dt, vw, vh, bounds);     // base camera set by follow / room camera
shake.Update(dt);                               // advance shake
renderCamera.Position = camera.Position + shake.Offset;
renderCamera.Rotation = camera.Rotation + shake.Angle;
// ... render with renderCamera; the base `camera` is never mutated by the shake.
```

## Testing (headless)

New file `KhaozEngine.Tests/ScreenShakeTests.cs`. (The deleted particle tests are removed in Part 1.)

- `Add` raises trauma and clamps at 1 (`Add(2)` → 1; `Add(0.5)`+`Add(0.7)` → 1); negative amount is ignored.
- `Update` drains trauma by `DecayPerSecond*dt` and floors at 0 (`Add(0.3)` then `Update(1f)` with
  `DecayPerSecond=1` → trauma 0).
- Zero trauma → `Offset == Vector2.Zero` and `Angle == 0`.
- `Offset`/`Angle` bounded by `trauma^2 * MaxOffset` / `trauma^2 * MaxAngle` (|noise| <= 1).
- Magnitude scales with `trauma^2`: same seed + same time, trauma 0.5 vs trauma 1 → `|Offset|` ratio ≈ 0.25.
- Determinism: two shakes with the same seed and identical `Add`/`Update` sequences produce identical
  `Offset`.
- Oscillation: with `DecayPerSecond = 0` and trauma set, `Offset.X` changes sign across successive `Update`
  steps spanning the period (it shakes, not pushes).
- `MaxAngle = 0` → `Angle` is always 0.

## Shipping (engine release ritual)

The graduation + new feature ship together as **5.56.0** (5.x line). Because Effects is *moving onto* the
5.x line, this is the release that first versions it as 5.56.0.

1. Bump `<KhaozEngine5xVersion>` to `5.56.0`.
2. Newest-first `CHANGELOG.md` entry covering both the graduation and `ScreenShake`.
3. Update the three guard-checked declarations (CONSUMERS "Engine current version", ROADMAP "Current
   released version", README package refs) to 5.56.0.
4. Apply the Part 1 version-line membership doc edits (move Effects 4.x → 5.x in the lists above).
5. In `docs/ROADMAP.md` camera section, move the screen-shake item from "Still open" to "Shipped". (The
   separate "Screen shake (`KhaozEngine.Effects`)" section can be marked shipped too.)
6. `dotnet pack -c Release -o ./local-feed` (cumulative). Effects now packs at 5.56.0.
7. Commit, `git tag v5.56.0`. (Push at branch-finish.)

No consumer adopts immediately; the planned game with juice is the first user.

## Files

- Delete: `KhaozEngine.Effects/ParticleSystem.cs`, `ParticleEmission.cs`, `ParticleEmitterConfig.cs`,
  `ParticlePresets.cs`, `ParticleView.cs`; `KhaozEngine.Tests/ParticleSystemTests.cs`,
  `KhaozEngine.Tests/ParticlePresetsTests.cs`.
- Modify: `KhaozEngine.Effects/KhaozEngine.Effects.csproj` (graduate), `KhaozEngine.Effects/README.md`.
- New: `KhaozEngine.Effects/ScreenShake.cs`, `KhaozEngine.Tests/ScreenShakeTests.cs`.
- Release/doc: `Directory.Build.props`, `CHANGELOG.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`,
  `CLAUDE.md`.
