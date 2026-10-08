# Explicit play-area boundaries

Explicit simulation previously ignored its host's configured WorldBounds. A late clamp would also
skip the required solid and medium proof of the corrected path. Boundary admission now stays inside
the shared capsule resolver.

## Contract and implementation

`MovementBoundary` is an immutable physics-frame certificate with a stable semantic identity.
It classifies starting centres, proposes constrained displacements and proves complete affine
segments plus their stored endpoints. A proposal is never clearance. The shared resolver constrains
each requested remainder, then re-proves bounds, solids and medium before publication. A refusal
on any corrected segment discards the entire tentative move.

The built-in rectangle and disc are convex. Exact dyadic endpoint membership proves their whole
closed segment, including the short storage-rounding correction. The comparison retains low bits
that float or double addition can discard. Projection may offer one inward candidate within the
existing contact scale, but the exact proof must still pass. This arithmetic concerns play-area
centre constraints only. It does not change or certify Bepu sweeps.

Scores are 1 to 10 with equal weighting.

| Approach | Benefit | Cost | Complete path proof | One resolver | Frame consistency | Simplicity | Total |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: |
| Clamp the published pose | Small change | Can cross an untested wall or medium boundary | 2 | 5 | 7 | 10 | 24 |
| Constrain candidates inside the shared resolver | Every correction is proved | Adds a boundary certificate and analytic membership | 10 | 10 | 10 | 7 | 37 |

Use the second approach. It also keeps outside correction bases distinct from legal inside motion.
`WorldBounds.TryCaptureExplicit` supplies certificates for CircleBounds and RectBounds. A custom
point-only clamp refuses until it supplies a complete-path certificate. Unconfigured legacy callers
retain their existing clamp path. Bounds constrain the capsule centre, matching the existing host
contract, rather than silently shrinking the play area by its radius.

## Read ownership

Player reads capture the boundary once before environment preparation and retain it through
simulation, replay or placement publication. Simulator frame changes are fenced from the start of
that preparation until disposal. Cell import admission captures the destination's boundary under
its own frame. Semantic identity uses authored world parameters and survives rebase.

Tests cover ordinary and tangent movement, outside-basis refusal, custom point-only refusal,
post-solid-correction refusal, hidden affine endpoint error, exact versus rounded circle contact,
far-frame equivalence, frame mutation during preparation/read, and same/far-cell teleport bounds.
The finite physics backend and native/game adoption gates remain unchanged.
