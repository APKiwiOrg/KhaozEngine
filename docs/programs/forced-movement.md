# Forced movement

## Goal

A server moves an actor toward a target actor along walkable ground, around obstacles, at a set speed, and
stops within reach. Client prediction presents the move smoothly and a client never moves itself. The first
consumer is a player charge used at range. Knockback and leaps reuse the same machinery later.

## Links

- Roadmap: https://github.com/APKiwiOrg/KhaozEngine/issues/1658
- Unbuilt design: [forced movement design](forced-movement-design.md)
- Docs to change: [Locomotion](../../KhaozEngine.Locomotion/README.md),
  [Movement](../../KhaozEngine.Movement/README.md), [NetWorld](../../KhaozEngine.NetWorld/README.md),
  [consumer contract](../USING-KHAOZENGINE.md)
- Consumer adoption: https://github.com/APKiwiOrg/Grimhollow/issues/609

## Milestones

| Milestone | Status | Issue | Owner gate |
| --- | --- | --- | --- |
| M1 Pathed charge (ground commitment, server steering, pursuit planning) | waiting on the path follower work | https://github.com/APKiwiOrg/KhaozEngine/issues/1658 | FM-D8 gate met, then the owner confirms the open questions below |
| M2 Release and consumer note | waiting on M1 | https://github.com/APKiwiOrg/KhaozEngine/issues/1658 | Owner approves the exact tag |

## Decisions in force

- FM-D1: The charge extends `MovementCommitment` with a ground travel kind, so it rides the existing
  replication, prediction replay and movement anomaly exemption. A client never moves itself.
- FM-D2: A charge triggers only when the target is within a maximum straight-line range, supplied per
  ability by the game.
- FM-D3: The charge follows a walkable path around obstacles. Admission requires a complete, walk-only
  path (no hop or drop links) whose length is within a path-length cap. No path, a partial path or a path
  over the cap refuses the charge, which is what refuses a detour around a cliff.
- FM-D4: While the charge runs, the server re-plans as the target moves. It ends with arrival within the
  stop distance, and cancels when the target is gone, no path remains, the remaining path exceeds the cap,
  progress stays blocked for several consecutive ticks, or the timeout passes. The game applies its own
  strike on arrival.
- FM-D5: Each tick the server sends the direction toward the next waypoint and the remaining path length.
  Prediction replays them and stops itself when the length runs out, so the client never overshoots. A
  corner costs a short straight run then a smoothed correction.
- FM-D6: Pathing is a new type in KhaozEngine.Movement built on the existing planners. NetWorld gains
  steering and end calls and takes no Navigation dependency. `PathFollower` and `MoveToRange` are not
  edited for this.
- FM-D7: The charge moves at its own speed and ignores the speed scale. The game refuses a charge while the
  actor is rooted.
- FM-D8: Work starts only after the path follower work lands on main, the branches
  `feature/contact-engine-2120` (https://github.com/APKiwiOrg/KhaozEngine/issues/1645) and
  `fix/contact-follow-moving-goal` (https://github.com/APKiwiOrg/KhaozEngine/issues/1646).
- FM-D9: Knockback is the same ground travel with a fixed direction and distance. Leaps already use the
  ballistic commitment.

## Open questions and owner gates

- Owner: is the path-length cap a separate value per ability or derived from the trigger range?
- Owner: does the finer replicated speed scale (https://github.com/APKiwiOrg/KhaozEngine/issues/1652)
  ride the same wire generation bump? It changes a public field type, so the release becomes a major.
- Design call at M1 start: whether the engine owns the per-tick pursuit steering for players or the game
  drives it from its tick hook through the NetWorld steering call.

## Next step

Confirm FM-D8 is met on current main. Then re-read the merged navigation and movement code, settle the
open questions with the owner, write the M1 plan from the design file and build it test first.
