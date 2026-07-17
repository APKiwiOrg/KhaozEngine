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
`ParticleRenderer` uses a `LessEqual` depth test, so its fragments already pass at background pixels;
by inspection a translucent particle over the void hits the same trap today. No consumer has noticed
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

| Change | Effect | Assessment |
|---|---|---|
| Stars flow through quantize/dither/palette | They pixelate with everything else | Fix. Today the stars are the only element the retro chain does not touch. |
| Stars flow through bloom | Bright stars glow | Improvement. |
| Stars flow through the distortion pass | A ripple over the void now warps the stars behind it | Fix. Heat-haze should distort what is behind it. |
| Stars tonemap in HDR | Slightly dimmer than today's post-tonemap injection | Accepted. The new bloom compensates. Verified in the A/B, not assumed. |
| Stars render at internal resolution | Cell grid is the same in NDC, so the pattern is unchanged, but per-star pixel size and crispness shift | Accepted. |
| Stars now make background pixels opaque | `TransparentBackground` + stars previously composited through, drawing stars at `outA = 0` (invisible) | Accepted. That combination was already nonsense; `Render3DPreview` and `UseSmoothPreset` both already force starfield off for exactly this reason. Documented: a transparent composite needs `Background = Solid`. |

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

The two booleans stay as **computed** `[Obsolete]` aliases over the enum, not as parallel state:

```csharp
[Obsolete("Use Background = BackgroundMode.Starfield. Removed in the next major.")]
public bool Starfield
{
    get => Background == BackgroundMode.Starfield;
    set => Background = value ? BackgroundMode.Starfield : BackgroundMode.Solid;
}
```

The aliases are last-writer-wins, where the old booleans had an implicit precedence. The one sequence
that changes meaning is an explicit `Starfield = true` *after* `Sky.Enabled = true`, which used to
leave the sky winning and now selects the starfield. That sequence is incoherent on its face, and the
common shape is unaffected: `Starfield` defaults to `true` and is never explicitly set, so enabling
the sky simply moves `Background` from `Starfield` to `Sky`. Called out in the `[Obsolete]` message
and the CHANGELOG.

So the release is additive (minor bump) with no consumer breaks. Warnings are errors engine-wide, so
the engine's own internal uses migrate to the enum in this release. The obsolete surface exists purely
for consumers and is deleted at the next major.

### Test and golden impact

Eight goldens rebake, all 3D, all with visible background pixels (verified by simulating each test's
camera math against its geometry):

`scene3d`, `scene3d_hdr_off`, `scene3d_fill`, `telegraph_ground`, `telegraph_modern`,
`scene3d_textured`, `scene3d_splat`, `scene3d_splat_distance`.

24 files (8 names x metal/direct3d11/vulkan). The other 24 golden names already run starfield-off or
are 2D and are expected to stay byte-exact. That expectation is the regression net for this release.

Two non-golden tests have their premise inverted and are rewritten against the new pass, not merely
rebaked:

- `HdrPipelineGpuTests.Hdr_alpha_marker_survives_tonemap` asserts the blit injects stars only where
  `a < 0.5`. That mechanism is being deleted. It becomes an assertion that the background pass's
  stars survive the tonemap.
- `DistortionGpuTests.Distortion_alpha_marker_survives` asserts stars are unaffected by a ripple
  **because** they are painted after distortion. Post-migration they are in `ColorTex` before the
  post chain, so it becomes the opposite assertion: a ripple over the void warps the stars.

New headless tests cover the mode enum and its aliases: each alias round-trips, every old
boolean-pair sequence used in-tree resolves to the expected mode, and the last-writer-wins semantics
are pinned so the one changed sequence is a deliberate, asserted choice rather than a surprise. New
GPU test: stars land only on background pixels and never on geometry.

### Verification

1. `dotnet test` green, with the 24 starfield-off goldens byte-exact.
2. Metal goldens rebaked locally, then eyeballed via a PIL contact sheet (a self-baked golden always
   passes its own compare, so the bake is reviewed visually, never trusted green).
3. D3D11 + Vulkan baked via `cross-platform-gpu.yml` `workflow_dispatch` with `bake=true`, artifacts
   downloaded and committed.
4. **Windowed A/B in Hardpoint and SpaceGame before merge.** This release changes the look of the
   starfield in both games. Tests cannot answer whether bloomed, quantized, distortable stars read
   better than the current ones. This is a hard gate, not a nicety.

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

`CoalesceDecalRuns` is unchanged for the base pass; the void pass coalesces its own subset by blend.

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
