# Per-body water optics

## Goal

Water bodies shade from their own authored bed instead of the first opaque surface behind them, at the
world edge as well as inside it, and submerged receivers are lit by sunlight that has passed through the
water. Native bounded water from MapDoc binds the same neutral optical resource. Grimhollow restores its
floater sun shadows and adopts authored beds once the engine work ships.

## Links

- Roadmap: https://github.com/APKiwiOrg/KhaozEngine/issues/1300
- Docs changed: [TileWorld.Render3D](../../KhaozEngine.TileWorld.Render3D/README.md#authored-optical-beds),
  [Render3D](../../KhaozEngine.Render3D/README.md), [consumer contract](../USING-KHAOZENGINE.md).
- Design rationale: [per-body water optics](../design/WATER-OBJECT-OPTICS-DESIGN-2026-10-24.md),
  [native handoff](../design/WATER-OPTICS-R8B-HANDOFF-2026-10-24.md).
- Consumer adoption: https://github.com/APKiwiOrg/Grimhollow/issues/644

## Milestones

| Milestone | Status | Issue | Owner gate |
| --- | --- | --- | --- |
| M1 Authored beds at the world manifest edge | in progress | https://github.com/APKiwiOrg/KhaozEngine/issues/1586 | Owner approves the exact v21.14.0 tag |
| M2 Underwater sunlight for submerged receivers | brainstorm, design required | https://github.com/APKiwiOrg/KhaozEngine/issues/1588 | Owner approves the spec, then the execution choice |
| M3 Native R8b optical adapter | waiting on the R8a native renderer | https://github.com/APKiwiOrg/KhaozEngine/issues/1300 | None beyond the normal GPU gate |
| Adoption Grimhollow floater shadows and authored beds | waiting on M1 and M2 releases | https://github.com/APKiwiOrg/Grimhollow/issues/644 | Grimhollow owner |

## Decisions in force

- WO-D1: The v21.14.0 tag is held until M1 lands on main, so the authored-bed opt-in does not ship with
  a known world-edge defect. The release then follows the engine release skill with the owner's approval
  of the exact tag.
- WO-D2: A world manifest edge is a defined boundary, not missing data. A neighbour region that exists in
  the manifest but is not loaded keeps the strict refusal.
- WO-D3: Beyond a manifest edge the bed continues the last in-world ground, clamped to the edge, so the
  certificate band past the edge certifies without touching Render3D's one-certificate admission or
  trimming the drawn water. A present region without a current surface snapshot still gets no
  manufactured bed.
- WO-D4: Underwater sunlight refracts the sun at the water surface for submerged receivers (Snell,
  n about 1.33), attenuates it along the in-water path with the body's absorption and softens the
  penumbra with depth, optionally with caustic modulation. It needs the water surface height above
  opaque receivers, which the opaque pass lacks today.
- WO-D5: M2 runs brainstorm, written spec, owner approval, plan and owner execution choice in that order.
- WO-D6: Public fixtures are synthetic. No Grimhollow image, model or source enters this repository.
- WO-D7: Each release carrying M1 or M2 is sent to the Grimhollow coordinator with its tag and adoption
  notes. Grimhollow ships floaters without a sun shadow on 21.13.0 until then.

## Open questions and owner gates

- The owner approves the exact v21.14.0 tag once M1 is on main.
- The M2 spec must settle how opaque receivers learn the water surface above them, per-body versus
  scene-wide opt-in, and the cost on each backend. The owner approves it.
- M3 starts only when the R8a native renderer prepares and submits bounded water on main.

## Next step

Land M1 on this branch with a public synthetic fixture covering water that reaches the manifest edge and
a neighbour that is in the manifest but unloaded, then review, integrate and ask the owner for the tag.
Brainstorm M2 in parallel.
