# Per-body water optics

## Goal

Water bodies shade from their own authored bed instead of the first opaque surface behind them, at the
world edge as well as inside it, and submerged receivers are lit by sunlight that has passed through the
water. Native bounded water from MapDoc binds the same neutral optical resource. Grimhollow restores its
floater sun shadows and adopts authored beds once the engine work ships.

## Links

- Roadmap: https://github.com/APKiwiOrg/KhaozEngine/issues/1300
- Docs changed: [TileWorld.Render3D](../../KhaozEngine.TileWorld.Render3D/README.md#authored-optical-beds),
  [Render3D](../../KhaozEngine.Render3D/README.md), [render pipeline](../RENDER-PIPELINE.md),
  [consumer contract](../USING-KHAOZENGINE.md).
- Design rationale: [per-body water optics](../design/WATER-OBJECT-OPTICS-DESIGN-2026-10-24.md),
  [native handoff](../design/WATER-OPTICS-R8B-HANDOFF-2026-10-24.md).
- Consumer adoption: https://github.com/APKiwiOrg/Grimhollow/issues/644

## Milestones

| Milestone | Status | Issue | Owner gate |
| --- | --- | --- | --- |
| M1 Authored beds at the world manifest edge | shipped in v21.14.0 | https://github.com/APKiwiOrg/KhaozEngine/issues/1586 | none |
| M2 Underwater sunlight for submerged receivers | shipped in v21.15.0 | https://github.com/APKiwiOrg/KhaozEngine/issues/1588 | none |
| M3 Underwater caustics | design required | https://github.com/APKiwiOrg/KhaozEngine/issues/1604 | Owner reviews its design and approves the M2 performance run before it ships |
| M4 Native R8b optical adapter | waiting on the R8a native renderer | https://github.com/APKiwiOrg/KhaozEngine/issues/1300 | None beyond the normal GPU gate |
| Adoption Grimhollow floater shadows and authored beds | v21.15.0 handed to the Grimhollow coordinator | https://github.com/APKiwiOrg/Grimhollow/issues/644 | Grimhollow owner |

## Decisions in force

- WO-D6: Caustics are their own milestone after M2 and read M2's water volume records.
- WO-D7: Public fixtures are synthetic. No Grimhollow image, model or source enters this repository.
- WO-D10: M2 shipped on its cost analysis without a native performance run. It is off unless a body opts in,
  and an opted-in submerged pixel costs a fixed 16 shadow comparisons and a four-slice march. The native run
  with opted-in water follows https://github.com/APKiwiOrg/KhaozEngine/issues/1602 and must land before M3
  ships, because caustics add cost to the same pixels.

## Open questions and owner gates

- The M2 native performance run needs the owner's separate approval naming its workload and cap, once the
  workloads in https://github.com/APKiwiOrg/KhaozEngine/issues/1602 exist.
- The owner approves the M3 caustics design before implementation.
- M4 starts only when the R8a native renderer prepares and submits bounded water on main.

## Next step

Write the M3 caustics design for https://github.com/APKiwiOrg/KhaozEngine/issues/1604 and bring it to the
owner. Follow Grimhollow's adoption of v21.15.0 under https://github.com/APKiwiOrg/Grimhollow/issues/644.
