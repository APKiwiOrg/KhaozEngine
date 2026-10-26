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
| M2 Underwater sunlight for submerged receivers | on main as 21.15.0, release pending | https://github.com/APKiwiOrg/KhaozEngine/issues/1588 | Owner approves the release tag |
| M3 Underwater caustics | after the M2 release, design required | https://github.com/APKiwiOrg/KhaozEngine/issues/1604 | Owner reviews its design |
| M4 Native R8b optical adapter | waiting on the R8a native renderer | https://github.com/APKiwiOrg/KhaozEngine/issues/1300 | None beyond the normal GPU gate |
| Adoption Grimhollow floater shadows and authored beds | waiting on the M2 release | https://github.com/APKiwiOrg/Grimhollow/issues/644 | Grimhollow owner |

## Decisions in force

- WO-D6: Caustics are their own milestone after M2 and read M2's water volume records.
- WO-D7: Public fixtures are synthetic. No Grimhollow image, model or source enters this repository.
- WO-D8: The release carrying M2 is sent to the Grimhollow coordinator with its tag and adoption notes.
  Grimhollow keeps floaters without a sun shadow until then.

## Open questions and owner gates

- The owner approves the exact M2 release tag.
- Any native performance run for M2 needs the owner's separate approval naming its workload and cap. The
  workloads it would measure are https://github.com/APKiwiOrg/KhaozEngine/issues/1602.
- M4 starts only when the R8a native renderer prepares and submits bounded water on main.

## Next step

The owner approves the exact 21.15.0 tag and decides whether the M2 performance run waits for #1602. Then
release it, send the Grimhollow coordinator the tag and adoption notes for
https://github.com/APKiwiOrg/Grimhollow/issues/644, and write the M3 caustics
design.
