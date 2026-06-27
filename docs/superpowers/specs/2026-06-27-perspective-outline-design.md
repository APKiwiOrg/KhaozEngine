# Perspective-correct toon outline + two outline bugs (`Render3D` `PixelPostProcess`)

Date: 2026-06-27
Status: approved design, ready for implementation plan
Area: rendering quality (engine), independent of the overworld gameplay track

## Context

The depth/normal edge outline in `KhaozEngine.Render3D` (`Internal/ShaderSources.EdgeFrag` +
`Rendering/PixelPostProcess`) was built for the **orthographic** `IsoCamera3D` (SpaceGame, Hardpoint).
The overworld's `FollowCamera3D` is the first thing to drive it with a **perspective** camera, which
exposed three issues, diagnosed live in `TerrainWalkSample` (it now has an A/B debug harness:
`[M]` RenderScale, `[O]` outline, `[K]/[L]` depth threshold, `[G]/[H]` normal threshold).

All three live in `Render3D`, so the fix benefits every game (SpaceGame's 2.5D, Hardpoint, Ruinborne,
any non-ortho camera). Tester finding: a moderate ("medium") outline flickers; the extremes (heavy or
none) are stable. The goal is to make a **medium** outline stable under perspective; if it cannot be,
the games fall back to none, but the two bugs are fixed regardless.

The current `EdgeFrag` (for reference):
```glsl
float d0 = texture(DepthTex, vUv).r;                 // stored depth = gl_Position.z / gl_Position.w
vec3  n0 = texture(NormalTex, vUv).rgb * 2.0 - 1.0;  // geometric normal MRT
for (i in 4 neighbours) {
  if (abs(d - d0) > Thresh.x) edge = 1.0;            // Thresh.x = OutlineDepthThreshold
  if ((1.0 - dot(n, n0)) > Thresh.y) edge = 1.0;     // Thresh.y = OutlineNormalThreshold
}
```

## The three items

### Bug A - outline toggle flips the image vertically (ping-pong parity)

Toggling `Outline` (and likely `Quantize`/`Dither`) adds/removes a full-screen pass in the
`PixelPostProcess` ping-pong chain, and the on-screen vertical orientation depends on the **parity** of
how many passes ran. So `Outline=false` renders upside-down. **Fix:** make the final on-screen
orientation independent of which optional passes (outline/quantize/dither) are enabled - e.g. resolve
the flip in the final blit based on actual source orientation, or always run an even number of flips, or
flip in the blit's UVs deterministically. Outline-on and outline-off must both be upright; the same must
hold for any combination of quantize/dither.

### Bug B - the normal-edge term contributes nothing

The normal-edge term exists but produced **no visible change at any threshold**, even when the depth
term was dialled down so it should dominate. Investigate the root cause (geometric-normal MRT
encoding/precision, the `1 - dot` threshold range, whether the depth term still masks it) and **repair
it so it catches interior creases** the depth term misses. This matters specifically for a clean *medium*
outline (silhouettes from depth + creases from normals).

### Fix C - perspective-correct depth threshold

`EdgeFrag` compares raw `z/w` (linear only for ortho; **non-linear under perspective**) against a fixed
`Thresh.x`, so under perspective the same threshold over-detects near and under-detects far, and edges
**pop in/out on zoom/distance** - the medium-outline flicker. **Fix:** linearize depth for the edge test
(reconstruct view-space depth from the camera's near/far in `EdgeFrag`, or store linear depth) and use a
**distance-relative** threshold (e.g. compare a depth delta normalized by view-space depth, or scale the
threshold by depth) so a moderate outline is stable at any zoom. Plumb the camera `NearPlane`/`FarPlane`
(and/or a perspective flag) into the `Edge` UBO from `Scene3D`/`PixelPostProcess`.

**The orthographic path MUST stay byte-identical.** For ortho, `z/w` is already linear; gate or
parameterize the linearization so the ortho output is unchanged and the existing iso-camera GPU goldens
do not move.

### Optional D - distance-fade (helps medium read)

Fade outline strength beyond a distance so the far foliage stops aliasing into mush. Behind a new
`OutlineDistanceFade` setting, **default off** so ortho goldens are unchanged. Include if cheap.

## Verification (GPU discipline - read the engine's golden rules first)

- **Existing goldens stay byte-identical.** They all use the ortho `IsoCamera3D`; the ortho outline path
  must not change. A moved ortho golden is a regression, not a re-bake.
- **Add a new perspective-outline golden**: a perspective-camera scene (a few meshes under a perspective
  camera) locking (a) the corrected stable outline and (b) the Y-flip fix (outline-on AND outline-off
  both upright). Per the engine's GPU-golden rule, a golden baked only on Metal turns `main` red on the
  other backends: bake all three (Metal locally + D3D11/Vulkan via `cross-platform-gpu.yml`
  `workflow_dispatch bake=true`), download artifacts, commit.
- **Headless edge-math test** where practical (linearization reduces to identity for ortho; a perspective
  depth delta yields a stable edge across two zoom levels).
- **Manual**: `TerrainWalkSample` at a medium `OutlineDepthThreshold` - confirm a moderate outline is
  **stable on zoom and rotate** (the harness keys A/B it). This is the "does medium work now" check.

## Scope

### In scope

- The three fixes (A flip, B normal term, C perspective depth) + optional D distance-fade, all in
  `Render3D` (`ShaderSources.EdgeFrag`, `Rendering/PixelPostProcess`, the `Edge` UBO, near/far plumbing
  from `Scene3D`).
- Keep the ortho path byte-identical; add the new perspective golden(s) and bake them cross-platform.
- Docs + version bump: **patch** if no new public knob, **minor** if `OutlineDistanceFade` (or any new
  `PixelPostProcessSettings` field) is added. Update CHANGELOG/CHANGENOTES, the 3 guard declarations, and
  `docs/USING-KHAOZENGINE.md` if a knob is added.

### Out of scope (named)

- **TAA** and **inverted-hull (back-face) outlines** - the heavier, more robust alternatives; note as
  future options if medium still doesn't satisfy.
- **MSAA**, palette/dither changes.
- The **art-style decision** (none / heavy / medium) - that's the games' call; this just makes medium
  viable. Setting each game's chosen default is a separate, trivial follow-up.

## Engine-first

All in `Render3D`; benefits SpaceGame, Hardpoint, and Ruinborne alike. Independent of the overworld
gameplay track and of the in-flight sharding work (different files: `Render3D` vs `NetWorld`/`Sharding`),
so it can run as a **concurrent** engine chat in its own worktree.

## Open items to confirm during implementation

- Reconstruct linear depth in `EdgeFrag` from near/far (preferred - no MRT change, ortho stays identical)
  vs storing linear depth in the depth MRT (changes the MRT, risks ortho goldens).
- The exact distance-relative threshold form (normalize delta by depth vs scale threshold by depth);
  pick whichever gives a stable medium outline near and far.
- Bug B root cause (normal MRT precision/encoding vs threshold range vs depth masking).
- Whether the Y-flip fix is best in the final blit (UV flip) or by normalizing pass parity.
