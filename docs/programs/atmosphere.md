# Physical atmosphere and night sky

## Goal

Deliver a physical sky and distance fog, then atmosphere-lit volumetric clouds, cloud shadows and stars
under the atmosphere. Release and adopt each engine phase in Grimhollow before advancing. Aerial
perspective and eye adaptation remain separately gated companions.

## Links

- Roadmap: https://github.com/APKiwiOrg/KhaozEngine/issues/1585
- Docs changed: [Render3D](../../KhaozEngine.Render3D/README.md),
  [consumer contract](../USING-KHAOZENGINE.md), [render pipeline](../RENDER-PIPELINE.md).
- Design rationale: [physical atmosphere](../design/PHYSICAL-ATMOSPHERE-DESIGN-2026-10-12.md).
- Consumer adoption: https://github.com/APKiwiOrg/Grimhollow/issues/490

## Milestones

| Milestone | Status | Issue | Owner gate |
| --- | --- | --- | --- |
| M1 Physical sky, derived lighting, water reflection and analytic fog | Done | https://github.com/APKiwiOrg/KhaozEngine/issues/1366 | Confirm consumer adoption before M2 |
| M2 Volumetric clouds | Blocked | https://github.com/APKiwiOrg/KhaozEngine/issues/1367 | Owner reviews design before planning |
| M3 Cloud shadows | Blocked | https://github.com/APKiwiOrg/KhaozEngine/issues/1368 | Owner reviews design and preceding adoption |
| M4 Stars under the atmosphere | Planned | https://github.com/APKiwiOrg/KhaozEngine/issues/1369 | Owner reviews design and preceding adoption |
| Companion Aerial-perspective volume | Planned | https://github.com/APKiwiOrg/KhaozEngine/issues/1364 | Consumer need or cloud dependency, then own design |
| Companion Eye adaptation | Planned | https://github.com/APKiwiOrg/KhaozEngine/issues/1365 | Consumer need for physical night values, then own design |

## Decisions in force

- ATM-D1: Sky, fog, clouds and stars belong to the engine. Server-held weather is consumer work,
  using the engine's coverage and wind inputs.
- ATM-D2: Clouds use the owner-selected volumetric look rather than a painted layer or low-poly puffs.
- ATM-D3: Physical scattering leads the look to keep physically based lighting open. Art overrides
  preserve a game's mood without replacing the physical model.
- ATM-D5: Each engine phase is released and adopted by Grimhollow before the next phase proceeds.
  Later phases need their own owner-reviewed design before planning.
- ATM-D6: The aerial-perspective volume is an additive upgrade behind the fog interface, not a
  prerequisite silently added to the shipped phase.

## Open questions and owner gates

- The owner decides whether [calendar and lunar cycles](https://github.com/APKiwiOrg/KhaozEngine/issues/1412)
  join the stars milestone as a larger night sky program. Each game owns its world calendar.
- The cloud design must resolve shape noise and CPU versus GPU generation, terrain entering the
  cloud layer, and whether the aerial-perspective volume must land first. The owner approves it.
- Release timing and consumer look acceptance remain owner gates for every phase.

## Next step

Confirm Grimhollow's phase 1 adoption and look acceptance, then write the volumetric-cloud design for
owner review. Resolve its volume dependency before making an implementation plan.
