# Swimming medium fact amendment SM1

Migration accepted this unreleased-value amendment in Grimhollow
[235118ae](https://github.com/APKiwiOrg/Grimhollow/commit/235118aeca5d9657d52126d9ed7e60fd57d45fc1),
with the adjacent-body clarification SM1a at
[fa49cf79](https://github.com/APKiwiOrg/Grimhollow/commit/fa49cf79).
It supplements immutable [F3](SWIM-ENVIRONMENT-FACADE-F3-2026-10-06.md) and VC1.
There is no native geometry API, second sampler, region evaluator or wire field.

## Required facts

- A domain key names one level body. All facts for that key under one query identity have the
  same nominal surface float. The lease checks trace contacts and wet centre samples against
  earlier accepted facts and refuses a mismatch atomically.
- NominalSurfaceY and UpperIsFreeSurface certify each contact's whole covered subregion.
  Producers partition contacts at every free/closed transition. A ceiling within certified error
  of the surface is closed. Unplaceable transitions return Unresolved. LowerY, a ceiling UpperY
  and boundary IDs remain local facts at IntervalColumnXZ.
- Surface eligibility requires every covered wet region to have a free top and one exact surface
  level. Equal-level adjacent domain keys are allowed. Centre membership chooses the current
  body's medium settings and buoyancy level. Simultaneous unequal levels refuse pending an
  explicit transition rule. Near-equal floats are different levels.
- Every contact supplies MovementContactOverlap at its Fraction. Tangent certifies no positive
  intersection of the uninflated capsule. Overlapping means positive or uncertain intersection,
  including error ties. Zero is invalid. A Tangent whose located column definitely intersects
  the uninflated capsule beyond the error bound is Invalid.

## Consumer checks and finite limits

The lease remembers at most 256 distinct domain levels for its read lifetime. This bounds one
query interval, not the world. A new key beyond that limit returns CapacityExceeded. Existing
keys remain readable. Trace validation and level consistency finish before either caller buffer
or the remembered level set changes. A malformed trace cannot register a partial level fact.

The located-column tangency check erodes the capsule and interval by the declared error and
rejects a definite positive intersection. It cannot reconstruct native region geometry. The
producer remains responsible for complete clipping, whole-region upper-kind partitions and
conservative overlap classification.

Finite box and plane fixtures distinguish skin-only contact from physical contact, positive
intersection and error ties. The SM1 guards and affected environment/movement tests passed 249
cases without skips. Whole-region policy acceptance and actual swimming are subsequent work.

Migration carries these facts into R4's native obligations. Native G1b, R3/R4 approval, rendering,
nav infrastructure, world handoff and release gates are unchanged.
