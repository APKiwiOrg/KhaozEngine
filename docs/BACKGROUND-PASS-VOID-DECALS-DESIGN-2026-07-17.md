# Background pass + void ground decals (design, 2026-07-17)

Two releases, in order. Release 1 moves the procedural starfield from the final blit into a real
background pass and folds the background knobs into one mode enum. Release 2 adds the opt-in
virtual-plane fallback for ground decals, which is only correct once release 1 has landed.

## Why, in one paragraph

The ground-decal pass reconstructs its paint surface from the scene depth buffer, so a decal larger
than the geometry under it truncates. Hardpoint is a floating mesa in space: a sniper tower's range
ring overhangs the island edge and vanishes into the void, which reads as a broken indicator. The
fix is to project onto the decal's own horizontal plane wherever there is no geometry. That fix
cannot work today, because the final blit regenerates the background from scratch and discards
whatever was drawn there. Release 1 removes that obstacle permanently rather than working around it.

## Release 1: starfield becomes a background pass

### The problem being fixed

`PixelPostProcessSettings.Starfield` defaults to `true`. The stars are generated in the **final
blit**, after the whole post chain:

```glsl
// ShaderSources.BlitFrag
if (Params.x > 0.5 && s.a < 0.5) {      // "background" = the colour target's alpha marker
    col = BgColor.rgb + vec3(star);     // discards whatever was actually drawn at this pixel
}
```

The colour target's alpha is a 1-bit marker doing double duty: it answers both "is this background"
and, implicitly, "how covered is it". Anything translucent drawn at a background pixel is therefore
either erased (`a < 0.5`, the blit overwrites it with stars) or punches a star-shaped hole
(`a >= 0.5`, the branch does not fire and no stars render in the band). There is no alpha value that
composites correctly.

Void decals are the first feature to hit this, but not the only code path exposed to it.
`ParticleRenderer` uses a `LessEqual` depth test, so its fragments already pass at background pixels.
By inspection a translucent particle over the void hits the same trap today. No consumer has noticed
because nothing currently draws over the void. This is verified as part of release 1's test work, not
asserted here.

### The shape

The engine already has the correct pattern next door. `SkyRenderer` is a background pass: a
fullscreen triangle at the far plane with a read-only `GpuComparison.Equal` depth test, which passes
only where the stored depth still equals the cleared far plane, i.e. true background where no
geometry was drawn. It writes `alpha = 1` and runs before the decals. The starfield is an older,
cruder version of exactly that, done in the wrong place.

New `KhaozEngine.Render3D/Rendering/StarfieldRenderer.cs`, a sibling of `SkyRenderer` and
structurally a copy of it:

- Fullscreen triangle at z=1, `DepthStencil = (test: true, write: false, GpuComparison.Equal)`,
  `BlendAttachments = { OverrideBlend }`, drawn into `ColorDepthFB`.
- Writes `vec4(BgColor.rgb + vec3(star), 1.0)`.
- The star function moves verbatim from `BlitFrag`: the same `floor(uv * vec2(220.0, 124.0))` cell
  grid, the same `step(0.992, hash(cell)) * (0.55 + 0.45 * hash(cell + 3.7))`. It rebuilds its UV
  from `gl_FragCoord` and a `Res` uniform lane instead of an interpolated `vUv`, matching the
  backend-independent convention `SkyFrag` and `DecalFrag` already use.
- One uniform buffer, per the Metal invariant (see AGENTS.md and the `GroundDecalRenderer` header).
- `SetOutputs` rebuilds the pipeline when the MRT sample count changes for MSAA, same as `SkyRenderer`.

Drawn in `Scene3D` at the sky's slot, before the ground decals. Skipped when the sky pass runs: sky
already wins over starfield today (it writes `alpha = 1`, so the blit's branch never fires), and that
precedence is preserved exactly.

The blit's starfield branch is deleted along with its now-unused `BgColor` uniform field and the
`Params.x` lane.

### Behaviour changes, and why each is acceptable

Every one of these has the same root cause: the stars stop being pasted on at the end and start
living in the scene. They are all consequences of the fix, not incidental damage.

**Most of them do not fire for the actual consumer.** Measured against Hardpoint's real configuration:
`Bloom`, `Quantize`, `Dither` and `Outline` all default to false, and Hardpoint sets
`RenderScale = MatchViewport`, so stars render at the same resolution as before. That leaves exactly
one live change, the HDR tonemap row, because `Hdr.Enabled` defaults to true. The rest of this table
describes what happens to a consumer who has opted into those passes.

| Change | Effect | Assessment |
|---|---|---|
| Stars flow through quantize/dither/palette | They pixelate with everything else | Fix. Today the stars are the only element the retro chain does not touch. |
| Stars flow through bloom | Bright stars glow | Improvement. |
| Stars flow through the distortion pass | A ripple over the void now warps the stars behind it | Fix. Heat-haze should distort what is behind it. |
| Stars tonemap in HDR | Measured: star count identical (835 vs 835, none lost), brightest star luma 255 to 205, mean star luma 202.1 to 189.4 | Accepted, and milder than predicted. An earlier draft claimed "the new bloom compensates". That was wrong: `Bloom.Enabled` defaults to FALSE, so nothing compensates. The stars are simply slightly dimmer, and the brightest ones stop clipping to pure white, which is arguably more correct. |
| Stars render at internal resolution | Cell grid is the same in NDC, so the pattern is unchanged, but per-star pixel size and crispness shift | Accepted. |
| Stars now make background pixels opaque | `TransparentBackground = true` combined with the DEFAULT `Starfield = true` on a raw `Scene3D` previously composited invisibly (stars drawn at `outA = 0`). Now the background pass writes `alpha = 1`, so the background is fully OPAQUE and hides whatever the caller composites it over | Accepted, but a real behaviour change on reachable public API, not just a look change. See the note below the table. |

`TransparentBackground = true` with the default `Starfield = true` is reachable on any raw `Scene3D`,
not a contrived state, and its behaviour genuinely changes: invisible stars over a transparent
background become an opaque starfield covering the composite. `Render3DPreview` and
`UseSmoothPreset` both already force starfield off, which is why neither is exposed, but a consumer
that builds `Scene3D` directly with `TransparentBackground = true` is not protected by either.
SpaceGame is the concrete near miss: it sets `Scene.Post.TransparentBackground = true` and never
touches `Starfield`, and is saved only because its one 3D path, `Render3DPreview`, forces
`Post.Starfield = false` in its constructor (`Render3DPreview.cs:74`). Had SpaceGame instead built a
raw `Scene3D` the way its 2D path does, this release would have shipped an opaque starfield silently
covering its 2D backdrop. This must be called out in `CHANGELOG.md` as a BEHAVIOUR CHANGE, not folded
into an "additive, minor" description: a transparent composite now needs `Background = Solid` set
explicitly, where before the default was already safe for that combination.

### The API: one concern, one knob

`Post.Starfield` and `Post.Sky.Enabled` are two booleans with an implicit `sky > starfield > solid`
precedence, both describing one thing: what fills the background. They collapse into:

```csharp
public enum BackgroundMode { Solid, Starfield, Sky }
```

Mutually exclusive by construction, and extensible where the next options actually land (`Gradient`,
`Cubemap`, `Nebula`). `Post.Background` defaults to `Starfield`, preserving today's default.

`Post.TransparentBackground` deliberately stays a separate boolean and is **not** folded in. It is a
different concern: it controls the final image's output alpha for offscreen compositing, not what is
painted at background pixels. Folding it in would also be lossy, because the old booleans form a
cross-product the enum cannot express (`TransparentBackground = true` with `Starfield` left at its
default `true` is a reachable state today, and no single enum value means it). The interaction is
documented instead: a non-`Solid` background writes opaque pixels, so `TransparentBackground` only
takes effect with `Background = Solid`.

`Background` is a **derived view** over the existing booleans, not new state:

```csharp
public BackgroundMode Background
{
    get => Sky.Enabled ? BackgroundMode.Sky
         : Starfield   ? BackgroundMode.Starfield
                       : BackgroundMode.Solid;
    set { Sky.Enabled = value == BackgroundMode.Sky; Starfield = value == BackgroundMode.Starfield; }
}
```

An earlier draft made the enum authoritative and demoted the booleans to `[Obsolete]` aliases. That
does not survive contact with `SkySettings`, which is a `sealed class` held in a reassignable field
(`public SkySettings Sky = new()`). `Sky.Enabled` could only delegate to an authoritative
`Post.Background` through an owner back-pointer, which breaks the moment a consumer assigns a fresh
`SkySettings`, and which couples a "plain settings bag" (its own words, following the
`ShadowSettings` precedent) to its parent. The alternatives were worse: an `Auto` mode (two sources
plus a mode switch), or lossy aliases that silently change behaviour.

Deriving instead of owning gets the same ergonomics with none of that:

- No new state, no back-pointer, no parallel sources.
- Lossless. `get(set(x)) == x` for all three values, and `set(get(x))` normalizes a both-true state to
  exactly today's `sky > starfield > solid` precedence.
- The getter **is** the precedence, written down in one place instead of implied across three passes.
- Nothing is `[Obsolete]`, so the ~20 in-tree `Post.Starfield = false` call sites do not churn. With
  warnings-as-errors that churn would have been mandatory, not optional.
- Additive (minor bump), no consumer break, no compat risk.

The cost: the booleans remain the storage, so a both-true state is still representable through them.
It resolves deterministically via the documented precedence, which is exactly today's behaviour.
Retiring the booleans in favour of a `StarfieldSettings` bag mirroring `SkySettings` is the natural
next-major move and is out of scope here.

### Test and golden impact

**Zero goldens rebake. The golden suite is blind to the starfield.** This was measured, and it
corrects an earlier draft of this document that predicted eight rebakes across three backends.

The prediction assumed the goldens were byte-exact pixel compares. They are not. `GoldenCompare`
downsamples each render to a **32x18 grid of averaged RGB per cell** and compares with a per-channel
tolerance of `0.06` (`GoldenGrid.DefaultTolerance`). The stars are sparse (roughly 0.8% of a 220x124
cell grid) and each golden cell averages a block of hundreds of pixels, so a star's entire
contribution to a cell average is about `0.012`, five times smaller than the tolerance. A committed
golden shows this directly: a star-bearing cell reads `0.0316 0.0434 0.0708` against the
`0.0196 0.0314 0.0588` clear colour.

Measured proof, not inference: with the `_starfield.Draw` call commented out entirely, so the engine
renders NO starfield at all, `telegraph_ground` and `scene3d` still pass. The full 65-test golden
suite also passes unchanged with the real pass wired in.

Two consequences, and the second matters more than the first:

1. The migration is far cheaper than planned. No rebake, no cross-backend bake dispatch, no
   contact-sheet review of rebaked scenes.
2. **The golden suite provides zero regression protection for the starfield, and never did.** That is
   a pre-existing gap this work exposed rather than caused. It is defensible by design (the grid is
   built to catch gross shader / UBO / blend / winding regressions while tolerating driver noise), but
   it means the automated net cannot see this feature at all. `StarfieldGpuTests` (added by this
   release) is therefore the ONLY automated guard on the starfield, which makes it load-bearing rather
   than a nice-to-have. It samples raw pixels, not the golden grid, and its sabotage check confirms it
   fails when the pass is removed.

Two non-golden tests have their premise inverted and are rewritten against the new pass:

- `HdrPipelineGpuTests.Hdr_alpha_marker_survives_tonemap` asserts the blit injects stars only where
  `a < 0.5`. That mechanism is being deleted. It becomes `Hdr_starfield_survives_tonemap`, asserting
  the background pass's stars survive the tonemap (a real new risk: they now go through it).
- `DistortionGpuTests.Distortion_alpha_marker_survives` asserts stars are unaffected by a ripple
  **because** they are painted after distortion. Post-migration they are in `ColorTex` before the
  post chain, so it becomes `Distortion_warps_the_starfield`, the opposite assertion.

New headless tests (`BackgroundModeTests`) pin the derived enum itself, not aliases: there are none,
per the design above. They cover the default (`Background` starts at `Starfield`), that every mode
round-trips through set-then-get, that setting one mode clears the other two booleans (mode
exclusivity), that a both-true state normalizes to `Sky` (the legacy sky-over-starfield precedence),
that normalizing is idempotent, and that reassigning `Post.Sky` to a fresh `SkySettings` instance
still resolves correctly through the derived getter. New GPU test: stars land only on background
pixels and never on geometry.

### Verification

1. `dotnet test` green, with all 65 goldens passing unchanged (no rebake, see above).
2. `StarfieldGpuTests` passing, and confirmed to FAIL when the pass is sabotaged. Since the goldens
   cannot see the starfield, this test carries the entire automated net and its bite is verified
   rather than assumed.
3. **Windowed A/B in Hardpoint before merge. This is the hard gate.** The goldens being blind to the
   starfield raises rather than lowers the stakes here: no automated check can tell us whether
   bloomed, quantized, tonemapped, distortable stars read better than the pasted-on ones. A fully
   green suite is not evidence the look is right. Only a human looking at the game is. SpaceGame is
   not a valid target for this gate: its only 3D path is `Render3DPreview`, whose constructor forces
   `Post.Starfield = false` (`Render3DPreview.cs:74`), so it always renders `BackgroundMode.Solid` and
   can never show a star.

## Release 2: opt-in void fallback for ground decals

Lands on the clean foundation from release 1, with no blit hack.

### Correcting the premise

The engine is **not** reversed-Z. Depth is `[0,1]` near-to-far on every supported backend
(`GpuClip`'s `DepthRangeZeroToOne`), cleared to `1f` (`ModelRenderer.BeginModelPass`). The decal
pass's far-plane quad with a `Greater` test therefore passes where stored depth `< 1`, i.e. on
geometry, and rejects the cleared background.

That makes the void half free, and cheaper than the original sketch: no demoting the depth test to a
shader branch, no disabling it, no sniffing a clear value, no new texture binding.
`GpuComparison.Equal` at z=1 is the exact complement of `Greater` at z=1. The hardware partitions the
screen into geometry and background with no overlap and no gaps, using the pattern `SkyRenderer`
already proves on all three backends.

### API

```csharp
public struct GroundDecal
{
    // ...
    /// <summary>Project onto the virtual horizontal plane at Center.Y wherever there is no scene
    /// geometry, instead of leaving the decal truncated at the geometry's edge. Default false keeps
    /// the legacy depth-only behaviour byte-for-byte.</summary>
    public bool VoidFallback;
    /// <summary>Alpha scale applied only to void-projected pixels, so they read as projected rather
    /// than as standing on ground. 0 (default) = no dim, i.e. void pixels match ground pixels.</summary>
    public float VoidDim;
}
```

`TelegraphStyle` carries the passthrough (`VoidFallback`, `VoidDim`) through `TelegraphResolve` to
`ResolvedTelegraph` to `GroundTelegraphs.Base`, because the `Ground*` extension methods take only
(geometry, progress, style) and a per-call options parameter would churn every signature. Precedent
is strong: `EdgeWidthWorld`, `FeatherWidthWorld` and `ZoneSense` are already 3D-only or reserved
knobs living on the shared style, and `TelegraphRenderer2D` ignores them.

### Rendering

Two new pipelines (alpha and additive at `Equal`), giving four total. A flagged decal is drawn twice:

1. The existing `Greater` runs, over all decals, completely unchanged. Geometry pixels are
   byte-identical to today.
2. New `Equal` runs, over only the flagged subset. Background pixels take the plane path.

Instance packing appends the void instances after the base ones, so base bytes never move:

- Base instance: `Extra = (BaseFill, 0, 0, 0)`, identical to today's packing.
- Void instance: `Extra = (BaseFill, 1, VoidDim, 0)`, with its screen rect computed over the flat
  AABB at `Center.Y` (a tighter, correct bound than the Y-gate band).

`CoalesceDecalRuns` is unchanged for the base pass. The void pass coalesces its own subset by blend.

Fragment shader branches on `Extra.y`:

- `Extra.y <= 0.5`: today's exact path (reconstruct world from `DepthTex` + `InvViewProj`, Y-band gate).
- `Extra.y > 0.5`: unproject `gl_FragCoord` at two NDC depths to get the camera ray (correct for both
  the ortho iso camera and the perspective follow camera), intersect with `y = Center.Y`, discard when
  `|dir.y| < eps` (parallel) or `t < 0` (plane behind the eye). Skip the Y-band gate: the plane *is*
  the decal's plane by construction. Apply `VoidDim` to the final alpha. Everything downstream (SDF,
  feather, pattern, energy lanes) is shared.

Geometry that exists but sits outside the Y band (a wall face, a lower ledge) keeps today's behaviour
and discards. It does **not** fall back to the plane: the plane point there is *behind* the wall, so
painting it would draw an x-ray decal through solid geometry. The fallback is for absent geometry
only, which is exactly Hardpoint's island-edge case.

### Zero-neutral contract

No flagged decals means: identical instance bytes, zero extra draws, zero extra pipeline binds, and
the `Equal` pipelines never bound. `telegraph_ground` and `telegraph_modern` stay byte-exact (against
their release-1 baselines) and are the net.

### Showcase and golden

New `telegraph_ground_void` golden: a small island (`MeshPrimitives.Tile`) with a `GroundCircle`
whose radius overhangs the edge, framed so the void projection is visible continuing past the
geometry. Baked on Metal locally, D3D11 + Vulkan via the `cross-platform-gpu.yml` bake dispatch. A
showcase PNG dump alongside it, in the `TelegraphShowcaseGpuTests` style, for human review.

### Docs

`docs/USING-KHAOZENGINE.md` (a section for the new public API), `KhaozEngine.Render3D/README.md`,
`KhaozEngine.Telegraphs.Render3D/README.md`, `CHANGELOG.md`, plus the guard-checked version
declarations (`docs/ROADMAP.md` "Current released version", the `README.md` `<PackageReference>`
example). Full doc sweep per AGENTS.md: grep the new type/flag names across all `*.md` recursively.

## Consumer follow-up (not engine work)

Hardpoint opts in for `BoardRenderer.DrawRangeRing` and evaluates the blast rings once release 2
ships. Tracked in Hardpoint `docs/TODO.md` under Engine candidates, "Void/plane fallback for ground
decals". Hardpoint also re-checks its starfield look after release 1.

## Sequencing

Release 1 must land first. Building the void fallback on today's blit would need a two-line gated
hack that release 1 deletes anyway, and would leave the goldens moving for two reasons at once,
making release 2's zero-neutral contract, the property that makes it safe to ship, untestable.
