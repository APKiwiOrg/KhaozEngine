# KhaozEngine roadmap

Future work only: what's planned or missing, highest-priority first. This file does NOT record shipped
history. See [CHANGELOG.md](../CHANGELOG.md) and `git tag` for what landed and when. When an item ships,
delete it from here (the detail moves to the changelog) rather than marking it "done".

Current released version: **9.2.0** (the shared `<KhaozEngineVersion>` line in `Directory.Build.props`).

Each near-term item gets its own design spec + plan under `docs/superpowers/` when it is scheduled.

## Near-term (next up)

### 1. Engine-level Discord social SDK

Discord integration (rich presence, join/invite, lobbies, OAuth identity) is currently done bespoke per game.
Centralize it: a `KhaozEngine.Social` seam (`ISocialProvider`: presence, invites/friends, identity, optionally
achievements) with a `KhaozEngine.Social.Discord` implementation over the Discord Social SDK, so every game gets
Discord from the engine. Retire the game-specific Discord implementations once the package ships.

- Design the seam for extensibility: the provider interface should leave room for Steam / other platforms later
  behind the same contract.
- Native dependency: the Discord SDK ships native libraries, so the `.Discord` package pulls them while the
  `Social` core seam stays dependency-free, mirroring the opt-in-backend pattern (`Netcode.LiteNetLib`,
  `WorldStore.*`). Per-RID bundling is the same concern as Audio/Windowing.

### 2. Physics engine: dynamic bodies + constraints

The static-body physics seam (`IPhysicsWorld` + the opt-in `KhaozEngine.Physics.Bepu` backend) is in place,
and character movement collide-and-slides capsule-vs-mesh against it. What remains:

**Dynamic rigid bodies + their replication.** A body that falls / bounces / rests needs a replication component
(like `MovementState` for players); `Scene3DChunkSink` would drive `AddDynamic`/step per loaded chunk, and
`WorldClient` would interpolate dynamic-body positions from replicated snapshots. Enables physics-driven crates,
barrels, falling debris. Terrain-as-physics-geometry (the whole terrain mesh fed into a `TriangleMesh` body) also
lands here: a static terrain body replaces the `TerrainCollision` delegate in the `Step` call so all surfaces
(terrain + props + buildings) share one query path.

**Constraints, joints, and vehicles.** Hinges, sliders, ragdolls, wheeled vehicles.

### 3. Visual fidelity (textures + materials)

The terrain now renders PBR splat textures. Props can now carry albedo/normal/roughness surface detail too
(`PropLoader.LoadPropWithMaterial` reads a prop glTF's textures, opt-in via the `textured` manifest flag; a
prop with no textures still degrades to the flat render). Goal: make props, trees, and buildings actually look
good, not just read as shapes.

- ~~Textured props~~ landed: `PropLoader.LoadPropWithMaterial` reads a prop's baseColor/normal/roughness
  textures instead of flattening them to a base-colour factor. `MeshOps.WithTangents` gives a UV-mapped
  primitive mesh a tangent basis so normal maps take effect, and `PropMaterialPresets.Procedural` generates an
  asset-free mossy-stone albedo+normal for samples and tests. Remaining: real Quaternius kit re-ingest with
  textures on (today's samples use the procedural preset or opt in per-asset), and multi-texture-per-primitive.
- Water: a real water shader for the lake / sea (currently a flat plane at the water level).
- Lighting polish: pairs with shadows (see Rendering) + an HDRI/sky direction for a cohesive look.
- CC0-asset-friendly throughout (ambientCG terrain textures, the kit textures), no new heavy dependencies.

## Netcode / MMO refinements

- Delta + AoI unification: fold the interest-grid filtering into the delta encoder (one pass instead of two).
- Delta bit-packing / quantization: shrink per-snapshot bandwidth.
- SpaceGame as the first netcode adopter / testbed: validate the authoritative stack on a real game.

## Overworld / world content

- Procedural dungeon generator.
- Animated characters / creatures: needs a glTF animation-clip-playback feature first (also unblocks the
  SpaceGame 2.5D rigged-creature direction and pairs with the physics work).
- Per-cell world-state snapshot persistence: persist cell/world state, not just player records (pairs with
  sharding).
- Visual fidelity (textured props, water): see Near-term item #3 above.

## Rendering

- Shadows: 3D shadow rendering (shadow maps). A named gap from the 5.x engine audit; the MRT / depth
  infrastructure already exists.
- Depth-sorted transparency (3D): transparent meshes and billboards currently render unsorted, so overlapping
  alpha is draw-order dependent. Sort back-to-front (or add order-independent transparency) for correct blending.

## Cross-platform reach

- Mobile: iOS / Android platform layers (lifecycle, touch, packaging, store submission). Silk.NET is the
  windowing/input foundation it builds on.
- GL backend verification: the `gl` Veldrid backend override parses but is unverified; waits on real GPU CI /
  hardware. See [CROSS-PLATFORM.md](CROSS-PLATFORM.md).
- Live gamepad smoke: polling is best-effort + compile-verified; needs an on-device pass with a physical
  controller.
- Richer text entry: IME / locale / dead-keys (the current `TextEntry` is US-layout key-mapping).

## Tooling & developer experience

- On-screen profiling / diagnostics overlay: a live frame-time / draw-call / memory overlay (logging only
  today). The Gui makes it cheap to build.
- Asset hot-reload: reload meshes, textures, and shaders at runtime during development. The prop asset pipeline
  shipped, but hot-reload did not.

## Possible future factoring (unscheduled)

- Shared 2D/3D particle sim core: `KhaozEngine.Particles` (3D) and `Render2D.Vfx.Particle2DSystem` (2D) share
  the emit/integrate/lerp-over-life model; a pass could factor the common sim core. No single target today.
- Nullwake camera convergence: Nullwake's `OreField.RefToScreen` is a non-uniform scale into a screen sub-rect,
  not `Camera2D`. Converging would need sub-viewport + non-uniform-scale support in the engine camera, else it
  stays game-specific (see [CONSUMERS.md](CONSUMERS.md)).
