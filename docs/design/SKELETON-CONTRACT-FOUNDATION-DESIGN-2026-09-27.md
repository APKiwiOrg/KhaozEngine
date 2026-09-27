# Skinned body foundation

## Status

In progress under [#1182](https://github.com/APKiwiOrg/KhaozEngine/issues/1182). `SkeletonContract`,
`ContractJointMap`, `ClipRefusals`, `BoneMask.ForJoints`, `SkinnedGrounding` and `MomentSchedule` are in.
The round ships as one minor release.

## Purpose

Grimhollow's skinned player carries machinery that checks a loaded skeleton, finds its base frames,
refuses misleading clips, builds joint masks, grounds the deformed skin and schedules idle moments.
All of it is written against the humanoid's own joint table. Grimhollow's next skinned body is a
four-legged cow, and every quadruped after it needs the same machinery over a different table.

The engine takes the part that is stable, proven on the player and indifferent to body shape, and
makes it take a contract as input. A game brings a table of joints and gets the checks and frames the
player already relies on. Nothing here knows what a leg, a head or a tail is.

## The pieces

Every piece lives under `KhaozEngine.Render3D/Animation`, in the `KhaozEngine.Render3D` namespace
beside `Skeleton` and `BoneMask`. Each is pure, headless and free of a graphics device.

| Piece | What it is | Lifted from |
| --- | --- | --- |
| `SkeletonContract` | A table of joints: name, parent, deforms. | `HumanoidSkeletonContract`'s shape |
| `ContractJointMap` | A skeleton checked against a contract, with each joint's base frames. | `HumanoidJointMap` |
| `ClipRefusals` | Load-time clip refusals, each naming the clip, the rule and the joint. | the game's `ClipRefusals` and `LocomotionClips.Unkeyed` |
| `BoneMask.ForJoints` | A mask of weight one on named joints, refusing a missing name. | `LocomotionClips.Mask` |
| `SkinnedGrounding.MinimumY` | The deformed skin's lowest point for a palette and a model, with no allocation. | `SkinnedHumanoidBody.MinimumY` |
| `MomentSchedule` | The idle-moment schedule over a weighted table of moment families. | `IdleTurns` |

## SkeletonContract

The table is validated once. It is not empty, every name is unique, exactly one joint has no parent
and it comes first, and every other joint's parent is declared before it. One forward pass over the
joints therefore meets each parent before its children, the order a `Skeleton` keeps its nodes in. A
refusal is an `ArgumentException` naming the offending joint. The table is copied, so a caller may
reuse its list.

## ContractJointMap

The map refuses a skeleton with more nodes than `SkinningMath.MaxBonesPerDraw`, two nodes of one
name, and a contract joint that is missing, misparented or, other than the root, outside the skin.
The misparent message names both the actual and the expected parent. Unnamed nodes are left alone,
as `Skeleton` leaves them, so helper nodes a rig exports without names cost nothing.

The base is the body's zero. It is an optional one-key stance clip, sampled once, or the bind rest
without one. The stance must carry the caller's clip name (`stance` by default), key each track
exactly once, animate contract joints only and carry no scale track. A clip that breaks any of those
is refused before it is sampled, because every pose is measured from the zero and a misleading one
skews all of them.

From the base the map derives each contract joint's model frame, its parent-base inverse, and a body
alignment that takes the joint's base orientation back out so a rigid piece authored in the body's
axes sits at the joint with those axes. `SkinAtBase` deforms a skin to the base on the CPU, the same
deform the shader applies. The frames exist for contract joints only. `Node` resolves any named node,
so the frame accessors refuse a node outside the contract, naming it, rather than hand back a zero
matrix that would collapse an attached piece to the origin.

The arithmetic runs in the same order as Grimhollow's `HumanoidJointMap`. The humanoid can then wrap
this map and keep its palettes bit for bit. The humanoid's hip pivot is a humanoid fact and stays in
the game.

## The other four

`ClipRefusals` returns a message for two clips of one name, a clip of no length, a required joint's
rotation or a named joint's translation left unkeyed, a channel the stance keys left unkeyed on the
nodes a filter takes, and the first `ClipHygiene` finding. The messages keep Grimhollow's wording,
with the caller's clip family and rule in place of the player's own. `Unkeyed` walks the turned joints
in order, each rotation and then its translation when the moved list names it too, then the joints
only the moved list names. A loader that lists its joints as the player does therefore meets the
player's first refusal. The humanoid clause of the player's message, which names its first contract,
stays in the game. A joint name the skeleton lacks is the caller's error and throws.

`BoneMask.ForJoints` builds the named-group masks a layered animator composites through. It weighs
the named joints only, never their descendants, and refuses a name the skeleton does not carry.

`SkinnedGrounding.MinimumY` finds where a posed skin meets the floor without a per-frame allocation.
The caller lends one scratch column per bone. A vertex whose weights total under the threshold
`SkinningMath.BlendSkinMatrix` and the skinned shader share draws through the model alone. An empty
skin returns positive infinity.

`MomentSchedule` takes a slot length, quiet ends, an empty share, an empty-run cap and a salt, over a
weighted table of moment families that are single or mirrored left and right. It is pure per body id
and clock. Its hash, its empty draw, its family draw (bits 32 to 47), its side bit (31) and its start
offset (the low 31 bits) are exactly those of `IdleTurns`, so salt 0 over a glance and turn table
reproduces the player's schedule to the bit. The salt is XORed into the seed before the first finalizer,
so salt 0 leaves the seed alone and salt s at seed n is salt 0 at seed n XOR s. One body draws unrelated
schedules under two salts, but across a population a salt only permutes which seed gets which schedule.
The family draw compares against each family's running weight over the table's total, not a running sum
of divided shares. Where the running sums and the total are exact in binary, as 0.75 and 1 or 3 and 4
are, each cut is the exact share. Otherwise rounding can move a cut by one draw in 65536. The last family
takes any draw rounding leaves over.

The table is copied at construction and `At` allocates nothing. The constructor refuses an empty table, a
weight that is not finite and above zero, weights whose total is not finite, and a length that is not
finite and above zero or does not fit between a slot's quiet ends. It refuses a slot length that is not
finite and above zero, a quiet end that is not finite and at least zero, an empty share that is not
finite, below 0 or at least 1, and a negative cap. The none moment, family -1, is `Moment.None`, and it
is the default `Moment`. The struct keeps the family one up, so a value type built without a moment
carries none, as a default `IdleTurn` does.

## What stays in games

Each body's contract table, its layer order, its motion inputs, its clip names and its joint groups
stay game code. So do the tuning and every clip. These change with a body's art and feel, and a
second body shares none of them with the first. A full engine capability that also owned the layer
stack was weighed and rejected for that reason. The layer stack is where the bodies differ.

## Release

The round ships as one minor release. The tag waits for the owner's authorization. Grimhollow adopts
the pin, moves its humanoid onto these types and proves with a probe that the player's palettes and
turn schedule hash identically before and after.

## Tests

Tests build skeletons in code and synthesize their clips. The joint map fixture keeps its skin bones
in an order unlike its node order, turns some rest frames, and carries a named node outside the
contract and the skin. No authored file or graphics device participates.
