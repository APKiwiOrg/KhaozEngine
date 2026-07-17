# KhaozEngine TODO / follow-ups

Discovered follow-ups, known gaps, and consumer pulls. Not a tracked sprint and not the roadmap.

**TODO vs ROADMAP.** This file is the chip pile: things noticed in passing, gaps a release knowingly
left, pulls a game has asked for. [`ROADMAP.md`](ROADMAP.md) is the program list: anything that earns
its own design spec and its own release. If it needs a spec, it is a roadmap item. Otherwise it is a
TODO. Shipped detail lives in [`../CHANGELOG.md`](../CHANGELOG.md), so a resolved item is deleted here
rather than ticked and kept.

Anything discovered and not done belongs here **at the moment it is discovered**, before the finding
chat moves on. Open items are actioned at a checkpoint (the moment you are about to end your turn and
report back), never mid-task. Resolved entries are deleted by the release sweep. See the
"Discovered work" section in [`../AGENTS.md`](../AGENTS.md) for the full lifecycle and the entry format.

## Consumer pulls (game-requested)

- [ ] **BakeOverworld overload that sweeps an IPhysicsWorld directly.** Ruinborne hand-rolls a
  physics-probe surface provider (`RuinborneNavSurfaceProvider`) because its props are physics-only Bepu
  statics. An overload sweeping `IPhysicsWorld` directly is useful to any physics-obstacle game.
  Recorded and deliberately not centralized at the time. Evidence: Ruinborne
  `docs/ENGINE-INTEGRATION.md`, "Not centralized (recorded)" note in the NPC follower-integration
  section.

## Known gaps

- [ ] **View distance vs cell geometry is an undocumented, unguarded coupling.** Sharding is only correct
  while `InterestRadius <= OverlapMargin`: raise a game's view distance past the cell overlap band and a
  player sees entities the neighbouring cell has not handed over yet. Nothing states the invariant and
  nothing asserts it, so a consumer tuning view distance can silently break replication. Wants a documented
  invariant plus a startup assert or an analyzer. Carried over from the 2026-07-04 MMO architecture review
  (recorded 2026-07-17).

- [ ] **Showcase 3D overworld room is not a shadow/day-night testbed.** The 12.0.0 frustum-slice
  shadow rework had to be visually verified without a representative showcase scene: the 3D overworld
  demo room enables no shadows (`ShadowMode` stays `Off`), has no staircase or multi-level geometry
  (the exact stair-climb case that surfaced the old cascade square-seam artifact in Ruinborne), and
  runs no day/night cycle. Extend the room to enable `ShadowMode.ShadowMap` with the engine defaults,
  add a walkable staircase plus a tree line near a cascade hand-off distance, and drive the sun with
  the same `SunCycle` mapping Ruinborne uses so a moving key light exercises per-frame cascade refits
  and the dirty-skip path. Makes the showcase the one-click windowed verification surface for every
  future shadow/lighting change. Recorded 2026-07-17 from the 12.0.0 release retro.

- [ ] **Ground decals wrap down a sharp edge's vertical face (legacy, unflagged decals).** The Y-band gate is
  `[Center.Y - YTolerance, Center.Y + MaxStep]` and `GroundTelegraphs` hardcodes `YTolerance = 0.3`, so the TOP
  0.3 of any vertical face is inside the band and the decal conforms ONTO it, evaluated at that face's XZ
  (pinned at the edge) instead of the decal's own. A range ring overhanging a mesa visibly drips down the cliff.
  The gate's stated purpose is "conform to terrain, not walls" (`DecalFrag`), which it fails at the top of a
  wall: at one pixel, with only depth, a terrain dip 0.3 below the plane and the top 0.3 of a cliff face are
  arithmetically identical. The geometric normal is the only signal that separates them. 12.1.0 fixed this for
  `VoidFallback` decals ONLY (normal-gated, so the release stayed zero-neutral for everyone else) and left the
  legacy path alone: `GroundDecalRenderer` now binds `NormalTex`, so making it universal is a shader-side
  `if` plus a golden rebake sweep, but it CHANGES existing decal rendering for every consumer and so wants its
  own release and its own windowed A/B. Reproduced with the fallback off during 12.1.0. The `GroundNormalMinY`
  0.5 threshold (60-degree slopes still count as ground) is the constant to reuse. Evidence: `CHANGELOG.md`
  12.1.0, `KhaozEngine.Tests/Gpu/GroundDecalVoidGoldenTests.Golden_void_fallback_keeps_the_disc_flat_across_a_cliff_face`
  (its legacy-delta control measures exactly this artifact).
- [ ] **`GroundTelegraphs.BuildResidueCircle` drops `VoidFallback` / `VoidDim`.** It composes its `GroundDecal`
  directly instead of through `Base()`, so a residue mark whose style opted into the void fallback still truncates
  at an island's edge while every other `Ground*` shape projects. Deliberate for 12.1.0 (a scorch mark is a mark ON
  ground, so projecting it into the void is not obviously wanted) and documented on the builder, but the asymmetry
  is a trap: the flag is on the shared `TelegraphStyle` and silently does nothing here. Either route the builder
  through `Base()` or split residue onto its own style type. Evidence: `CHANGELOG.md` 12.1.0,
  `KhaozEngine.Telegraphs.Render3D/GroundTelegraphs.cs` `BuildResidueCircle`.
- [ ] **HDR chroma preservation is partial.** A saturated channel that clips at the display ceiling
  before the rescale still desaturates, even at `ChromaPreservation = 1`. Evidence: `CHANGELOG.md`
  11.7.0.
- [ ] **Map editor invalidation still falls back to full rebuild for several edit kinds.** Scatter-layer,
  companion, and terrain-scalar edits take the full-rebuild path rather than narrowed partial
  invalidation (exclusion and scatter-override edits were narrowed to partial rebuild in 11.4.0).
  Polygon override shapes stay MCP-authored and inspector-read-only, and the biome-band "Affects" row
  admits ground tinting is not yet wired. Evidence: `CHANGELOG.md` 10.119.0 "Scope cuts (by design this
  round)", 10.125.0 "Deferred out of this round", 11.4.0 "Biome-band editing honesty".
- [ ] **Cascaded shadow map gaps.** Terrain is receive-only and cannot cast, there are no alpha-tested
  cutout casters, and GPU-skinned casters stay opt-in and off by default pending a windowed A/B.
  Evidence: `CHANGELOG.md` 10.122.0 "Out of scope (unchanged)".
- [ ] **The golden grid is blind to fine, sparse detail. It cannot see the starfield at all.**
  `GoldenCompare` downsamples each render to a 32x18 grid of AVERAGED rgb per cell and compares with a
  0.06/channel tolerance (`GoldenGrid.DefaultTolerance`). A star contributes only about 0.012 to a cell
  average, five times under tolerance. Proven during 11.9.0: with `_starfield.Draw` commented out, so the
  engine renders NO starfield whatsoever, `telegraph_ground` and `scene3d` still PASS. The grid is
  deliberately coarse (it exists to catch gross shader / UBO / blend / winding regressions while
  tolerating driver noise), so this is not a defect in itself, but it means any sparse or fine-detail
  feature has zero golden coverage and needs its own raw-pixel test. `StarfieldGpuTests` is now the only
  net for the starfield. Worth auditing which OTHER features believe they are golden-covered but are not.
  Evidence: `CHANGELOG.md` 11.9.0, `docs/BACKGROUND-PASS-VOID-DECALS-DESIGN-2026-07-17.md`.
- [ ] **`StarfieldGpuTests` box-coverage guard proves "mostly covered", not fully.** The guard added in
  11.9.0 asserts the box's centre block is meaningfully brighter than the clear colour before trusting
  the cross-mode byte-identity diff, which closes the vacuous-pass hole. A reviewer noted it still only
  establishes the block is mostly covered by geometry, not wholly. Low value, recorded for honesty.
  Evidence: `CHANGELOG.md` 11.9.0.
- [ ] **Rebake the drifted direct3d11/vulkan goldens before the tolerance margin runs out.** A
  `workflow_dispatch bake=true` run of `cross-platform-gpu.yml` (run 29567466645, 2026-07-17, on the
  frustum-slice shadow branch after merging main at 11.9.0) showed 8 scenes whose direct3d11/vulkan
  goldens have accumulated whole-frame drift versus their checked-in grids that is NOT attributable to
  the shadow rework: `scene3d`, `scene3d_fill`, `scene3d_hdr_off`, `scene3d_splat`,
  `scene3d_splat_distance`, `scene3d_textured`, `telegraph_ground`, `telegraph_modern`. 636-1641 of 1728
  cells changed, max deltas 0.046-0.058 against the 0.06 verify tolerance. Likely source is the 11.9.0
  background-pass release (or residual 11.7.0 chroma-tonemap drift) shipping without a CI-backend rebake
  because it stayed sub-tolerance. The verify legs still pass today, but the margin is nearly consumed:
  any further sub-tolerance change turns main red on those scenes. Task: confirm the drift source from
  the relevant releases' bake commits, run a fresh `cross-platform-gpu.yml bake=true` on current main,
  eyeball the bake evidence PNGs per scene (repo rule), then commit the rebaked direct3d11/vulkan
  goldens with a message attributing the drift. Goldens-only change, no version bump needed.
- [ ] **Audit whether the golden tests are actually valid, robust, and useful.** The grid-blindness
  entry and the backend-drift entry above are two symptoms of the same unexamined question: what do the
  goldens really prove? Known so far: the 32x18 averaged grid cannot see sparse detail at all (a
  fully deleted starfield still passes), and whole-frame drift accumulates silently right up to the
  0.06 tolerance so a "green" leg can be one small change away from red. Wanted: a first-principles
  review of the mechanism, not a patch. Is per-cell averaging with a fixed absolute tolerance the right
  comparison? Should tolerance be per-backend, or a structural/perceptual metric instead of a mean? How
  many scenes would still pass with their feature-under-test deleted (the starfield experiment
  generalized)? Does the suite catch anything the raw-pixel GPU tests do not? Is the per-backend rebake
  ritual masking real regressions as "driver noise"? Outcome should be a written verdict on what the
  goldens are for and what tier of test covers what, whether or not the mechanism changes.

## From the 2026-07-17 partial whole-repo review

A max-effort multi-agent review (27 subsystems planned, stopped early on cost) completed 9 subsystems:
all six `KhaozEngine.Render3D` units and all three `KhaozEngine.MapEditor` units. Every item below was
adversarially verified by an independent agent that tried to refute it. Three further candidates were
refuted with evidence and are deliberately not recorded as work: `OverlayUnlitVert`'s holed vertex-input
signature (`ShaderSources.cs:1477`, benign here, the committed direct3d11 goldens render correct colour
and match metal to 0.0001), `ViewportWorld.Rebuild`'s stale `_built` flag (latent, no caller catches so
the process dies on the failing frame anyway), and `SkinnedMeshBuilder.BuildTube`'s missing boneCount
upper bound (already throws at first draw, error-message locality only).

- [ ] **`LayeredAnimator` builds the additive rotation delta on the wrong side.** `ApplyAdditive`
  extracts the delta in the PARENT frame (`sample * inverse(reference)`, `LayeredAnimator.cs:188`) but
  applies it in the joint's LOCAL frame (`base * delta`, line 198). Local-frame application is the
  deliberate, test-pinned convention, so line 188 is the wrong side and must be
  `Quaternion.Inverse(reference.Rotation) * sample.Rotation`. Any joint whose additive reference (the
  clip at t=0) is non-identity gets the authored rotation conjugated by the reference instead of the
  authored pose, which is every glTF humanoid shoulder/spine. Confirmed by running a test through the
  real `LayeredAnimator`/`BonePalette` path: with base == reference the result does not reproduce the
  sample, which is the defining invariant of additive animation. The whole additive-rotation suite uses
  an identity reference, where both extractions coincide, so it cannot catch this. Fix line 188, fix the
  self-contradicting comment at 175-181, and add a non-identity-reference regression test.
- [ ] **`TransitionRenderer` frozen-capture texture has 1 mip but is whole-resource-copied from a
  mipped `ColorTex`.** `BindTargets` (`TransitionRenderer.cs:109`) always allocates `_frozen` with
  mipLevels 1, while `BeginFrame` does `cl.CopyTexture(res.ColorTex, _frozen!)` (line 134). Veldrid's
  whole-resource overload requires `source.MipLevels == destination.MipLevels` and throws otherwise.
  Triggers when `Post.EffectiveSupersample > 1` (e.g. `AntiAliasing.Ssaa`) or `FixedInternal` +
  `MipFilterFixedInternalDownscale` above the viewport, which makes `WantsMipDownsample` true and gives
  `ColorTex` a full chain. Plain `MatchViewport` at supersample 1 does NOT trigger it (`tw == viewportW`
  so `mipped` stays false). Veldrid 4.9.0 release does not compile the validation out, so it is a
  deterministic exception, not silent corruption. Fix: copy only mip 0 via the existing
  `CopyTextureSubresource` seam, which validates width/height but not mip-count equality, and the frozen
  frame is only ever sampled 1:1.
- [ ] **`DistortionRenderer` and `WaterRenderer` dispose live GPU buffers inline instead of retiring
  them.** `DistortionRenderer.EnsureCapacity` (`DistortionRenderer.cs:121`) calls `_instances?.Dispose()`
  on the grow path, and `WaterRenderer.EnsureUboCapacity` (`WaterRenderer.cs:120`) does the same to
  `_ubo`, while a prior frame's submitted command list may still be reading them. The frame path has no
  `WaitForIdle`, so the CPU can be N frames ahead. `ModelRenderer.cs:606` documents the rule in the
  engine's own words and the sibling renderers (`ParticleRenderer.cs:223`, `GroundDecalRenderer.cs:177`,
  `OverlayMeshRenderer.cs:135`, `ShadowMapRenderer.cs:289`) all keep a `_retired` list. Distortion is
  reachable today via the public unbounded `Scene3D.DrawDistortion`/`DrawDistortions` once a frame
  exceeds 64 sprites. Water is latent, since no current caller draws more than one plane, but the class
  is public API and the fix is identical, so do both together.
- [ ] **A vanished skinned caster leaves a ghost shadow baked into the reused atlas.**
  `Scene3D.ShadowDepthPassDirty` (`Scene3D.cs:2874-2876`, called at :1975) takes `anySkinnedCaster` from
  the CURRENT frame only. Nothing records whether the LAST rendered depth pass had skinned casters, and
  `CaptureShadowCasters` (:2917) iterates only the rigid `_runs`, so when a character despawns or is
  dropped by `ClassifySkinnedVisibility` (:1905) every dirty input reads false and the atlas is reused
  un-cleared. The character's shadow stays on the ground until an unrelated event (a full-texel camera
  pan, a sun move, a rigid caster change, a resolution change) forces a re-render. The texel-snapped
  cascade fit widens the trigger: any sub-texel camera motion still leaves `lightMatrixChanged` false,
  so a perfectly frozen camera is not required. `ShadowDepthDirtyTests.cs:37` currently asserts the buggy
  frame is clean. Fix: persist last-rendered skinned presence alongside the other `_last*` fields and OR
  it into the dirty check, with a test covering the true-to-false transition.
- [ ] **Undoing an Add after a values edit silently leaves a stray scatter override.**
  `AddScatterOverrideCommand.Revert` (`EditorCommands.cs:1365`) removes by reference, but
  `EditScatterOverrideValuesCommand`'s ctor deep-clones `oldValue` (line 1505) and its `Revert` (1532)
  restores that CLONE, evicting the original instance from the document. `MapScatterOverrideDoc`
  (`MapDocument.cs:133`) has no `Equals` override, so the later `Remove` compares by reference, finds
  nothing, and removes nothing. Proven by a throwaway test: after Add, values edit, and two undos the
  override count is 2 against a baseline of 1, while `History.UndoDepth == 0` and `IsDirty == false`
  both pass, so the editor renders "Save" with no asterisk over a corrupted document. Fix: have
  `AddScatterOverrideCommand` capture its index at Apply and revert via `RemoveAt`, which is
  identity-independent and also hardens the latent `AddScatterLayerCommand`/`EditScatterLayerCommand`
  pair at 1740/1818.
- [ ] **"[+ add companion]" crashes the editor on a document with no scatter layers.**
  `MapEditorScene.RunOutlineAction` (`MapEditorScene.cs:1446-1447`) takes a `: ""` branch and commits a
  companion layer with `HostLayer == ""`. The command's `AffectsWorld` is true with a null (full)
  `DirtyRegion`, and because `OnUpdate` runs `UpdateChrome` before `CheckWorldRebuild`, the same frame's
  rebuild reaches `ViewportWorld.BuildPropLayers`, which throws `MapDocumentException` for the undeclared
  host, out of `OnUpdate`. The outline affordance is emitted unconditionally, so nothing stops an
  operator reaching it on a fresh session. All three independent finder angles flagged this one. Fix:
  gate the outline node on `ScatterLayers.Count > 0`, or treat an empty `HostLayer` as
  unconfigured-and-skipped and leave rejection to the save-time validator.
- [ ] **A rename gesture that returns to its starting name corrupts the history stack on redo.** The
  inspector Name row writes through per keystroke (`PropertyGrid.cs:250-255`, no commit event) and
  nothing seals the gesture while the row keeps focus (`SealGesture` is wired to `FloatRow.GestureEnded`
  only, `MapEditorScene.cs:1937`). Typing "2" then backspacing chains two renames that `TryMerge`
  collapses into one command with `_oldName == _newName`. Redo then calls the non-self-excluding
  uniqueness guard, which matches the source object itself and throws, with no try/catch on the redo
  path. Affects `RenameRegionCommand` (Apply `EditorCommands.cs:2135`), `RenameScatterLayerCommand`
  (1864), and `RenameCompanionLayerCommand` (2034). Fix: give the guards the self-exclusion that
  `GuardNoFeatureName` already has via `exceptIndex`, so a collapsed self-rename applies as a no-op.
- [ ] **`ShadowMapResolution` and `ShadowCascadeCount` are documented knobs that can never take effect.**
  `ShadowMode.cs:119`/:139 document these as construction-time quality settings ("a low-end profile can
  drop to 1024 or 512", echoed at `docs/USING-KHAOZENGINE.md:1746`), but no public API offers the window:
  `Render3DSurface`/`Render3DPreview` construct `Scene3D` in their own ctor with no settings parameter,
  `Scene3D`'s ctor is internal, and `Post` is a get-only self-instantiated property. The only resize path
  (`ShadowMapRenderer.EnsureLayout`) is called once from its own ctor on an internal class, so the atlas
  is permanently 2048 x 3 (~48 MB) and a post-construction write is accepted with no error and ignored.
  Aggravating the contract, sibling fields on the same object (`ShadowNearDistance`,
  `ResolvedMaxDistance`) ARE re-read every frame, so two fields are inert while their neighbours are
  live-tunable with nothing marking the difference. Fix: either forward an optional settings object
  through the `Render3DSurface`/`Render3DPreview`/`Render3DSnapshot` ctors, or make a settings change
  re-run `EnsureLayout` so the fields behave like their siblings.
- [ ] **`FollowCamera3D.Eye` issues a physics sweep on every property read.** The getter
  (`FollowCamera3D.cs:169`) runs a full `IPhysicsWorld.SweepCapsule` plus a `GroundHeight` sample, and
  `Forward`/`View`/`ViewProjection`/`WorldToScreen`/`ScreenToRay` all funnel back through it, so one
  `Scene3D.Render` pass re-enters it at 33 sites and issues dozens of broadphase sweeps where one would
  do. Only bites when a consumer opts into `Occlusion` (Showcase `Room3D.cs:248` and `RoomDungeon.cs:189`
  both do, as would any Ruinborne-style third-person game). Sibling `IsoCamera3D.Eye` is pure arithmetic,
  so nothing at the call sites signals that reading a camera property is expensive. Fix: compute `Eye`
  once per frame into a backing field invalidated by the setters.
- [ ] **18 of 27 subsystems were never reviewed.** The 2026-07-17 review covered only `Render3D` and
  `MapEditor`. Not read at all: `Netcode`/`NetWorld`/`Replication`/`Sharding`, `Ecs`/`Simulation`, `Gpu`,
  `Windowing`, `Gui`, `Game`/`Game.Render3D`, `Physics`/`Physics.Bepu`/`Collision`/`Locomotion`/
  `Navigation`, `Terrain`, `Updates`, `Dungeon`, `Render2D`, `Audio`, `Particles`/`Effects`/`Telegraphs`,
  `Primitives`/`Pooling`/`Determinism`, `Platform`/`App`/`Diagnostics`/`Persistence`/`Localization`, the
  `WorldStore`/`Commerce`/`Identity`/`Social` services, and `Showcase`. The hit rate on the two packages
  that were reviewed (10 confirmed defects, 8 of them high severity) is the argument for finishing the
  sweep. Note the cost shape for whoever picks this up: the per-candidate verify fan-out is what makes it
  expensive, so cap it (high-severity only) rather than running it unbounded.
