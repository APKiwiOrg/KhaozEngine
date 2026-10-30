# Forced movement

## Goal

Provide server-authoritative ground pursuit toward a target, with prediction replay, exact arrival
signals and fine replicated speed multipliers. The game owns ability content, targets and impact.
Knockback and leaps can reuse the movement primitives later.

## Links

- Roadmap: https://github.com/APKiwiOrg/KhaozEngine/issues/1658
- Bundled speed precision: https://github.com/APKiwiOrg/KhaozEngine/issues/1652
- Approved unbuilt direction: [Forced movement design](forced-movement-design.md)
- Delivered rig identity: https://github.com/APKiwiOrg/KhaozEngine/issues/1661 and
  [Attachments](../../KhaozEngine.Attachments/README.md#body-rig-identity)
- Docs to change: [Locomotion](../../KhaozEngine.Locomotion/README.md),
  [Movement](../../KhaozEngine.Movement/README.md), [Netcode](../../KhaozEngine.Netcode/README.md),
  [NetWorld](../../KhaozEngine.NetWorld/README.md), [consumer contract](../USING-KHAOZENGINE.md)
- Consumer adoption: https://github.com/APKiwiOrg/Grimhollow/issues/609

## Milestones

| Milestone | Status | Issue | Owner gate |
| --- | --- | --- | --- |
| M0 Body rig identity | Done | https://github.com/APKiwiOrg/KhaozEngine/issues/1661 | Released in v21.21.0, consumer adoption handed off |
| M1 Ground pursuit, control, replay and fine speed scale | Blocked | https://github.com/APKiwiOrg/KhaozEngine/issues/1658 and https://github.com/APKiwiOrg/KhaozEngine/issues/1652 | Design approved and implementation plan ready, FM-D8 still unmet |
| M2 Major release and consumer note | Planned | https://github.com/APKiwiOrg/KhaozEngine/issues/1658 | Owner approves the exact tag after implementation and verification |

## Decisions in force

- FM-D1: Extend MovementCommitment with a ground kind, retaining server authority, replication,
  reconciliation, pending-command replay, handoff and movement anomaly exemption.
- FM-D2: Admission requires the target inside a game-supplied maximum straight-line trigger range.
- FM-D3: Require a complete walk-only route. Partial, unreachable, hop, drop, unknown-transition and
  over-cap routes refuse. The path cap prevents excessive detours around cliffs.
- FM-D4: Replan for a moving target, corridor departure, exhausted route or invalid cached endpoint.
  Confirm arrival from current exact body reach. Target loss, route loss, excessive remaining path,
  sustained blocked progress and timeout cancel travel. The game owns the strike.
- FM-D5: Replicate direction and remaining horizontal travel budget. Prediction stops at zero budget
  and holds the commitment for authoritative steering/end. Zero budget alone does not signal arrival.
  Older corner steering may cause a short straight run followed by bounded correction.
- FM-D6: Pursuit is a new Movement type using guarded planners. NetWorld gains steering/end calls
  without Navigation or Movement dependencies. Do not edit PathFollower or MoveToRange for this work.
- FM-D7: Ground travel uses its own speed and ignores SpeedScale. The game refuses a new charge while
  rooted and owns any root/stun cancellation policy during travel.
- FM-D8: Implementation waits for the navigation batch on main, including
  https://github.com/APKiwiOrg/KhaozEngine/issues/1645 and
  https://github.com/APKiwiOrg/KhaozEngine/issues/1646. The named branches are
  feature/contact-engine-2120 and fix/contact-follow-moving-goal. Do not take over that batch implicitly.
- FM-D9: A later fixed-direction knockback can use ground travel and caller-owned completion. Leaps
  use the existing ballistic kind. No turnkey knockback or leap ability ships in M1.
- FM-D10: Every ability supplies its own MaximumPathLength independently of TriggerRange. Path
  budgets use horizontal XZ metres, while arrival uses exact three-dimensional body reach.
- FM-D11: Bundle #1652 into the same wire migration and a major engine release. SpeedScaleQ becomes
  short at quantum 1/1024. Retain the 8x maximum, exact default/root behavior and existing clamping policy.
- FM-D12: The game calls one engine pursuit object from its server tick, supplies actor/target state
  and passes results to queued NetWorld controls. The engine owns navigation and physical mechanics.
  There is no turnkey engine actor/ability manager.
- FM-D13: Emit authoritative Arrived immediately before optional recovery. Ended marks control
  release and preserves Arrived. Both events are exactly once. Existing ballistic Landed semantics stay.
- FM-D14: Speed widening needs an explicit historical payload rewrite, composed with the owner-timer
  split. Old speed q becomes Int16(q * 64). New ground state defaults to Ballistic and zero.

## Open questions and owner gates

- No architectural choice remains open. The owner approved the complete design and its technical
  defaults after a fresh builder check.
- FM-D8 remains the implementation gate. The Contact batch is held by its owning program and has not
  landed on main. Its staged version must be reconciled with current tags before landing.
- Re-read the merged navigation and protocol contracts before execution. A material change to the
  approved semantics returns to the owner rather than being decided silently.
- No forced-movement code, version bump or release tag is authorized by design approval alone.
  Implementation is authorized after FM-D8, exact-tag approval remains a later gate.

## Next step

The next engine chat refreshes main, verifies the #1645 and #1646 resolutions in shipped code and
re-reads the merged seams. The approved direction below and the preserved implementation plan are
ready. Use `dotfiles work path --key forced-movement` to locate the program work folder and resume
instructions. No implementation has started. Build test first after FM-D8, then perform one fresh
whole-branch review, reconcile and publish through the normal major-release ritual. Do not restart
the completed rig identity work or edit a consumer repository.
