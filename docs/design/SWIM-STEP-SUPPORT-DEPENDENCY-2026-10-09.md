# Short-step support dependency and owner amendment

Status: ownership and body model ruled on 2026-10-09. Implementation handoff pending.

## Owner ruling

The [#438 specification at 86ff2b637](https://github.com/APKiwiOrg/KhaozEngine/blob/86ff2b637/docs/design/CONTACT-CLASSIFICATION-CONTROLLER-2026-10-08.md#ownership-with-swimming)
owns ground movement for every path, including swimming's explicit path. Its ground core owns
support semantics and support-owner binding, the body model, up/side/down passes, steps, ledges,
walls, ceilings, air, ground state and ground signals. The knee-height capsule shell blocks walls
and ceilings. The foot footprint decides support.

Swimming retains medium classification, water policies, buoyancy, swim, wade, entry/exit arcs,
the medium half of placement proofs, transport/read admission, lease discipline, and sweep
completeness with certified error brackets. Every tick retains one authenticated read interval.

This owner ruling amends the original F3 full-capsule solid proof to shell solid proof plus
certified footprint support plus medium proof. The original accepted F3 file and hash are retained
as the historical baseline. They do not override this amendment.

Explicit grounded ticks will call the #438 ground core through its phase 4 handoff.
`MovementSupportResolver` land selection will be replaced by the #438 support primitive.
The current implementation and `f3-full-capsule-v1` profile identity still describe the old code.
Do not relabel them as the new model before that handoff is implemented and verified.

The #438 specification still awaits owner review before its implementation plan. This ruling is
not authorization for this lane to build a ground resolver or implement the adjacent controller.

## Reproduction retained as RED evidence

A real identity-oriented Bepu box forms a 0.25 m bank beginning at X=1. A capsule with radius
0.25 m and half-height 0.75 m approaches at 4 m/s, 30 Hz, with StepHeight 0.4 m. The current
centre-column producer and shared resolver stop at X=0.74805236. The following target centre is
about X=0.88139, still outside the tread's column. The large-displacement step tests do not exercise
this ordinary approach.

Both tests in `ExplicitCharacterMovementTests.StepFootprint.cs` remain unchanged as the RED evidence
set until #438 phases 1 and 2 land. The bank approach fails. The companion passing guard requires
feet XZ to equal the body's axis and rejects an off-axis witness substituted for feet. Neither test
is removed or weakened. The bank case is now also named in #438 phase 1 acceptance.

## Certified correspondence and axis support

The approved finite capsule-feature query is specified at
[44acfe9b9](https://github.com/APKiwiOrg/KhaozEngine/commit/44acfe9b9), with reviewed implementation on
`fix/low-lip-resting-proof`. A Complete `CapsuleFeatureResult` under an authentic lease supplies the
target static and installed pose, feature kind, complete incident faces, certified witness, and
separation/normal enclosures. A convex crease with exactly one supporting top face is eligible for
this bank edge. The producer that installed the static maps it to the canonical owner.

Sweep normals and ray hits are not certificates. The correspondence specification measured corner
rays missing the edge by 0.03 to 5.08 micrometres. #438 phase 1 integrates the finite-feature query
as certified face identity for both consumers, without the legacy low-prop fallback.

Keep feet XZ equality. Support is evaluated at the axis column. The touched point within the disc
is a witness attached to the candidate, never its feet. At X=0.881, #438 supplies support Y=0.25
at the axis from the certified bank-top plane.

The old F3 alternative would have used tangent-plane feet Y=0.1856 with corner-normal Y=0.879,
placing the lower sphere centre at Y=0.4699. That round-bottom route is superseded, not a fallback.
Do not fork shared arithmetic, relax XZ equality or add another ground-step resolver. Shared Bepu
QueryView edits remain serialized between the owning lanes.
