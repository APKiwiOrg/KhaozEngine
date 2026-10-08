# Short-step support dependency

Status: open producer/correspondence alignment. This is not a new approved ground model.

## Reproduction

A real identity-oriented Bepu box forms a 0.25 m bank beginning at X=1. A capsule with radius
0.25 m and half-height 0.75 m approaches at 4 m/s, 30 Hz, with StepHeight 0.4 m. The current
centre-column producer and shared resolver stop at X=0.74805236. The following target centre is
about X=0.88139, still outside the tread's column. The large-displacement step tests do not exercise
this ordinary approach.

`ExplicitCharacterMovementTests.StepFootprint.cs` pins that missing behavior. Its other test protects
the current contract: an off-axis geometry point alone is not an axis support-plane certificate.
The exploratory attempt to substitute an edge point directly for Feet returned EnvironmentInvalid.
Removing the XZ check is not the fix.

## Existing evidence and unresolved boundary

The correspondence owner identified the approved finite capsule-feature query from
[44acfe9b9](https://github.com/APKiwiOrg/KhaozEngine/commit/44acfe9b9), implemented on
`fix/low-lip-resting-proof`. Its result binds the target static and installed pose to a feature,
complete incident faces, certified witness point, and separation/normal enclosures. The producer
that installed the static owns its canonical-owner mapping. A ray or sweep normal is not that
certificate.

For the full-capsule F3 model, a certified corner tangent can be expressed as an axis support plane.
That explains why Feet must remain at the query axis rather than becoming an arbitrary edge point.
The producer/consumer split for that witness, its finite height search and owner association must be
agreed before changing the native support facts. A point and normal are not an unbounded plane.

The later #438 draft at `9869af0fd` uses a raised knee-height shell and foot-footprint support.
That is a different ground model from immutable F3's full-capsule clearance. This lane keeps its
approved F3 work and does not independently build a competing ground controller or select the draft
as the game's model. Final controller/native adoption needs that ownership and model alignment.

The failing ordinary-step repro remains open. It is not native acceptance, and it does not block
independent water-policy, transport or generic profile work.
