# Cascade gather performance probe

Permanent diagnostic archive on `fix/mac-frame-cascade-probe`, based on
`66391b2d6fcadfe483849f5ffb8371bc13f55a78`. Never merge this branch into main.

The only renderer change copies `TemporalShadowGatherCompileTests.GatherFilterGlsl` exactly into
`ShaderSources.CascadeShadowFilterGlsl`. This measures the existing four-gather candidate in the
real renderer. It changes no resolution, cascade count, comparison footprint, material, temporal
policy, camera or game setting. It includes neither the point-caster sorting patch nor the native
pass-label probe. A private client substitutes only this build's renderer assembly.

The candidate's standalone oracle and component-order checks passed on all three native backends
in [run 39173269032](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39173269032). Those checks
cover four 8 by 8 cascades. They do not prove correctness at the production atlas dimensions,
integrated temporal behaviour, physical Windows or Linux GPU performance, or any frame-time gain.

The existing test requiring the shipped kernel to use explicit fetches intentionally remains
unchanged in this archive. This probe is not a shipping patch or a completed verification gate.
Adoption requires broader filter and scene correctness coverage, measured benefit, current-main
reconciliation and the full repository verification. No version bump or release is part of the probe.

Build the renderer in Release through `build-slot`. The comparison uses one stock and one candidate
bounded offscreen capture under a single heavy build-slot admission. Capture output, binary hashes
and comparison metrics belong under `/tmp/grimhollow-mac-frame-performance`. These measurements
exclude the live client, NPC simulation, UI and window presentation, so they cannot establish live FPS.
