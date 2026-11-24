# Attachment motion: hanging parts that swing

Implemented for 24.5.0 under [#1879](https://github.com/APKiwiOrg/KhaozEngine/issues/1879), with byte loading of
a skinned body and its clips under [#1876](https://github.com/APKiwiOrg/KhaozEngine/issues/1876). 24.8.0 keeps
corrections out of velocity ([#1882](https://github.com/APKiwiOrg/KhaozEngine/issues/1882)), steps on a fixed grid
([#1883](https://github.com/APKiwiOrg/KhaozEngine/issues/1883)) and adds a width bound for linked chains
([#1884](https://github.com/APKiwiOrg/KhaozEngine/issues/1884)). It also skips capsules outside each chain's reach.
The [consumer guide](../USING-KHAOZENGINE.md#hanging-parts-that-swing) covers setup and frame submission.

A worn skinned part may carry joints of its own beyond the body's. They form chains, each hanging from a body joint:
a cloak's panels, a skirt's strips, a strap tail, a braid, a pouch. A motion stage moves those joints every frame
after the body's pose is final and before the garment's bone palette is written. Chains swing, trail the wearer,
settle under gravity and are pushed out of the body's colliders. Every render pass reads the same palette, so
colour, depth, motion vectors, cascade and point shadows, outlines and dissolve all show the same swing.

## Decisions

### 1. Chains are particles under position constraints

Each simulated chain joint is a particle that carries its own velocity and gravity, is pulled back toward its rest
shape, keeps its segment lengths, bends no further than a limit and is pushed out of capsules. Neighbouring chains of
one part may be linked softly, so a cloak's panels stay together when a leg pushes one of them, and the part may bound
how far linked panels spread. This is the spring bone family games use for capes, tails and hair, and it is cheap
enough for every wearer on screen.

Damped angular springs per joint were rejected because a rotation spring has no position to push out of a collider.
Rigid bodies in the physics backend were rejected because presentation would need a physics world on every client
and would cost far more at 32 wearers. Particles, distance constraints and capsule collision are also the parts a
cloth solver is built from, which keeps the path to cloth ([#1870](https://github.com/APKiwiOrg/KhaozEngine/issues/1870)) open.

### 2. Corrections move positions only, and a limit shares its stop with the parent

A particle carries a position and a velocity. Gravity, damping, the stiffness spring and the links change velocity.
The bend limit, capsule clearance, segment lengths and the width bound only move positions. After a move, velocity
constraints remove what would leave the constraint again: velocity along the segment, outward velocity at the bend
limit and approach toward a capsule.

When a moved position also became velocity, every correction fed the next step. A particle that started a step
outside its limit, because its parent was corrected first in the root to tip pass, kept its whole correction as speed,
and a particle held against a limit kept half of each step's force pressing into it. Moving positions only removes
both.

The bend limit's wall moves with the parent. A child that reaches it stops against it, and the stop is shared with the
parent's end particle as between two equal masses. A child of the anchored first joint takes the whole stop, because
the anchor does not move in response. A stop taken by the child alone hands it the parent's turning rate for free
while the parent never feels the child, so turning rate stacks down a chain and its tip whips when the wearer stops.
Removing only the child's outward speed relative to the moving wall keeps that stacking, and so does driving the wall
from velocities alone. Sharing the stop moves momentum between the two instead of creating it.

The length constraint keeps the spring bone's one sided form: the child takes its whole speed along the segment
relative to the parent, and the parent never feels it. Each joint's spring therefore holds only its own segment,
which is what `stiffness` means. A length constraint shared with the parent would conserve energy exactly, but every
joint would then carry the weight of the chain below it, so every piece would hang lower than it was tuned to. The
cost is that a parent moving along its child's segment hands the child that speed, a small transfer the limits and
contacts then remove.

A full two sided solver with masses on every constraint is the cloth solver's job
([#1870](https://github.com/APKiwiOrg/KhaozEngine/issues/1870)). Chains keep the root to tip pass, their settings and
their cost.

### 3. Time advances on a fixed step and is shown re-anchored

Chains advance on a fixed step, at most 1/120 s by default, whatever the frame length. Each frame runs every step
whose time falls inside it, with the anchors, colliders and wearer interpolated at that step's time between the
previous frame's poses and this frame's. Every frame rate and every frame pacing therefore runs the same steps, and a
chain settles on the same path.

The shown chain is the anchor relative joint frames of the last two steps, interpolated at the frame time's place
between them and composed on the current anchor. The shape relative to the anchor is shown one step late, and the
chain never lags the body in world space.

Substeps that end at every frame time change the integration with every frame length, so a chain driven by a slow
breathing anchor shimmered under uneven pacing and a chain at rest against a capsule never quite stopped.
Interpolating world states instead of anchor relative ones shows the chain a step behind a moving body, several
centimetres on a running wearer. One extra uncommitted step from the last grid step to the frame time is exact, but
costs a step on every frame, which the cost limit cannot carry at 60 Hz.

### 4. Swing state belongs to a per wearer `WornMotion`

Binding resolves once and can be shared. Motion changes every frame for one wearer. A `WornMotion` is made from a
bound outfit or a bound set, stepped once per frame and passed to the draw calls. Two wearers can share one bound
outfit and its mesh views, each with its own motion. A consumer that never makes a motion still draws a chained
piece, with its chains at their rest hang. A cloth part later substitutes a per wearer deformed mesh through the same
object.

Keeping the state inside `BoundOutfit` and `BoundAttachmentSet` would make a binding unshareable without saying so.
Keeping it in `Scene3D` per motion key would hide a wearer's lifetime inside the renderer.

### 5. The solver is GPU free

The solver, the chain data and the collider data live in `KhaozEngine.Attachments`, which keeps its one reference,
to `KhaozEngine.Primitives`, and uses only `System.Numerics`. Binding, motion and draws live in
`KhaozEngine.Attachments.Render3D`. A fitting tool or a pose check can run the swing over exported poses without a
renderer, as it can run the outfit rule. No package or dependency edge is added.

### 6. The capability is a minor, 24.5.0, and the fix is a minor, 24.8.0

Format 4, body colliders, the solver, motion and the byte loaders add types, members and overloads. No public
interface gains a member, so the release is additive. 24.8.0 adds format 6 and two init only members and changes how
every chain moves, so it is a minor too.

### 7. Capsule contact follows the whole segment and keeps its opening side

A segment can cross a thin capsule between its joints. Even if the tip touches, the tip's contact circle can lie
past the segment's tangent point, folding the segment through the capsule. Use the tangent cone when the segment
reaches that point and the tip's contact circle for shorter segments. Both keep the authored segment length.
An anchored parent inside a capsule can clear only its endpoint.

A segment takes its side from its opening positions before the wearer carry when the residuals to the capsule point
to opposite sides and the straight path between them crosses the capsule. A capsule can then push a segment ahead
of it even when their closing poses are already separated. If the capsule is beyond the segment's reach at closing,
there is no correction.

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
| `damping` | per second, 0 or more | How fast velocity decays. Speed is multiplied by `exp(-damping * h)` each step. |
| `gravity` | multiple of world gravity, 0 to 10 | How strongly the chain falls. |
| `trail` | 0 to 1 | How much of the wearer's own movement through the world the chain lags behind. 1 is full inertia. 0 carries the chain with the wearer, so only the body's animation swings it. |
| `angleLimit` | radians, above 0 and at most pi | How far a segment may bend away from its rest direction relative to its parent. |
| `radius` | metres, 0 or more | The chain's thickness against colliders. |

Links name pairs of chains in one part. A spring of 10 radians per second keeps movable joints at matching depths,
and both chains' tips, near their rest spacing. Tips are linked because the tip
sets the last joint's turn, so an unlinked tip would let a hem splay.

A soft link only pulls, so under a moving body linked panels can spread to several times their rest spacing and come
back, which reads as rubber. A part may state `linkStretch`, above 0 and at most 1. The solver then steers each linked
pair into a band around rest spacing, within the position sweep budget and subject to the bend limit and capsules,
so a cloak of panels moves as one sheet. A garment that should hang as one piece states the bound. Strips that
should fan out rely on the springs alone. Stretch and compression share one bound because a sheet of panels should
neither part nor overlap.

## Colliders

A collider is a capsule bound to a host joint: two points and a radius, in the body's rest model space, the space a
fitting step works in. A sphere is a capsule whose points are equal. Each frame, the host inverse bind, the
joint's current palette matrix and the model compose the rest-to-world transform. This transform places the
endpoints and scales the radius by its largest axis scale. The inverse bind cancels the joint's rest scale
at bind, so the radius there is the authored radius before wearer scaling.

- A body declares its colliders once in a body collider file, bound to the body's `rig-v1` identity like a region
  map. A loaded collider file also belongs to the supplied host mesh's skeleton instance.
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
space. Then it runs the steps of decision 3: every step whose time falls inside the frame, at most `maxSteps` of them,
with older time dropped. The anchors, colliders and wearer are interpolated at each step's time between the previous
frame's poses and this frame's. Dropped time is crossed by the wearer's trail carry alone, which moves positions and
leaves velocities untouched, and the first kept step measures capsule motion, the anchor's turn and the first joint's
motion from its own opening poses. Measuring them from the last step before the dropped time divided all the skipped
motion by one step, so a slow capsule could throw a tip at many times its own speed. Within each step of length `h`:

1. **Trail.** Snapshot the opening positions before carrying. Every particle's position and velocity move with `(1 - trail)` of the wearer's rigid movement over the
   step, which starts from the previous step's wearer pose, sometimes in the previous frame. Velocities turn with the
   carried turn.
2. **Predict.** Velocity is damped by `exp(-damping * h)` and gains gravity times `h`. The position moves by the damped
   velocity times `h` plus gravity's `a * h^2 / 2`, so a chain released from rest falls exactly as constant
   acceleration predicts.
3. **Root to tip, per chain.** The anchored first joint moves with its anchor, and its velocity is the anchor's motion
   over the step, so the constraints below see the parent's real speed. A joint's unswung rotation is its rest local
   rotation on its parent's final rotation, the anchor's for the first joint, and its rest direction follows from that.
   - A spring of natural frequency `stiffness` pulls the next particle toward that direction. It is solved by the
     implicit midpoint rule with weight `w = 2z / (1 + z)`, `z = (stiffness * h / 2)^2`, on the particle's offset from
     its parent at the start and the end of the step. The correction `c` moves the position by `c` and the velocity
     by `2c / h`, which keeps the spring stable for any step. The start offset is measured from the parent's position
     at the start of the step. Measuring it from the parent's end of step position coupled the spring to the parent's
     speed by an amount that changed with `h`.
   - The bend is clamped to `angleLimit`. Capsule contacts turn the segment about its parent on its length
     sphere. If the opening and current residuals cross a capsule, the correction keeps the opening side. These
     moves only change positions.
   - Then the velocity constraints of decision 2. The velocity along the segment relative to the parent is removed.
     When the bend limit acted, the limit's wall moves with the parent particle's velocity and with the turning rate of
     the parent's frame. The first joint's frame turns with the anchor's whole rotation over the step relative to the
     carried frame, twist included. Each later frame keeps the twist about its parent segment and adds that segment's
     swing rate from its endpoint velocities, so an anchor twisting about the first segment still moves every wall
     below it. The child's outward velocity relative to that wall is removed, half from the child and half given to
     the parent particle, or all from the child when the parent is the anchored first joint. Capsule contact
     projects the child speed, parent speed and surface speed onto the contact normal's direction in the segment's
     tangent plane. At fraction `s`, with parent speed `b` and surface speed `w`, the solver raises the child's speed
     along that direction to at most `min((w - (1 - s) * b) / s, w)`. It skips contacts too near the parent to divide
     safely. At the tip, the target is the surface speed. At interior contacts the cap keeps repeated pushes from
     accumulating to a child speed greater than the surface speed and prevents the parent's motion from flicking
     the child. Any remaining approach and pushes along the segment are handled by positions. The surface moves
     with its axis and with radius changes along the contact normal. Capsule motion, the anchor's turn
     and the first joint's velocity are measured in the chain's carried frame, where its velocities live, so a capsule
     or a limit that moves with the wearer never pushes a chain that trails nothing.
   - The joint's rotation is the swing that turns its rest direction onto the simulated one, applied to its unswung
     rotation, which keeps the animation's twist.
4. **Links.** Every linked pair reads the same snapshot, and each particle takes the mean of its pairs' midpoint
   corrections, so a particle in several links never takes a stiffer step and the update stays stable. Then a second
   root to tip pass without the stiffness spring restores the bend limit, collisions and lengths.
5. **Width bound.** Chains joined by links of a part that states `linkStretch` run that second pass together, depth by
   depth. Each depth's particles are placed from their final parents. Bound pairs at that depth then move both
   particles on their own length spheres toward the band. The first sweep uses the position solve's rounding
   tolerance. Later sweeps allow a 0.5 mm residual because a shared particle can reopen a neighbour's gap.
   Correction starts at zero at each margin and reaches the authored band at twice the margin. Each correction
   targets inside the margin to avoid chasing roundoff at its edge. Alternating sweeps stop when every pair fits
   within the current margin or after eight sweeps. The bend limit and capsules are applied again to each moved
   particle because they outrank the bound.
   The impulse that stops escaping width velocity increases continuously across the 0.5 mm approach margin inside
   the band, reaching a full stop at the edge within rounding tolerance. This avoids switching between free
   separation and a full stop while springs and gravity act on a cape at rest. The width velocity constraint runs
   before the length, limit and contact velocity constraints, which keep the last word. Tip pairs are solved
   together after all joint depths, because a short chain's tip can be linked to a longer chain's. A pair whose
   segments point straight apart turns both toward one shared perpendicular, since its tangents give no leverage.
   Coincident endpoints turn to opposite sides of it, so a gap can open. A bound applied after the whole pass
   has to drag the joints below a corrected particle without their speed, and the next step pushes them back out.
   Placing a depth only from final parents keeps lengths exact and drags nothing.

Corrections never write velocity. A chain whose one movable joint hangs from a still anchor never gains kinetic
energy from its limit, static contacts or length. A tip released from contact at its endpoint starts from rest. The
anchored first joint and segment lengths always hold. A collider outranks the bend limit, because a pass through
looks worse than an extra bend. Contact solves the capsule locally as a sphere at the closest point of its axis, up to
eight passes, in double precision. Use the axis point closest to the segment being placed. The contact correction
allows a small margin for rounding the direction and endpoint to float, because that endpoint becomes the next
segment's parent. Each chain can skip capsules farther from its first joint than the chain's reach plus both radii,
the contact tolerance and a rounding margin far above double precision error. A correction that
would leave that reach replays the joint against every capsule, so skipping preserves the result. A standalone
simulation with more than 64 capsules, or a reach too large to bound safely, skips the per-chain reach test. The
next filter compares each placed parent's reach on its length sphere, including the chain radius and contact
tolerance, with each capsule's swept bounding sphere in float. The swept sphere encloses both capsule poses so
a crossing can still be tested. Each placement reuses the selected candidates for its position passes and
velocity constraint. Segment lengths come from this step's rest hang, so a scaled wearer keeps its proportions.

**Known limits.** The segment solve clears the line between joints and on to the virtual tip. Opening-side
contact follows a straight relative path between steps. A collider can still leave a segment behind if it moves
farther than the segment can reach in one step. The shown shape is up to one step behind the latest solved shape
relative to its anchor. A collider moving fast relative to the anchor can overlap the shown chain by up to one
step of that motion while the stepped chain clears it. While a held chain blends back to the live one, the shown
chain can overlap a capsule for at most the blend time, while the live chain already clears it. When linked panels
sit on opposing sides of a capsule, contact can prevent them from reaching their width band. The conflicting
constraints can leave small movements even when the pose is fixed.

Each chain joint's result is kept relative to its anchor, and drawing composes it on the anchor's current world
transform. The shown result interpolates the last two steps' anchor relative frames at the frame time's place between
them, through the hierarchy. Each joint's swing from its unswung frame is slerped, and each child attaches to its
blended parent, with the first joint pinned to its anchor. At a grid sample the captured frames are copied. Between
samples, the solver's cached swings follow the current anchor when each sample-to-current transform is a rotation with
positive uniform scale and any translation, and the anchor and rest bases are well conditioned. A changing non-uniform
stretch or shear changes the endpoint bases differently. An ill-conditioned basis can lose a direction to float
rounding even under a rigid change. The display reads the endpoint matrices in either case. Anchor bases are checked
per sample and frame. The immutable rig bounds the conditioning of its rest locals together, because scale spread can
accumulate down a chain. When two endpoint swings are near a half turn apart, the display rereads that joint's
endpoints so both paths select the same arc. Interpolating each frame's translation on its own shortened a segment to
about 0.71 of its length halfway through a quarter turn in one step. A frame that ends before the next step shows that
interpolation, and a zero time frame keeps its shown frames, both composed on the current anchor, so the chain never
falls behind the body.

### Resets and limits

- **Reset.** The first step, `Reset()`, a wearer that moved farther than the teleport distance in one frame, and a
  frame time longer than the longest gap all put every chain at its rest hang with no velocity, so a respawn or a
  teleport is calm.
- **Hold.** A frame at `MotionLevel.Held` runs no steps. The chains blend from their last shown shape to the rest
  hang over the hold blend time and stay there. A later simulated step starts the live chain from the held shape with
  no velocity and blends the shown chain to it over the same time. Each blend continues from the last shown swing, so
  an anchor turning through a half turn during a blend never switches arcs, and neither switch pops. A blend rebuilds
  the chain through the hierarchy as the step interpolation does, so lengths hold mid blend. It carries the
  last shown swings with the same transform check, and copies the target when it finishes. With a hold blend time of
  0 a switch completes on its first frame, even one with no elapsed time.
- **Picking who swings.** `MotionLevels.Assign` sets the nearest wearers within a distance to simulated, up to a
  count, and the rest to held, writing into caller spans without allocating.

| Option | Default |
| --- | --- |
| Simulation step (`MaxStepSeconds`) | 1/120 s |
| Most steps per frame (`MaxStepsPerFrame`) | 8 |
| Teleport distance | 2 m |
| Longest gap | 0.5 s |
| Hold blend | 0.25 s |
| World gravity | (0, -9.81, 0) |

These are motion options with defaults in the API, not in a file, because they describe the game's world and frame
budget, not a piece. A step is at most 1/30 s and a frame at most 64 steps, because a presentation solver needs
nothing coarser and the link update is proven stable within those bounds. A step is at least 0.0001 s, because
velocities divide position changes by the step and a far shorter one overflows them. `MaxStepSeconds` names the
fixed step. The name is older than the fixed grid, and a rename would break callers for no change in what they set.

## Format 4 and the body collider file

`AttachmentSetJson.MotionFormatVersion` is 4. Format 4 is format 3 with two optional members: `motion` on a skinned
part and `colliders` at the root. `Write` chooses the lowest format that expresses the set, so these additions
alone write format 4 and formats 1 to 3 keep their bytes and `SemanticSha256`. A set with motion or colliders
needs a layer. A part with chains can also [lift over lower layers](ATTACHMENT-LIFT-DESIGN.md#chained-parts)
from format 5 onward.

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

`motion.kind` is data, and `chains` is the one kind. An unknown kind refuses with a typed reason, so a reader that
predates cloth refuses a cloth piece instead of drawing it wrong.

Format 6 is format 5 with one optional member, `linkStretch` in a part's chain motion, beside `links`:

```json
"motion": {
  "kind": "chains",
  "chains": [ ... ],
  "links": [[0, 1], [1, 2]],
  "linkStretch": 0.15
}
```

`Write` uses format 6 for sets with `linkStretch` and no footprint reference. Formats 1 to 5 keep their bytes
and `SemanticSha256`. A set with a footprint reference writes format 7 and retains its `linkStretch` if present.
Move all readers to 24.8.0 before publishing format 6. Earlier readers refuse it so the bound cannot be silently
lost. A bound must be finite, greater than zero and at most one. The part needs at least one link for the bound
to apply.

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
  `ChainMotionDefinition.LinkStretch` is init only and null for a part without a width bound. `ChainLink.Stretch` is
  init only and carries the bound per link, because one wearer's rig combines chains from several parts.
- `ColliderDefinition`, `AttachmentSetDefinition.Colliders` and a constructor overload that takes them.
- `BodyColliderSet`, `BodyCollidersJson`, `BodyCollidersRefusal` and `BodyCollidersDataException`.
- `ChainRig` (immutable rest data for one wearer's chains), `ChainSimulation` (one wearer's particles, allocated once)
  and `ChainSimulationOptions`. Plain numbers go in and joint frames come out, so a tool can drive it from exported
  poses.
- `MotionLevel` (`Simulated`, `Held`) and `MotionLevels.Assign`.

`KhaozEngine.Attachments.Render3D`:

- `AttachmentSetCache.GetBodyColliders(key, expectedSha256, bodyRigIdentity, hostMesh)` returns `LoadedBodyColliders`.
- `BoundOutfit.CreateMotion` and `BoundAttachmentSet.CreateMotion` take the body colliders, or none, and options, and
  return a `WornMotion`. They require collider data loaded for the binding's skeleton instance. With no body
  colliders the chains collide with worn set colliders only.
- `WornMotion.Step(skeleton, palette, model, deltaSeconds, level)`, `Reset()`, and `Tune(wornIndex, partId, chain, settings)`
  for a live tuner.
- Overloads of `BoundOutfit.WriteDraws` and `QueueOutline`, and of `BoundAttachmentSet.WriteDraws`, `Queue` and
  `QueueOutline`, that take the motion. A motion made for another outfit or set refuses with a typed reason.

`KhaozEngine.Render3D`:

- `GltfLoader.LoadSkinnedWithMaterial(ReadOnlyMemory<byte>, string)` and
  `GltfLoader.LoadAnimations(ReadOnlyMemory<byte>, string)`.

## Binding and drawing

- **Binding.** `Bind` resolves garment chain joints and host anchors and checks each chain's shape and inverse
  binds. `CreateMotion` combines the visible chains across worn sets, resolves colliders against the host skin and
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
  sphere. Loading a set grows each chained mesh's bounds to contain a sphere at each chain's first joint in mesh rest
  space. The radius sums the rest segments, the tip segment and the farthest weighted vertex from its chain joint.
  This growth covers the chain's swing around the anchor. A displaced view also keeps its margin for lift.
  An anchor animated far from rest by translation or scale can take the whole chain outside the culling bounds.
  Ordinary skinned meshes share this limit
  ([#1492](https://github.com/APKiwiOrg/KhaozEngine/issues/1492) and
  [#1538](https://github.com/APKiwiOrg/KhaozEngine/issues/1538)).
  The shipped upload leases use an internal `Scene3D` seam that grows the source and its views together. A custom
  lease keeps its own culling policy.
- **Hidden parts.** A part the outfit hides entirely is not stepped.
- **Allocation.** `CreateMotion` allocates everything. `Step`, `WriteDraws`, `Queue` and `QueueOutline` allocate
  nothing once warm.

## Coverage claims and swinging triangles

An outfit's hidden set is fixed when the outfit is bound. A cloak that claimed the back would hide the shirt's back,
then swing away and show a hole. A claim may therefore rest only on triangles that do not swing:
`CoverageClaimCheck` ignores every piece triangle with a vertex weighted to a chain joint, and a fitting step claims
only what the fixed part of a piece covers, such as a cloak's collar.

The fitting tool derives a per-vertex mask from the same skinned GLB it imports and the part's chain joint names.
Both use flattened mesh, primitive and vertex order so each flag describes the position at the same index. A piece
with motion passes one mesh per definition part in any order and sets `CoverageClaimMesh.PartId` on each mesh.
The check verifies that each part is represented exactly once, so a reordered mesh keeps its own swing evidence.
A missing or empty mask for a part with motion refuses with `SwingMaskMissing`, including its part ID and mesh key.
The [coverage claim check reference](../../KhaozEngine.Attachments/README.md#coverage-claim-check) describes the API.

## Room for cloth

- A part's movement kind is data, `motion.kind`. Cloth adds a kind and its settings in a later format.
- Colliders, the body collider file, region tags, layers and fitting do not change when cloth arrives.
- `WornMotion` is the one place that turns a movement kind into what a draw needs. Chains give joint matrices for the
  garment palette. Cloth will give a per wearer deformed mesh, which the same draw overloads substitute for the
  part's shared mesh.
- The kinds share the fixed step loop, collider posing and the reset and hold rules, and a cloth solver reuses the
  particles, the velocity rule, distance constraints, the width bound's strain limit and capsule collision.

Live reshape ([#1869](https://github.com/APKiwiOrg/KhaozEngine/issues/1869)) is untouched. Chains bind per fitted
body, like everything else in a piece.

## Cost

The wearables limits are 32 wearers, at most 2 ms added CPU preparation, no managed allocation once warm and at most
1.3 ms added GPU time at 32 characters on a T4.

- CPU: a cloak of five chains of five joints and four strap tails of three joints has 37 movable points, including
  the tips. Each step runs a root-to-tip solve against capsules within each chain's reach. When link springs move
  particles, chains outside bounded groups get a second pass. Bounded groups always get a second pass by depth.
  Each depth and the tip group run up to eight position sweeps over their bounded pairs, recheck contact and bends
  for moved points, then apply the velocity constraints. A reach test selects candidates once per chain per pass,
  with a full capsule replay if a contact correction leaves the conservative reach. The cull and both contact
  passes share the capsule axes and swept bounds prepared once per step. Each placement runs the float filter
  before any double precision contact query. Its preallocated candidate storage holds a row per chain so the
  depth pass can finish each chain's velocity constraints after solving width. A segment reuses its opening residual
  for a capsule throughout the placement and its current offset until a correction changes its direction.
  Constraint setup extracts the anchor rotation and skips the unused stretch.
  At the default 120 Hz simulation rate, a 144 Hz frame averages 0.83 steps and a 60 Hz frame averages two.
  A frame between grid steps rebuilds each shown chain from the last two steps. When both sample anchors carry
  by a positive similarity and the bases are well conditioned, the display transports and slerps cached swings
  and composes each joint on its blended parent. It still factors a joint's endpoint matrices near a half turn
  between the swings, where rounding can choose the opposite arc. The affine fallback factors every joint
  when the anchor changes stretch or shear or the chain's combined basis condition exceeds the cache bound.
- GPU: no shader, pass or draw count changes. A chained part adds palette entries within the 128 joint limit.
- Allocation is tested per call, as outfit draws are.

`SwingCostDiagnostics` reports CPU preparation for 32 wearers with independent outfits and motion. Each wearer
uses 12 body capsules and a cloak whose neighbouring chains have a 25% width bound. It measures
`WornMotion.Step` and `BoundOutfit.WriteDraws` for every worn set, excluding host animation and scene submission.
After 120 warm frames per rate it reports the mean, p50, p95 and maximum frame cost in milliseconds, total managed
bytes on the preparation thread and the runner environment in one JSON line. Each rate samples 600 frames at
a simulated frame rate of 144 Hz or 60 Hz. Each wearer's motion step is immediately followed by its draws. The
diagnostic sums the spans around those calls and includes their timestamps in the frame total. A third sample
uses the same 32 wearers held at 144 Hz to measure preparation with no physics steps. The held
sample shows steady held presentation and draws. Timing is report only and excluded from default CI.

Run the full measurement on a GitHub-hosted runner after approval. Only that run supplies timing evidence.

```sh
KE_GPU_TESTS=0 dotnet test KhaozEngine.Render.Tests -c Release --filter 'Category=Diagnostic&FullyQualifiedName~SwingCostDiagnostics' --logger 'console;verbosity=detailed'
```

`KE_SWING_COST_SMOKE_FRAMES` sets both frame counts to a value from 1 through 119 for a shortened harness check.

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
  bad tip, a bad link, a bad collider, too many chains, joints, links or colliders, motion or colliders without a
  layer, and the three width bound reasons listed below.
- `BodyCollidersRefusal`, a new family: an unsupported format version, an unknown or duplicate property, a bad rig
  identity, an empty joint name, a bad radius or point and too many colliders.
- `AttachmentBindingRefusal`: a chain joint missing from the garment skin, a chain joint named like a host joint, a
  chain that is not a parent line, an anchor that is not a host skin joint, a chain bind mismatch, an unresolved
  collider joint, body colliders for another rig identity or skeleton instance, and too many colliders on one wearer.
- `AttachmentSubmissionRefusal`: a motion made for another outfit or set.
- `CoverageClaimRefusal`: an unstated, unknown or repeated mesh part ID, a definition part without a mesh, or a
  missing or empty swing mask for a part that declares motion.
- `GltfImportRefusal`: an image named by a relative path in a byte import.

## Tests

All headless, on synthetic skeletons built in code.

- **Solver.** A chain released level falls and settles below its anchor. It settles to the same pose at 30, 60, 144
  and 240 Hz within 1 cm at the tip, and its first swing peaks within 10 percent across those rates. Random frame
  times from 1 ms to 500 ms, hitches included, stay finite. A 10 m teleport shows no whip. Whole segments clear
  thin and moving capsules, including long capsule axes. A soft chain settles on the side of a capsule it fell onto.
  A capsule that crosses a segment within one step pushes it ahead. Bends stay within the limit except where
  capsule clearance takes precedence. Disabling the placement filter preserves every joint frame bit, including
  for very long axes. A diagnostic counts segment-axis queries per wearer step with the filter enabled and disabled.
  Trail 0 keeps the chain's shape in the wearer's space while the wearer moves steadily, and trail 1 lags it. A hold and a resume move no joint further in a frame than the blend
  allows. Linked chains keep their spacing when a collider pushes one. Nothing allocates once warm.
- **Velocity rule.** A one joint chain on a still anchor, with no spring or gravity, sent into its limit and a capsule
  never gains kinetic energy, including with contact in the middle of the segment. A chain released from tip
  contact moves exactly as far as gravity alone moves it from rest. A cape of five linked chains with tight limits and stiff springs, on an anchor that runs with a
  sway and stops, has a lower peak tip speed after the stop than while running, at 30, 60, 144 and 240 Hz. A capsule
  that strikes a limp chain and stops never throws it faster than the capsule moved. A pure anchor twist moves a
  downstream limit's wall, and a growing sphere passes its surface speed to a tip.
- **Stepping.** A chain driven by a slow anchor stays within a tight bound of its fixed rate path under uneven frame
  times, and a frozen pose in contact stops moving. The first joint sits on the current anchor at every frame. A frame
  equals the grid steps inside it. A frame past the step budget keeps a capsule's real contact speed. The shown chain
  keeps its segment lengths through a large turn between steps and when a resume finishes between steps. A zero
  length hold finishes on a zero time frame, and a step below the minimum refuses.
- **Width bound.** Linked chains with `linkStretch` keep their spacing inside the bound under a driven anchor, and the
  same chains without it leave it. The second pass keeps them clear of capsules and within their limits. A soft
  bounded cape at rest against a frozen capsule stops moving at fixed and uneven frame pacing, including at low
  damping with the bend limit disabled. Coincident tips separate into their band.
- **Formats.** Formats 4 and 6 and the body collider file round trip. Each refusal sits beside a passing control that
  differs only in the broken rule. Format 1 to 5 bytes and hashes are unchanged.
- **Binding and draws.** A chained garment binds and draws. Each binding refusal is reached. Without a motion, chain
  joints sit at their rest hang. After steps the palette's chain joints move and its host joints do not. Draws and
  outlines read the same palette. A motion from another outfit refuses. Grown bounds contain the reach. Steps and
  draws allocate nothing once warm.
- **Coverage claim.** A claim that rests only on swinging triangles is reported uncovered. A part with motion
  and no vertex mask is refused.
- **Byte loading.** The byte entries match the path entries for a GLB with embedded images and refuse a relative path
  image.

## Not supported

The chain solver has no cloth, live reshape or wind input. Links constrain spacing within a part, with no
chain-to-chain contact. `WornMotion` takes only body and
worn set capsules and has no ground query. Rigid attachments bind to host joints, and cannot bind to chain joints
or follow a bending surface. Skin a hanging rigid piece to a one-joint chain to give it swing.

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

- Wearer binding. `Bind` validates chains before creating submission scratch, because the ordinary draw methods
  need the rest hang before a wearer creates motion. The cache already owns the uploads at this point.
  A chained bind reads the hierarchy and rest locals from the verified bytes. The cache keeps that metadata only
  after every part binds, so a refusal creates no cache entries or scene resources.
  `Tune(wornIndex, partId, chain, settings)` includes the worn position because different sets can reuse a part ID.
  The bare set uses index zero. `Reset` exposes the rest hang immediately and lets the solver discard velocity on
  its next valid step.
- Body collider binding. `GetBodyColliders` takes the host mesh alongside the caller's body rig identity. The cache
  shares verified file data across bodies and keeps a loaded view per skeleton instance. A bare set and an outfit
  both check that instance, and the outfit also checks its region map's rig identity. An outfit borrows inverse binds
  from its first set bound with the host mesh. A map-only binding still draws rigid parts. Motion with colliders needs
  at least one set bound with a host mesh, since a joint map has no inverse binds. The bound capsule keeps its
  rest-model data and the inverse bind so the radius can use a single scale from their composed rest-to-world
  transform.
- Coverage claim evidence. Init-only `CoverageClaimMesh.PartId` identifies the definition part because list
  position can pair a chained part with another mesh's mask. A set declaring motion supplies one mesh per part
  in any order. The check refuses an unstated, unknown or repeated ID, a missing part mesh or a missing mask
  on the mesh for a moving part. Zero is the default, and the check ignores IDs on pieces without motion.

- Motion submission. The public overloads keep the set and outfit draw signatures, with a motion argument before
  the skeleton. `MotionMismatch` uses the existing submission argument exception and identifies the motion
  parameter. The cull sphere starts at the first simulated joint. The host anchor carries that joint's offset,
  which is outside the simulated chain length. The cache measures reach from the uploaded garment's inverse binds
  and vertex weights. Binding still owns typed chain validation.

- Width bound. `AttachmentSetJson.LinkStretchFormatVersion` is 6. The nullable, init-only `float` properties
  `ChainMotionDefinition.LinkStretch` and `ChainLink.Stretch` preserve the existing constructors and deconstruction.
  A set supplies its bound through the motion definition. The renderer's existing `CreateMotion` methods copy
  it to each rig link in that part.
  A directly constructed `ChainRig` takes per-link bounds and rejects invalid values with `ArgumentException`.
  `AttachmentSetRefusal` appends `LinkStretchOutOfRange` and `LinkStretchWithoutLinks`, both with `PartId`,
  `PartIndex`, `SettingName` and `JsonPath`. It also appends `ChainLinkStretchInDefinition` for a rig bound
  embedded in a set definition's link, with `PartId`, `PartIndex`, `LinkIndex` and `JsonPath`.
- Fixed step. `ChainSimulationOptions.MaxStepSeconds` accepts 0.0001 s through 1/30 s. A simulation built with any
  other value, NaN included, refuses with `ArgumentOutOfRangeException` naming `MaxStepSeconds`, as a step above
  1/30 s did before.
