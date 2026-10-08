# Swimming span overlap amendment SM2

Migration accepted SM2 at Grimhollow [75c8e40a](https://github.com/APKiwiOrg/Grimhollow/commit/75c8e40a).
It supplements immutable [F3](SWIM-ENVIRONMENT-FACADE-F3-2026-10-06.md) and
[SM1](SWIM-MEDIUM-FACTS-SM1-2026-10-08.md). No native sampler or shape assumption is added.

## Required producer certificate

Each contact retains its point `Overlap` at `Fraction` and requires `SpanOverlap` over the closed
fraction interval of its single owning span. `Tangent` certifies no positive-volume overlap between
the uninflated capsule and that region anywhere in the span. Positive or uncertain overlap,
including numerical ties, is `Overlapping`. Zero is invalid. An exact geometric certificate may
prove equality without uncertainty. Merely comparing an approximate distance to zero may not.

Producers may partition spans at uninflated-overlap transitions. Ordered complete coverage, error
bounds and existing capacity limits still apply. Exceeding capacity refuses the entire query.
Contacts cannot be shared between spans. Each contact's fraction lies inside its owning span.
The lease rejects unowned contacts, overlapping contact ranges, span Tangent with point Overlapping,
and different point/span tags for a zero-length span. Validation precedes both output buffers and
remembered domain-level registration.

## Shared resolver use

A non-swimmer may cross a wet coverage span when every forbidden-region contact certifies span
Tangent. A point Tangent alone never approves a finite segment. Point overlap distinguishes an
invalid starting placement from a boundary approached by the candidate motion.

SurfaceSwimmer uses the same constraint path for known closed-water regions. Free-surface level
agreement is checked per simultaneous coverage span, allowing separated bodies at different levels
without introducing a rule for overlapping unequal surfaces.

DryOnly forbids positive water overlap. WadeOnly uses the original capsule height and configured
entry fraction to forbid deep overlap. Constant domain levels permit evaluating that threshold over
the entire affine path without extrapolating a bed column. Closed regions are forbidden. Actual
footing still requires the support resolver and its completely proved approach.

The shared capsule resolver chooses the earliest solid/water proposal, gathers the active normals,
projects the whole set, and retraces every prefix and remainder against both solids and medium.
A water contact distance is a proposal, never a clear-through certificate. Eight corrections and
nine endpoints remain the limit. Refusals discard the whole tentative path. Starting positive
forbidden overlap yields PlacementRefused at the public explicit entry point.

Stored-coordinate enclosures remain mandatory. When the endpoint arithmetic and represented
CapsuleShape conversion are both proved exact, the original body already encloses the path.
Unnecessary inflation must not destroy its exact tangency certificate. Inexact arithmetic retains
the outward enclosure and its 1 mm refusal budget. This change is in locomotion rounding only,
not shared Bepu arithmetic.

Migration carries SM2 into R4 alongside SM1. These generic fixtures do not certify native producers,
released adoption, nav completeness or a playable game feature.
