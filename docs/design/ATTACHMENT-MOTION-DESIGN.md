# Attachment motion: hanging parts that swing

Status: planned for 24.5.0 under [#1879](https://github.com/APKiwiOrg/KhaozEngine/issues/1879), with byte loading of
a skinned body and its clips under [#1876](https://github.com/APKiwiOrg/KhaozEngine/issues/1876). This note keeps the
decisions and the reasons for them. Each task records the shapes the code corrects in the last section.

A worn skinned part may carry joints of its own beyond the body's. They form chains, each hanging from a body joint:
a cloak's panels, a skirt's strips, a strap tail, a braid, a pouch. A motion stage moves those joints every frame
after the body's pose is final and before the garment's bone palette is written. Chains swing, trail the wearer,
settle under gravity and are pushed out of the body's colliders. Every render pass reads the same palette, so
colour, depth, motion vectors, cascade and point shadows, outlines and dissolve all show the same swing.

## Decisions

### 1. Chains are Verlet particles under position constraints

Each simulated chain joint is a particle that carries its own velocity and gravity, is pulled back toward its rest
shape, keeps its segment lengths, bends no further than a limit and is pushed out of capsules. Neighbouring chains of
one part may be linked softly, so a cloak's panels stay together when a leg pushes one of them. This is the spring
bone family games use for capes, tails and hair, and it is cheap enough for every wearer on screen.

Damped angular springs per joint were rejected because a rotation spring has no position to push out of a collider.
Rigid bodies in the physics backend were rejected because presentation would need a physics world on every client
and would cost far more at 32 wearers. Particles, distance constraints and capsule collision are also the parts a
cloth solver is built from, which keeps the path to cloth ([#1870](https://github.com/APKiwiOrg/KhaozEngine/issues/1870)) open.

### 2. Time is substepped and every step ends at the frame time

Each frame's interval is split into `n = clamp(ceil(dt / maxStep), 1, maxSteps)` substeps, at most 1/120 s each and
8 per frame, with the anchors, colliders and wearer interpolated across the substeps. A chain therefore never lags a
frame behind a fast body and never detaches from it. Stiffness and damping are rates per second, so 30, 60, 144 and
240 Hz frames (substeps of 8.3, 8.3, 6.9 and 4.2 ms) look alike.

A fixed rate with presentation interpolation is exactly rate independent but shows the chain one step late, which on
a running wearer is several centimetres unless every state is re-anchored. One clamped step per frame changes the
look with the frame rate and goes unstable on a hitch.

### 3. Swing state belongs to a per wearer `WornMotion`

Binding resolves once and can be shared. Motion changes every frame for one wearer. A `WornMotion` is made from a
bound outfit or a bound set, stepped once per frame and passed to the draw calls. Two wearers can share one bound
outfit and its mesh views, each with its own motion. A consumer that never makes a motion still draws a chained
piece, with its chains at their rest hang. A cloth part later substitutes a per wearer deformed mesh through the same
object.

Keeping the state inside `BoundOutfit` and `BoundAttachmentSet` would make a binding unshareable without saying so.
Keeping it in `Scene3D` per motion key would hide a wearer's lifetime inside the renderer.

### 4. The solver is GPU free

The solver, the chain data and the collider data live in `KhaozEngine.Attachments`, which keeps its one reference,
to `KhaozEngine.Primitives`, and uses only `System.Numerics`. Binding, motion and draws live in
`KhaozEngine.Attachments.Render3D`. A fitting tool or a pose check can run the swing over exported poses without a
renderer, as it can run the outfit rule. No package or dependency edge is added.

### 5. The capability is a minor, 24.5.0

Format 4, body colliders, the solver, motion and the byte loaders add types, members and overloads. No public
interface gains a member, so the release is additive.

## Chains

A chain is an ordered list of joints in a skinned part's own skin, from the joint nearest the body to the tip.

- The first joint's parent in the garment GLB is a node named like a joint of the host skin. That host joint is the
  chain's anchor.
- Each later joint's parent is the joint before it. Chains do not branch. A Y shaped strap is two chains on one
  anchor.
- The first joint is fixed to its anchor by its rest local transform, so a strap stays on its buckle. It still turns,
  to aim at the joint after it. Every later joint and one virtual tip point are simulated.
- The tip is a point in the last joint's rest frame, declared per chain, so the last joint turns too. A one joint
  chain is a pouch or a pendant, and its tip is where it hangs to.
- Every garment skin joint outside the part's chains binds to a host joint by name with the same inverse bind, as
  before. A chain joint must not share a name with a host joint.
- A chain joint's inverse bind matches its rest transform composed on its anchor's bind, within the binding
  tolerance, so the part is undeformed at rest.

A hanging rigid thing, such as a pouch on a belt, is a skinned part whose vertices are all weighted to a one joint
chain. One source of chain data serves every hanging piece, and per triangle hiding works on it as on any skinned
part.

### Settings

Every chain states every setting. The format has no defaults, so a piece never changes look when an engine default
moves.

| Setting | Unit and range | Meaning |
| --- | --- | --- |
| `stiffness` | radians per second, 0 or more | The natural frequency of the spring that returns a segment toward its rest direction relative to its parent. 0 is limp. |
| `damping` | per second, 0 or more | How fast velocity decays. Speed is multiplied by `exp(-damping * h)` each substep. |
| `gravity` | multiple of world gravity, 0 to 10 | How strongly the chain falls. |
| `trail` | 0 to 1 | How much of the wearer's own movement through the world the chain lags behind. 1 is full inertia. 0 carries the chain with the wearer, so only the body's animation swings it. |
| `angleLimit` | radians, above 0 and at most pi | How far a segment may bend away from its rest direction relative to its parent. |
| `radius` | metres, 0 or more | The chain's thickness against colliders. |

Links name pairs of chains in one part. A link keeps the movable joints at the same position in both chains, and both
chains' tips, near their rest spacing through a spring of 10 radians per second. Tips are linked because the tip
sets the last joint's turn, so an unlinked tip would let a hem splay.

## Colliders

A collider is a capsule bound to a host joint: two points and a radius, in the body's rest model space, the space a
fitting step works in. A sphere is a capsule whose points are equal. At bind time each collider moves into its
joint's frame through the host's inverse bind. Each frame that joint's palette matrix and the model pose it.

- A body declares its colliders once in a body collider file, bound to the body's `rig-v1` identity like a region
  map.
- A worn set may add colliders while it is worn, so outerwear clears the armour under it. They use the same space
  and bind the same way.
- Every chain of a wearer collides with every collider of that wearer, the body's and those of all its worn sets.
- Chains do not collide with each other or with other wearers.

Caps bound the cost and are refused past: 32 colliders in a body file, 8 in a worn set and 64 in one wearer's
motion. A part holds at most 32 chains and 64 links, and a chain at most 16 joints. The garment skin keeps its 128
joint limit, which counts chain joints.

## One step

Each frame the motion takes the host palette, the model matrix and the frame time. It composes each chain's rest
hang, the chain joints' rest transforms on the anchor's current world transform, and poses every collider in world
space. Then it runs the substeps of decision 2. Time beyond `maxSteps * maxStep` is dropped. Within each substep:

1. **Trail.** Every particle and its previous position move by `(1 - trail)` of the wearer's rigid movement over the
   substep.
2. **Predict.** Time corrected Verlet. Velocity comes from the last two positions, scaled by `h / hPrevious` for a
   changed substep length and damped by `exp(-damping * h)`. Gravity's displacement `a * h^2 / 2` is added to the
   prediction and subtracted from the stored previous sample, so a chain released from rest falls exactly as constant
   acceleration predicts at any substep length.
3. **Root to tip, per chain.** A joint's unswung rotation is its rest local rotation on its parent's final rotation,
   the anchor's for the first joint, and its rest direction follows from that. A spring of natural frequency
   `stiffness` pulls the next particle toward that direction. It is solved by the implicit midpoint rule with weight
   `w = 2z / (1 + z)`, `z = (stiffness * h / 2)^2`, and the correction is written into both Verlet samples so it is
   a velocity impulse, which keeps the motion the same across substep lengths and stable for any of them. Then the
   bend is clamped to `angleLimit`, the particle is pushed out of every collider on the sphere of its segment length,
   and the length is restored. The joint's rotation is the swing that turns its rest direction onto the simulated
   one, applied to its unswung rotation, which keeps the animation's twist.
4. **Links.** Every linked pair reads the same snapshot, and each particle takes the mean of its pairs' midpoint
   corrections, so a particle in several links never takes a stiffer step and the update stays stable. Then a
   second root to tip pass without the stiffness spring restores the bend limit, collisions and lengths.

The anchored first joint and segment lengths always hold. A collider outranks the bend limit, because a pass through
looks worse than an extra bend. Contact solves the capsule locally as a sphere at the closest point of its axis, up to
eight passes, in double precision. Segment lengths come from this frame's rest hang, so a scaled wearer keeps its
proportions.

Known limits: contact acts on joints and tips, so a long segment can cross a thin capsule between its ends, and the
fitting step uses enough joints to avoid it. While a held chain blends back to the live one, the shown chain can
overlap a capsule for at most the blend time, while the live chain already clears it.

Each chain joint's result is kept relative to its anchor, and drawing composes it on the anchor's current world
transform. A frame that draws without stepping therefore carries the chain with the body in its last shape and never
leaves it behind.

### Resets and limits

- **Reset.** The first step, `Reset()`, a wearer that moved farther than the teleport distance in one frame, and a
  frame time longer than the longest gap all put every chain at its rest hang with no velocity, so a respawn or a
  teleport is calm.
- **Hold.** A step at `MotionLevel.Held` runs no substeps. The chains blend from their last shown shape to the rest
  hang over the hold blend time and stay there. A later simulated step starts the live chain from the held shape with
  no velocity and blends the shown chain to it over the same time. Each blend continues from the last shown swing, so
  an anchor turning through a half turn during a blend never switches arcs, and neither switch pops.
- **Picking who swings.** `MotionLevels.Assign` sets the nearest wearers within a distance to simulated, up to a
  count, and the rest to held, writing into caller spans without allocating.

| Option | Default |
| --- | --- |
| Longest substep | 1/120 s |
| Most substeps per frame | 8 |
| Teleport distance | 2 m |
| Longest gap | 0.5 s |
| Hold blend | 0.25 s |
| World gravity | (0, -9.81, 0) |

These are motion options with defaults in the API, not in a file, because they describe the game's world and frame
budget, not a piece. A substep is at most 1/30 s and a frame at most 64 substeps, because a presentation solver needs
nothing coarser and the link update is proven stable within those bounds.

## Format 4 and the body collider file

`AttachmentSetJson.MotionFormatVersion` is 4. Format 4 is format 3 with two optional members: `motion` on a skinned
part and `colliders` at the root. `Write` emits format 4 only for a set that has either, so every format 1, 2 and 3
definition keeps its bytes and `SemanticSha256`. A set with motion or colliders needs a layer.

```json
{
  "formatVersion": 4,
  "layer": "outerwear",
  "covers": ["neck"],
  "colliders": [
    { "joint": "upper", "start": [0, 1.30, -0.10], "end": [0, 1.30, -0.10], "radius": 0.09 }
  ],
  "parts": [
    {
      "id": 1,
      "kind": "skinned",
      "mesh": { "key": "parts/cape.glb", "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
      "regions": [["neck", 12], ["back", 52]],
      "motion": {
        "kind": "chains",
        "chains": [
          { "joints": ["left0", "left1", "left2"], "tip": [0, -0.25, 0],
            "stiffness": 6, "damping": 3, "gravity": 1, "trail": 0.7, "angleLimit": 1.4, "radius": 0.02 },
          { "joints": ["right0", "right1", "right2"], "tip": [0, -0.25, 0],
            "stiffness": 6, "damping": 3, "gravity": 1, "trail": 0.7, "angleLimit": 1.4, "radius": 0.02 }
        ],
        "links": [[0, 1]]
      }
    }
  ]
}
```

`motion.kind` is data, and `chains` is the one kind in 24.5.0. An unknown kind refuses with a typed reason, so a
reader that predates cloth refuses a cloth piece instead of drawing it wrong.

The body collider file, format 1:

```json
{
  "formatVersion": 1,
  "rig": "rig-v1:0000000000000000000000000000000000000000000000000000000000000000",
  "colliders": [
    { "joint": "upper", "start": [0, 1.05, 0], "end": [0, 1.40, 0], "radius": 0.14 },
    { "joint": "legL", "start": [0.10, 0.90, 0], "end": [0.10, 0.50, 0], "radius": 0.07 }
  ]
}
```

Its canonical bytes and SHA-256 follow the region map's rules.

## Public surface

`KhaozEngine.Attachments`, GPU free:

- `ChainSettings`, `ChainDefinition`, `ChainLink`, `PartMotionDefinition` and `ChainMotionDefinition`.
  `SkinnedAttachmentPartDefinition.Motion` is init only and null for a part that only follows the body.
- `ColliderDefinition`, `AttachmentSetDefinition.Colliders` and a constructor overload that takes them.
- `BodyColliderSet`, `BodyCollidersJson`, `BodyCollidersRefusal` and `BodyCollidersDataException`.
- `ChainRig` (immutable rest data for one wearer's chains), `ChainSimulation` (one wearer's particles, allocated once)
  and `ChainSimulationOptions`. Plain numbers go in and joint frames come out, so a tool can drive it from exported
  poses.
- `MotionLevel` (`Simulated`, `Held`) and `MotionLevels.Assign`.

`KhaozEngine.Attachments.Render3D`:

- `AttachmentSetCache.GetBodyColliders(key, expectedSha256, bodyRigIdentity)` returns `LoadedBodyColliders`.
- `BoundOutfit.CreateMotion` and `BoundAttachmentSet.CreateMotion` take the body colliders, or none, and options, and
  return a `WornMotion`. With no body colliders the chains collide with worn set colliders only.
- `WornMotion.Step(skeleton, palette, model, deltaSeconds, level)`, `Reset()`, and `Tune(partId, chain, settings)`
  for a live tuner.
- Overloads of `BoundOutfit.WriteDraws` and `QueueOutline`, and of `BoundAttachmentSet.WriteDraws`, `Queue` and
  `QueueOutline`, that take the motion. A motion made for another outfit or set refuses with a typed reason.

`KhaozEngine.Render3D`:

- `GltfLoader.LoadSkinnedWithMaterial(ReadOnlyMemory<byte>, string)` and
  `GltfLoader.LoadAnimations(ReadOnlyMemory<byte>, string)`.

## Binding and drawing

- **Binding.** `CreateMotion` resolves every chain against the garment skin and the host: chain joints by name in the
  garment skin, anchors by name in the host skin, colliders by joint. It checks each chain's shape and binds and
  allocates every buffer the wearer needs.
- **Frame order.** Animate the body, step the motion once, then write draws and outlines through the motion as often
  as needed. Draw calls only read the motion, so a draw and an outline always agree.
- **Palettes.** A chained part's palette stays in garment skin order. Host joints come from the host palette as
  before. Chain joints come from the motion, composed on the current anchor and taken into model space. The renderer
  applies the garment's inverse binds as before.
- **Without a motion.** The existing overloads write chain joints at their rest hang, so a consumer that adopts a
  chained piece before it adopts motion draws it hanging still.
- **Every pass.** Draw records copy the palette, so colour, cascade and point shadows, outlines and dissolve need no
  change. The scene keeps the previous palette per motion key, so motion vectors carry the swing unchanged.
- **Culling.** A skinned draw is culled by its mesh's rest bounds sphere times 1.5, and a swung tip can leave that
  sphere. When a set with chains loads, its chained meshes' bounds grow to contain each chain's reach: a sphere on the
  anchor's rest position whose radius is the chain's length plus its tip plus the farthest chain weighted vertex from
  its joint. The growth goes through an internal `Scene3D` seam that the shipped scene adapters call, and a view takes
  its source's grown bounds.
- **Hidden parts.** A part the outfit hides entirely is not stepped.
- **Allocation.** `CreateMotion` allocates everything. `Step`, `WriteDraws`, `Queue` and `QueueOutline` allocate
  nothing once warm.

## Coverage claims and swinging triangles

An outfit's hidden set is fixed when the outfit is bound. A cloak that claimed the back would hide the shirt's back,
then swing away and show a hole. A claim may therefore rest only on triangles that do not swing:
`CoverageClaimCheck` ignores every piece triangle with a vertex weighted to a chain joint, and a fitting step claims
only what the fixed part of a piece covers, such as a cloak's collar.

The fitting tool derives a per-vertex mask from the same skinned GLB it imports and the part's chain joint names.
Both use flattened mesh, primitive and vertex order so each flag describes the position at the same index.
The [coverage claim check reference](../../KhaozEngine.Attachments/README.md#coverage-claim-check) describes the API.

## Room for cloth

- A part's movement kind is data, `motion.kind`. Cloth adds a kind and its settings in a later format.
- Colliders, the body collider file, region tags, layers and fitting do not change when cloth arrives.
- `WornMotion` is the one place that turns a movement kind into what a draw needs. Chains give joint matrices for the
  garment palette. Cloth will give a per wearer deformed mesh, which the same draw overloads substitute for the
  part's shared mesh.
- The kinds share the substep loop, collider posing and the reset and hold rules, and a cloth solver reuses the
  particles, distance constraints and capsule collision.

Live reshape ([#1869](https://github.com/APKiwiOrg/KhaozEngine/issues/1869)) is untouched. Chains bind per fitted
body, like everything else in a piece.

## Cost

The wearables limits are 32 wearers, at most 2 ms added CPU preparation, no managed allocation once warm and at most
1.3 ms added GPU time at 32 characters on a T4.

- CPU, an estimate until measured: a cloak of five chains of five joints and four strap tails of three joints is
  about 37 particles against about 12 colliders. Each joint takes a few matrix products and a pass over the colliders
  per substep, twice when links are present, so 32 wearers cost roughly 0.8 ms a frame at 144 Hz.
- GPU: no shader, pass or draw count changes. A chained part adds palette entries within the 128 joint limit.
- Allocation is tested per call, as outfit draws are.
- `KhaozEngine.Benchmarks` gains a swing mode at 32 wearers. Its timing runs on a hosted runner, outside default CI.

## Byte loading

A consumer that hashes a body's bytes for its rig identity must upload the same bytes. The byte entries read the
flattened skinned mesh with its material maps, and the animation clips, from one snapshot. The mesh entry keeps the
path overload's primitive order and its first textured material rule. Images held in the GLB buffer or in data URIs
load. An image named by a relative path refuses with a new `GltfImportRefusal` reason, because the bytes alone cannot
say what the file names and a silent drop would hide a missing texture. The path overloads stay for assets with
external images.

## Refusals

Each new reason is appended to its enum and carries the identifiers that locate the fault: part, chain, joint, link
or collider.

- `AttachmentSetRefusal`: an unknown motion kind, an empty chain, a duplicate chain joint, a setting out of range, a
  bad tip, a bad link, a bad collider, too many chains, joints, links or colliders, and motion or colliders without a
  layer.
- `BodyCollidersRefusal`, a new family: an unsupported format version, an unknown or duplicate property, a bad rig
  identity, an empty joint name, a bad radius or point and too many colliders.
- `AttachmentBindingRefusal`: a chain joint missing from the garment skin, a chain joint named like a host joint, a
  chain that is not a parent line, an anchor that is not a host skin joint, a chain bind mismatch, an unresolved
  collider joint, body colliders for another rig and too many colliders on one wearer.
- `AttachmentSubmissionRefusal`: a motion made for another outfit or set.
- `GltfImportRefusal`: an image named by a relative path in a byte import.

## Tests

All headless, on synthetic skeletons built in code.

- **Solver.** A chain released level falls and settles below its anchor. It settles to the same pose at 30, 60, 144
  and 240 Hz within 1 cm at the tip, and its first swing peaks within 10 percent across those rates. Random frame
  times from 1 ms to 500 ms, hitches included, stay finite. A 10 m teleport shows no whip. No particle ends a step
  inside a collider. Bends stay within the limit. Trail 0 keeps the chain's shape in the wearer's space while the
  wearer moves steadily, and trail 1 lags it. A hold and a resume move no joint further in a frame than the blend
  allows. Linked chains keep their spacing when a collider pushes one. Nothing allocates once warm.
- **Formats.** Format 4 and the body collider file round trip. Each refusal sits beside a passing control that
  differs only in the broken rule. Format 1 to 3 bytes and hashes are unchanged.
- **Binding and draws.** A chained garment binds and draws. Each binding refusal is reached. Without a motion, chain
  joints sit at their rest hang. After steps the palette's chain joints move and its host joints do not. Draws and
  outlines read the same palette. A motion from another outfit refuses. Grown bounds contain the reach. Steps and
  draws allocate nothing once warm.
- **Coverage claim.** A claim that rests only on swinging triangles is reported uncovered.
- **Byte loading.** The byte entries match the path entries for a GLB with embedded images and refuse a relative path
  image.

## Not in 24.5.0

Cloth, live reshape, wind, chains colliding with each other or the ground, rigid parts bound to chain joints and a
rigid part riding a bending surface.

## Signatures corrected against the code

Each task records here any shape the code required that differs from this note, keeping the planned behaviour.

- Body colliders. `BodyColliderSet`'s constructor refuses with `BodyCollidersDataException`, as the region map's
  constructor does, so a file and a constructed set report a broken rule the same way. A collider sequence that cannot
  report its size is read only one item past `MaxColliders` before it refuses.
- Byte loading. The byte overloads of `LoadSkinnedWithMaterial` and `LoadAnimations` check every image in the GLB,
  including images no material uses, because the model reader resolves every image a GLB names. Images in a buffer
  view or a data URI are accepted. Any other image URI raises `GltfImportDataException` with
  `ExternalImageInByteImport`, the caller's `Identity` and the zero based `ImageIndex`, counted across repeated
  `images` arrays as the model reader counts them. The scan reads the GLB's JSON chunk strictly, so invalid UTF-8 and
  broken chunk framing refuse as `MalformedModel`. The path overloads resolve external images against the asset's
  folder as before.
- Worn set format. `AttachmentSetArgumentException` exposes `ActualCount` and `ExpectedCount` for collection
  limits, using the existing names on `AttachmentSetDataException`. A joint name with an unpaired escaped
  surrogate has no decoded `JointName`. The JSON refusal identifies it by path and input indices.
