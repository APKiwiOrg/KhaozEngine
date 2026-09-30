# Character pose socket composition

This resolves [#96](https://github.com/APKiwiOrg/KhaozEngine/issues/96) by completing the
`BoneSocket` integration into the replicated-character presentation bridge.

## Problem

`CharacterPose` carries the model transform and the current joint-model bone palette, but a game
must index the palette and compose an equipment grip through the joint and model itself. The
GPU-free `BoneSocket.Compose` and `ComposeRigid` math already exists in `Render3D`. The gap is a
draw-ready pose API that supplies the correct palette entry and model transform together.

## Decision

Move the cohesive `CharacterPose` struct out of `ReplicatedCharacterAnimators.cs` into its own file,
then add `ComposeSocket(int boneIndex, in Matrix4x4 pieceLocal)` and
`ComposeRigidSocket(int boneIndex, in Matrix4x4 pieceLocal)`. Both validate the bone index and delegate
the transform math to `BoneSocket`. The rigid form is explicit because it removes joint scale and
shear, which equipment often wants, while the raw form preserves animated deformation. A consumer
resolves a named bone through `Skeleton` once, then uses its bone index on each transient pose.

| Option | Cohesion | API fit | Change safety | Total |
|---|---:|---:|---:|---:|
| Move the pose type and add methods | 9 | 9 | 7 | 25 |
| Add a separate extension helper | 7 | 8 | 9 | 24 |
| Grow the animator file | 3 | 8 | 2 | 13 |

The direct methods avoid a second helper surface that merely repackages `BoneSocket`. Moving the
pose struct gives it one clear file and reduces the animator file from 590 to 515 lines. That file
was already below the KESIZE cap, so no baseline update is needed. The methods read the palette
at call time and do not cache its transient array.

## Verification

Headless Game tests cover transform order, the rigid scale and shear choice, index validation, and
reading the current palette value after mutation. Existing `BoneSocketTests` cover the underlying
matrix behavior. Update the Game.Render3D package README and the consumer guide, then run Release
build, tests and guards.
This package change rides the next staged version. Before merge, pack only to a private feed. After
the validated commit is pushed on `main`, pack the shared feed from that checkout.
