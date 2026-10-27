# Per-body water optics

## Goal

A realistic river above and below the water. Water bodies shade from their own authored bed, submerged
receivers are lit by sunlight that has passed through the water, objects over water cast shadows that read
as attached to them, the bed is seen bent and absorbed through the surface, the scene reflects in the water,
rivers flow, floaters disturb the surface and caustics play on the bed. Native bounded water from MapDoc
binds the same neutral optical resource. Grimhollow adopts each milestone as it ships.

## Links

- Roadmap: https://github.com/APKiwiOrg/KhaozEngine/issues/1300
- Docs changed: [TileWorld.Render3D](../../KhaozEngine.TileWorld.Render3D/README.md#authored-optical-beds),
  [Render3D](../../KhaozEngine.Render3D/README.md), [render pipeline](../RENDER-PIPELINE.md),
  [consumer contract](../USING-KHAOZENGINE.md).
- Approved design of unbuilt milestones: [water optics design](water-optics-design.md)
- Design rationale: [per-body water optics](../design/WATER-OBJECT-OPTICS-DESIGN-2026-10-24.md),
  [native handoff](../design/WATER-OPTICS-R8B-HANDOFF-2026-10-24.md).
- Consumer adoption: https://github.com/APKiwiOrg/Grimhollow/issues/644
- FFT ocean cost and FFT caustics, a separate project: https://github.com/APKiwiOrg/KhaozEngine/issues/1607

## Milestones

Rows are in the order the milestones run.

| Milestone | Status | Issue | Owner gate |
| --- | --- | --- | --- |
| M1 Authored beds at the world manifest edge | shipped in v21.14.0 | https://github.com/APKiwiOrg/KhaozEngine/issues/1586 | none |
| M2 Underwater sunlight for submerged receivers | shipped in v21.15.0 | https://github.com/APKiwiOrg/KhaozEngine/issues/1588 | Native performance run before M3 ships |
| M5 Water column light and shadow, with a bent view of the bed | Landing 1 built, awaiting measured-cost gate before release. Landing 2 bent view not built | https://github.com/APKiwiOrg/KhaozEngine/issues/1608 | Owner approves the M5 ON performance run and rules on measured cost |
| M6 Scene reflections in water | design required | https://github.com/APKiwiOrg/KhaozEngine/issues/1609 | Owner approves its design |
| M7 River flow for procedural water | design required | https://github.com/APKiwiOrg/KhaozEngine/issues/1610 | Owner approves its design |
| M3 Underwater caustics | design decisions taken, document not written | https://github.com/APKiwiOrg/KhaozEngine/issues/1604 | Owner approves its design, rules on its measured cost |
| M8 Interaction ripples on the water surface | design required | https://github.com/APKiwiOrg/KhaozEngine/issues/1611 | Owner approves its design |
| M9 Wet banks above the waterline, free to run alongside | design required | https://github.com/APKiwiOrg/KhaozEngine/issues/1612 | Owner approves its design |
| M10 Underwater camera view | later, when Grimhollow schedules diving or swimming | https://github.com/APKiwiOrg/KhaozEngine/issues/1613 | Owner approves its design |
| M4 Native R8b optical adapter | waiting on the R8a native renderer | https://github.com/APKiwiOrg/KhaozEngine/issues/1300 | None beyond the normal GPU gate |
| Adoption in Grimhollow | v21.15.0 adoption in progress, ducks shadowless until M5. Shared River column inputs are linear RGB (0.28, 0.30, 0.18) and Clarity 1.5 m, with WaterColumnLight null for explicit consumer opt-in | https://github.com/APKiwiOrg/Grimhollow/issues/644 | Grimhollow owner |

## Decisions in force

- WO-D6: Caustics are their own milestone and read M2's water volume records.
- WO-D7: Public fixtures are synthetic. No Grimhollow image, model or source enters this repository.
- WO-D10: M2 shipped on its cost analysis without a native performance run. It is off unless a body opts in,
  and an opted-in submerged pixel costs a fixed 16 shadow comparisons and a four-slice march. The native run
  with opted-in water follows https://github.com/APKiwiOrg/KhaozEngine/issues/1602 and must land before M3
  ships, because caustics add cost to the same pixels.
- WO-D11: The milestone order is the table order. It ranks each milestone by how much it improves the river
  for what it costs, and by how much later work reuses it. M5's scene colour copy feeds M6, and M3's camera
  window feeds M8.
- WO-D14: Scene reflections in water reverse https://github.com/APKiwiOrg/KhaozEngine/issues/54 for water
  only. Environment probes stay not planned.
- WO-D15: M3 caustics cover procedural bodies only. FFT bodies render without them.
- WO-D16: M3 renders a caustic map rather than per-pixel focusing in the lit shaders. A pass draws each
  body's ripple surface from the sun into one camera window about 64 m across that fades at its edge. Bodies
  share map layers unless they overlap, and each layer holds a few depth slices. The engine default strength
  is physical and the river look raises it to clear bands. The design sets no GPU budget. The owner rules on
  the cost measured on #1602's workloads.
- WO-D17: Remaining water work preserves the [Clarity domain](../../KhaozEngine.Render3D/README.md#water-column-coefficients)
  of at least 0.001 m, because the shader intermediates must stay finite.
- WO-D18: Landing 2 retains [engine light units](../../KhaozEngine.Render3D/README.md#water-column-coefficients)
  for scattering, because physical radiance units would darken the water beside the engine's banks.
- WO-D19: Adoption starts from the owner's [river values](../../KhaozEngine.TileWorld.Render3D/README.md),
  linear RGB (0.28, 0.30, 0.18) and Clarity 1.5 m, because the daylight renders matched the silty river tone.
  The shared preset stays opted out until consumer adoption.
- WO-D20: Remaining work preserves [non-water shader source identity](../RENDER-PIPELINE.md#water-column-composite).
  Byte-identical compiled non-water shaders and the off-path GPU facts prove that frames without opted-in
  water are unchanged. `NonWaterShaderSourceEqualityTests` keeps this constraint executable.

## Open questions and owner gates

- The M2 native performance run needs the owner's separate approval naming its workload and cap, once the
  workloads in https://github.com/APKiwiOrg/KhaozEngine/issues/1602 exist. Those recipes touch the renderer
  performance files, so they are coordinated with the renderer performance lanes.
- Each milestone's design goes to the owner for approval before implementation.
- M5 Landing 1 waits for the owner to approve the separate M5 ON village and outdoors run in
  https://github.com/APKiwiOrg/KhaozEngine/issues/1602, then rule on its measured cost before release.
- M4 starts only when the R8a native renderer prepares and submits bounded water on main.

## Next step

Obtain the owner's approval for the #1602 run with an M5 ON variant, then their ruling on measured cost.
After release, Grimhollow adopts the selected river look and restores the duck shadow under
https://github.com/APKiwiOrg/Grimhollow/issues/644. [Landing 2](water-optics-design.md) remains unbuilt.
