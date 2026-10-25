# AAA VFX

## Goal

Deliver modern AAA-quality effects and ambience through the shared particle and rendering stack.
Tier 1 is complete. Tier 2 adds effect vocabulary when a game pulls for it, while Tier 3 simulation
scale and authoring tools remain demand-gated.

## Links

- Roadmap: https://github.com/APKiwiOrg/KhaozEngine/issues/55
- Docs changed: [Render3D](../../KhaozEngine.Render3D/README.md),
  [particle simulation](../../KhaozEngine.Particles/README.md),
  [particle presentation](../../KhaozEngine.Particles.Render3D/README.md),
  [consumer contract](../USING-KHAOZENGINE.md).
- Design rationale: [Tier 1](../design/AAA-VFX-TIER1-DESIGN-2026-07-16.md).

## Milestones

| Milestone | Status | Issue | Owner gate |
| --- | --- | --- | --- |
| M1 HDR, filmic tonemap, HDR bloom, flipbooks and screen-space distortion | complete | https://github.com/APKiwiOrg/KhaozEngine/issues/55 | none |
| M2 Lit smoke | next tier, waiting on game pull | https://github.com/APKiwiOrg/KhaozEngine/issues/55 | Select consumer need and independent design |
| M3 Mesh particles | next tier, waiting on game pull | https://github.com/APKiwiOrg/KhaozEngine/issues/55 | Select consumer need and independent design |
| M4 Simulation death and collision events | next tier, waiting on game pull | https://github.com/APKiwiOrg/KhaozEngine/issues/55 | Select consumer need and deterministic event contract |
| M5 Textured UV-scrolling ribbons | next tier, waiting on game pull | https://github.com/APKiwiOrg/KhaozEngine/issues/55 | Select consumer need and independent design |
| M6 GPU simulation | gated Tier 3 | https://github.com/APKiwiOrg/KhaozEngine/issues/55 | Concrete game scale requiring it |
| M7 Data-driven authoring and tooling | gated Tier 3 | https://github.com/APKiwiOrg/KhaozEngine/issues/55 | Demonstrated effect-tuning friction in a game |

## Decisions in force

- VFX-D1: The AAA target applies to effects and their pipeline pieces. It does not raise the general
  rendering target beyond semi-realistic fidelity with a light stylization accent.
- VFX-D2: Tier 2 features are independent follow-ups, each designed and implemented in its own
  worktree when a game needs it. Their table order is not a dependency chain.
- VFX-D3: GPU compute simulation remains gated on concrete game demand. Cross-backend compute,
  determinism and golden verification must be part of its design.
- VFX-D4: Authoring tooling may advance when effect iteration demonstrably slows a game. It starts
  with data-driven descriptors through Content and hot reload, with an editor a later possibility.

## Open questions and owner gates

- The owner selects the first Tier 2 consumer and effect requirement before a feature is planned.
- No Tier 3 work starts without a game demonstrating the scale or authoring problem it solves.

## Next step

Identify the first concrete Tier 2 game pull with the owner, then write that feature's scoped design
and issue under the roadmap. Keep GPU simulation and authoring tooling gated until their need is
demonstrated.
