# Certified short-body contact shell

Design for [#1344](https://github.com/APKiwiOrg/KhaozEngine/issues/1344) in the
[#438](https://github.com/APKiwiOrg/KhaozEngine/issues/438) contact-controller programme.
Source baseline: `909fc59a8aa01d15f75cd7bd69c20218339e1466`, staged 21.7.0.
Status: proposed implementation contract under SB-O1 and SB-O2. No production implementation.
The [twelve-task plan](../superpowers/plans/2026-10-19-short-body-shell.md) implements this spec.

The owning checkout's `.superpowers/sdd/shell-1344/research.md` and `progress.md` are the
evidence and decision ledger. Their operative findings and all eight proof obligations are
retained here so another checkout can implement without private notes.

## Problem and binding decisions

The current shell spans feet plus StepHeight to feet plus twice CapsuleHalfHeight. A capsule
of radius R needs at least 2R of vertical span. Grimhollow's duck has R = 0.2 m, H = 0.25 m
and S = 0.4 m. Its span is 0.1 m, against a 0.4 m diameter. The cow has R = 0.9 m,
H = 0.95 m and S = 0.4 m. Its span is 1.5 m, against a 1.8 m diameter.

Removing validation makes the existing 0.01 m cylinder floor extend both shells about
155 mm below the knee and above the head. Changing only StepHeight also fails. The span
ceiling is 0.1 m for both bodies, but the banded 48 degree plane requires about 0.10038982 m
for duck and 0.44652343 m for cow. The issue's original assertion that cow clears the plane
predates the banded-plane correction. The authored stair fixture has 0.25 m risers.
The committed game world has no stairs placements, so the asset is a mobility contract,
not evidence of a currently occupied cow route. Bridge ramps are continuous terrain with
about 17.22 degree maximum grade and a 5 mm deck lip, not 0.31 m discrete steps.

> SB-O1: option B2. A certified full-width short-body convex shell preserves full horizontal
> radius, head height and 0.4 m step reach. Select its exact silhouette and ring height with
> an analytic banded-plane bound and the unchanged 0.001 m skin. Fitting bodies keep their
> existing capsule shell byte for byte.

> SB-O2: phase 6 proceeds as a mixed adoption. Profiles retain independent controller and bake
> selection. Mixed game adoption and its SG-21 budget are a separate lane.

This lane adds engine capability only. Grimhollow tuning, rebake and playtest wait for a
released engine. Contact aquatic navigation and public explicit bake profiles remain #1389.

## Global constraints

- Preserve the Euclidean 0.001 m arrival ball, roof and footprint protections and raw layer identity.
- Raw physics identity remains `(StaticHandle, ElementId)`. Coincident members never coalesce.
- No tolerance, skin, band, epsilon, reach or existing work/storage ceiling may be widened or added to obtain acceptance.
- Preserve the independent 0.001 m placement offset, 0.0001 m contact/join band and 0.0005 m HeightError ceiling.
- Preserve 256 raw support members, 512 support witness slots and the cumulative 1,048,576-event curved Find ceiling.
- Preserve fitting capsule geometry, queries, arithmetic and format 1 legacy bytes. Preserve format 2 bytes when no short-body contact profile is present, at the same engine identity.
- Use deterministic counters, not timing. No timing, load, stress, allocation benchmark, repeated test loop, consumer launch or world bake is authorized.
- Ride staged 21.7.0 if it remains untagged at implementation finish. Otherwise select the next free patch above current main, with its CHANGELOG entry and guarded declarations in the same commit.
- Implementation workers stop at verified commits. Integration, push, packaging, release, tags and Grimhollow adoption belong to the owning orchestrator and their separate gates.

## Requirements

### SB-1. Select by geometry without repairing an invalid capsule

Retain the current binary32 evaluation order for `span = 2f * H - S` and `diameter = 2f * R`.
If `span >= diameter`, use the original capsule factory, cylinder floor, centre computation,
validation and operational path unchanged. In particular, exact equality retains the historical
0.01 m cylinder and its 5 mm endpoint extension. Do not silently fix that separate behavior.
Failure of the old capsule's slope check remains failure, never a reason to select another shape.

Only a positive span below diameter selects ShortBody. Require finite positive R and H, finite
nonnegative S, finite positive span, the existing footprint minimum, and the proved plane bound
in SB-3. Reject invalid or uncertifiable tuning before motion. Legacy controller selection
does not validate or use a contact shell. No new game-facing shell-radius or ring-height knob.

### SB-2. Use a sharp lower cone and a full-radius upper cylinder

Let T = `2f * H`, S = StepHeight and C = `0.5f * (S + T)`, each evaluated once in that order.
Keep the represented values separately. Do not reconstruct endpoints from a rounded half-span.
Require S < C < T after representation. At geometric feet zero the shell is exactly:

```text
K = conv({(0,S,0)}, Disc(R,C), Disc(R,T))
radius(y) = R * (y-S)/(C-S)   for S <= y <= C
radius(y) = R                 for C <= y <= T
hK(n) = max(S*ny, C*ny + R*sqrt(nx*nx+nz*nz),
                     T*ny + R*sqrt(nx*nx+nz*nz))
```

This takes the research double cone's proved lower half and replaces its tapering upper half
with a cylinder ending in a closed head disc. The result remains one convex solid, not a union
of query proxies. Its maximum horizontal radius is exactly R, bottom exactly S and head exactly T.
The full head disc protects off-axis roofs over the entire declared radius, including roofs
that the double cone's upper taper would miss. This is a deliberate, conservative upper
collision silhouette. It does not change full-body medium or support geometry.

The midpoint is deterministic, symmetric in the available vertical interval, independent of
query direction and slope, and has substantial clearance for both required bodies. Raising
the ring to rescue other tuning is not an implicit fallback. Such tuning must satisfy SB-3
with this same rule or refuse.

Scores below are out of 10 per column, higher is better. Completeness is still a proof gate.

| Profile | Width/head/step fidelity | Plane proof | Off-axis roof protection | Simple exact query | Total |
| --- | ---: | ---: | ---: | ---: | ---: |
| Midpoint sharp cone-cylinder | 10 | 10 | 10 | 8 | 38 |
| Midpoint sharp double cone | 10 | 10 | 6 | 8 | 34 |
| Rounded cone or filleted shoulder | 8 | 6 | 8 | 4 | 26 |

The double cone is smaller above C and shares the lower clearance proof, but needs an extra
roof envelope. Rounding offers smoother geometric normals, but an outward fillet changes R,
S or T and an inward fillet shrinks the approved volume. Constrained rounding adds a new
profile and new extrema without a demonstrated benefit. Choose the sharp cone-cylinder.
Treat its apex, circular shoulder and head rim as closed features with normal cones. Never
average a seam normal into a climbable plane. Use a deterministic certified member of the
actual normal cone, with the controller's existing wall/slope classification.

### SB-3. Certify support-plane clearance analytically

For `theta = MaxSlopeRadians + max(TractionHysteresisRadians, 0)`, require finite
`0 <= theta < pi/2` and use a normalized upward support-plane normal at that banded angle.
Axisymmetry makes azimuth irrelevant. The lowest perpendicular clearance above the plane is:

```text
d(theta) = min(S*cos(theta), C*cos(theta)-R*sin(theta),
              T*cos(theta)-R*sin(theta))
margin = d(theta) - 0.001 m
```

The last term cannot beat the ring term because T > C and cos(theta) > 0. Each remaining
term decreases with theta, so the banded maximum proves every lesser walkable angle.
This is a bound for the entire convex volume, not sampled surface points. Require a directed
lower bound for d at least the unchanged skin. No epsilon is added at equality. Use certified
trigonometric enclosures of the actual represented angle, with refinement or refusal when a
comparison is indeterminate. Ordinary MathF values alone are not a certificate at the boundary.

| Body | R | S | T | C, nominal | Minimum clearance at 48 degrees | Surplus above 1 mm skin |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Duck | 0.2 m | 0.4 m | 0.5 m | 0.45 m | 0.1524798078 m | 0.1514798078 m |
| Cow | 0.9 m | 0.4 m | 1.9 m | 1.15 m | 0.1006698544 m | 0.0996698544 m |

Those are real-arithmetic nominal values, not test tolerances. Independent interval tests
over the production binary32 R/H/S/angle operands must prove surplus at least 0.15147 m
for duck and 0.09966 m for cow. These printed downward bounds do not replace the exact
admission comparison. Both bodies retain S = 0.4 m and their original foot radii 0.1/0.45 m.

### SB-4. Add a dependency-free moving-convex query contract

Physics owns a closed value-input capability `IPhysicsConvexQueries`. Initially its sole
admitted `ConvexQueryKind` is ConeCylinder. It is not an arbitrary callback-based shape API.
`ConvexQueryShape` carries Kind, Radius, LowerY, RingY, UpperY and Margin as immutable values.
The shape is local to a feet-origin Pose. Base Margin is positive zero. Margin 0.001 m is
the existing touching probe's exact Minkowski sum with a sphere, with support `hK(n)+Margin*|n|`.
It is not a rounded replacement for the base shell or permission to move the body by a margin.

The capability includes certified sweep, distance, complete contacts and penetration. Results
distinguish unavailable evidence from Clear, empty selection and genuine contact. A sweep
returns an enclosing earliest-impact bracket plus its proved operational hit/normal. A distance
result encloses the nearest nonnegative separation and carries both attained witnesses and raw
identity, or an explicit empty-selection result. Complete contacts retain every relevant raw
constraint and separation bound. Penetration supplies the deepest member's certified minimum
translation proposal, with deterministic raw-ID tie breaking. It is not a global-union MTV.
Recovery must requery and validate each placement, including after the final allowed correction.

Keep IPhysicsWorld and its capsule signatures source/binary compatible. New backends discover
the optional capability normally. A short shell on a backend lacking it refuses explicitly.
It never falls back to a capsule, sphere cloud, inscribed hull or unchecked raw query.

### SB-5. Prove the actual operational shape and represented installation

Bepu owns the continuous support mapping and its operational distance/sweep/contact proposals.
It may use bounded GJK/EPA to propose witnesses, but an approximate miss, simplex convergence
test or operational penetration vector is never proof. Independently bound the exact continuous
volume, including the circular shoulder/head rim and touching Margin. No polygon is the shape.

Use installed Bepu shape dimensions and represented transform operations as the sole target
truth. Bind root typed index, installed generation, centroid wrapper, raw child/triangle and
the actual composed pose. Reuse InstalledPoseOperator and the lane B/C represented-source
rules, not authored vertices, a normalized quaternion or an assumed exact inverse. Bound
forward and inverse-operation error wherever the operational path uses each. Exact rigid
poses still need operational correspondence.

The new kernel represents lower-cone, cylindrical side, head disc, circular shoulder/rim and
apex feature domains explicitly. Use support-plane dual separation certificates for a whole
clear fraction interval and attained primal/KKT feature witnesses for contact and penetration.
Finite feature-regime root isolation includes closed boundaries and double roots. Squared
radical equations retain their original sign/domain predicates, so extraneous roots cannot
become hits. Interval refinement that cannot decide a sign returns Unresolved, never Clear.
GJK/EPA ordering is only a proposal accelerator. Exact feature certificates decide publication.

For every operational hit prove normal-cone membership, finite unit normal, witness membership
and that its skin-shortened advance is inside the certified earlier clear interval.
The operational normal points away from the target. Its negative is the query's outward normal
at the paired query witness. Do not reverse that sign when checking the cone feature.
An operational miss requires full-path Clear for this exact shape. Any disagreement refuses the
whole query. The permitted encoded spatial error, including the whole Hit bracket, remains
at most 0.001 m. It is an evidence budget, never extra collision skin or arrival tolerance.

### SB-6. Cover every existing target family and the complete selected view

Support identity and rotated boxes, installed convex hulls and their centering wrappers,
spheres, capsules, cylinders, finite one-sided mesh triangles and every currently admitted
compound descendant. Include fixed-pose dynamic targets selected by QueryFilter. Preserve
mobility, exclusions, raw identity and the existing mesh initial-overlap and CullBackFaces
semantics. A mesh is a finite sided surface, never its filled AABB or a convex solid.

Reuse installation-owned immutable topology and proved tree pruning where their hypotheses
hold for this moving support function. Derive its own aperture and operation-error pad from
the full radius and S/T extrema. Capsule-specific pruning is not inherited by name. Preflight
the same selected tree interval used for enumeration. A later unresolved leaf cancels an
earlier Hit, distance, normal, contact list or recovery proposal. Validate before copying
caller output, leave unused tails intact, and preserve capacity/exception ordering.

### SB-7. Keep finite domains, bounded work and current evidence

Upright queries require exact identity orientation. Original feet-position and displacement
components have magnitude at most 1,000,000 m. Require finite `0 < R <= 1000`,
`0 <= S < C < T <= 2000` m and `Margin` equal to 0 or the existing 0.001 m skin.
This deliberately finite new capability does not restrict existing capsule queries.
Installed transforms keep coordinates within 2048 m, positive determinant, the existing
squared quaternion norm band `1 +/- 2^-20` and positive finite mesh scale.

Reuse, without increasing, the general-sweep ceilings: 8192 nodes, 4096 leaves, 128 pending
branches per tree, 4096 aggregate callbacks, 4096 unproved registrations, 4096 primitive
descendants per query, 256 fraction/refinement cells per primitive, depth 48, 4096 feature
regimes per primitive, 64 compound children, 256 hull faces, 508 vertices, 1524 face indices
and 4096 bits per exact integer. Count every proposal iteration as a cell and every
feature/certificate attempt as a feature. Exact-sign work shares the primitive account.
Exhaustion is sticky. At most four simultaneous scratch loans, the fifth refuses atomically.
Smaller internal test budgets are allowed, larger production budgets are not.

Every query authenticates receiver, selected view, origin, geometry generation and read
interval before work and before publication. Removal/reuse, pose/velocity mutation, Step,
sleep/wake, rebase, disposal, wrong thread and stale movement pins retain their existing
refusal rules. No mutable scratch or evidence crosses readers. Collect deterministic query,
candidate, feature, cell, refusal and high-water counters with worker merge/restoration.
Enforcement works with diagnostics disabled. No elapsed-time or throughput acceptance claim.

### SB-8. Introduce one shell descriptor and preserve the capsule public API

Locomotion owns `ContactShellKind` and immutable `ContactShellDescription`. A description
holds the kind, exact capsule radius/length and old centre offset, or the exact convex shape
at feet origin, plus recipe version. `ContactShell.Describe(in MoveTuning)` is the new general
door. `ContactShell.Validate` uses it. Existing `ContactShell.Shape` continues returning the
identical CapsuleShape for fitting bodies and throws NotSupportedException for ShortBody,
directing callers to Describe. Its return type never changes. Existing Centre arithmetic
remains unchanged, while new code uses the descriptor's correct per-kind pose.

Ground recovery, upward lift, side slide, ground seat, final clearance and placement use
the description. Dry and leased query contexts dispatch by kind. Keep the original capsule
statements in the Capsule branch, rather than rebuilding them through the new solver.
The ShortBody branch uses SB-4's capability for all queries, including the skin-grown touch
probe. Step pacing still charges the discrete support rise, not the lower cone's side normal.
Preserve four recovery passes, four slides and the existing climb/drop/substep budgets.

### SB-9. Keep all execution paths and full-body responsibilities aligned

Air movement, ceilings, landing, slopes, wading and within-tick swimming handoffs use the
same shell description as ground. The medium/domain resolver must carry that description
instead of treating a bounding capsule as its physical query. Its swept bounds can enclose
the shell but cannot supply a Clear verdict. Certify all endpoints and centre/feet mappings,
including the public explicit 0.001 m placement offset and ordinary zero offset.

Support stays on `FootRadiusFraction * R`. Complete footprint discovery, hidden supports,
joins, curved witnesses, raw member provenance and partition independence remain unchanged.
The full original body capsule owns medium coverage, waterline and domain eligibility.
Navigation area/headroom continues to inspect every column touched by full R and height 2H,
choosing the nearest raw layer before applying tags. A raised anchor earns fresh headroom,
shell clearance and exact-axis support. Neither a smaller cone cross-section nor a coincident
resting height may change any of these responsibilities.

### SB-10. Add compatible KENB format 3 with explicit shell identity

Write format 1 for all-legacy profiles, unchanged. Write format 2 for contact collections with
only fitting capsule shells, unchanged at the same engine identity. Write format 3 when any
contact profile uses ShortBody. Never select the format based on a legacy profile's body.

Format 3 retains the existing header, flags zero, source ordering, 27 tuning fields and each
profile's exact 14-byte format 2 contact block, including its required positive-zero reserved
placement field. Append one 26-byte shell record to each profile immediately after that block:

| Field | Encoding and canonical value |
| --- | --- |
| Shell kind | u8, 0 LegacyNone, 1 Capsule, 2 ShortBody |
| Recipe version | u8, 0 for LegacyNone, 1 for Capsule or ShortBody |
| Radius | IEEE binary32, capsule or cone-cylinder R |
| CapsuleLength | IEEE binary32, old capsule factory length, otherwise positive zero |
| LowerY, RingY, UpperY | Three IEEE binary32 values, S/C/T for ShortBody, otherwise positive zero |
| Margin | IEEE binary32 positive zero for every persisted base shell |

LegacyNone's entire remaining record is positive zero. A capsule's pose still follows its
unchanged tuning/centre recipe. Recompute the expected description from tuning and reject
any noncanonical field, unknown kind/version, nonfinite number, nonzero unused bit, wrong
dimension, invalid margin or truncation. Do not normalize negative zero. File identity
compares bits. Runtime compatibility additionally compares the baked descriptor's kind,
recipe and all represented dimension bits, alongside existing tuning/binding checks.

The format 3 payload is exactly the format 2 payload grammar, including resting feet arrays
for contact profiles and legacy payloads for legacy profiles. Update all reader size bounds,
header gates, payload allocation, decoding and writer version selection accordingly. Reading
formats 1/2 implies the historical legacy/capsule recipe only. Never infer ShortBody from
missing fields. An old-format/short-body expectation is an identity mismatch requiring rebake.
Older readers reject version 3 explicitly. There is no in-place migration or permissive load.

### SB-11. Bind shell identity to runtime profiles and proof reuse

NavProfileIdentity retains the actual validated description, not merely tuning from which a
different implementation might later infer a shape. GroundNavigation.Validate rejects a
different kind, recipe or represented dimension before planning or traversal. Existing
binding-kind checks still distinguish Legacy, OrdinaryContact and PublicExplicit.

ExplicitMovementProfile keeps its existing fingerprint recipe for capsule bodies at the
same engine identity. ShortBody uses recipe 2, controller model
`contact-short-body-shell-footprint-swim-v1` and the exact SB-10 short-shell record in addition
to every existing semantic field. No game movement wire field changes in this lane.

Immutable profile and initial-Hold reuse identities include kind, recipe and all shell bits.
Keep raw capture column, original surface ordinal, raw height bits and derived layer even
when two anchors coincide. Physics completed-footprint reuse remains a fact about the full
support request, with authenticated profile-domain tokens. It never caches a shell verdict.
Reuse-off, footprint-only and stable-Hold modes produce identical ShortBody files. Existing
seven capsule baselines and format 1 goldens remain unchanged at normalized engine identity.

### SB-12. Prove motion, identity and protections before claiming adoption readiness

Require duck and cow tests against every SB-6 family for Clear, first Hit, exact tangency,
skin touch, nearest distance, penetration, multiple constraints and recovery. Include all
azimuths through analytic or feature proofs, not a finite direction sample as certification.
Test sparse mesh pruning, hidden later blockers, coincident descendants, near-degenerate
features and represented non-rigid transform disagreements. Invalid/unbounded cases refuse.

Locomotion proofs cover 0.25 m stair rises within the retained 0.4 m reach, paced ascent and
descent, a 5 mm bridge-like lip, banded slopes, walls/door openings, full-width off-axis roofs,
lifted ceilings, air and recovery. Full-width openings narrower than 2R must obstruct at the
upper cylinder. Sharp shoulder contacts must not manufacture an uncharged climb. Both heads,
ordinary and public explicit paths agree after accounting for their existing placement offset.
Every accepted target is inside the existing Euclidean 0.001 m XYZ arrival ball.

## Eight research proof obligations and owning tasks

| Research obligation | Concrete retained contract | Tasks proving it |
| --- | --- | --- |
| 1. Complete body-radius footprint support | Duck 0.1 m and cow 0.45 m discs, hidden/curved/join/partition proofs and raw provenance | 8, 11 |
| 2. Certified real-query motion | Exact moving solid and grown touch probe, all target families, operational correspondence, finite domain, stale leases, bounded atomic refusal | 1 to 7, 9 |
| 3. Roof protection | Full head disc, existing capsule off-axis/raised-anchor outcomes, short-body roof and lift refusals | 8, 9, 11 |
| 4. Full navigation footprint | Full R/2H, every touched column, nearest raw surface before tags | 10, 11 |
| 5. Raw layer and reuse identity | Original column/ordinal/height bits and shell descriptor survive coincident anchors | 10, 11 |
| 6. Euclidean 1 mm arrival | Equality and adjacent floats, XYZ ball, Hold and directed recurrence unchanged | 9, 11 |
| 7. All paths and full-body mapping | Both heads, ground/air/medium/handoffs, resting anchors and signals | 8, 9, 11 |
| 8. Honest bakes and release | Formats 1/2 frozen, canonical format 3, mismatch refusal, final version/changelog and release gate | 10 to 12 |

## Acceptance groups

| Group | Evidence required |
| --- | --- |
| A. Shape and plane | Independent rational/interval oracle, exact represented dimensions, support extrema and duck/cow surplus, boundary refusal, unchanged capsule bits |
| B. Complete backend | Every target family and query operation, exact/operational correspondence, genuine normals/witnesses, full selected-view traversal, no partial result |
| C. Lifetime and work | Current receiver/view/lease, generation/origin/exclusion tests, sticky ceilings, bounded scratch, atomic outputs and diagnostics-off enforcement |
| D. Controller | Ground, step pacing, roofs, air, recovery, medium transitions and both-head parity with full body/foot geometry |
| E. Navigation | Formats 1/2 goldens, format 3 roundtrip/corruption/mismatch, raw layers, full footprint and all reuse modes with deterministic serial/parallel bytes |
| F. Finish | Living docs, requirement-to-test record, current staged version/changelog, once-only full Release build/tests/format and governance exits |

Record actual reds, focused greens, deterministic counters and final command exits in
`docs/verification/2026-10-19-short-body-shell.md`. A refusal or target shortfall is reported
as such. No measured speedup, SG-21 acceptance, #1334 closure or game acceptance is implied.

## Explicit non-goals and handoff

- No tolerance change, body narrowing/heightening, reduced StepHeight or slope retuning.
- No generic user-supplied convex callback, new persistent Bepu shape registration or dynamics simulation primitive.
- No revision of fitting capsule arithmetic, equality-floor behavior, format 1 or capsule-only format 2.
- No mixed-profile game wiring, engine release, game pin, Hollowmere rebake or creature playtest.
- No claim that #1389 aquatic navigation or native G1b acceptance is completed by CPU shell proofs.

The design author commits only this spec, its plan and the index row. Implementation requires
the owning orchestrator's next authorization. After engine implementation and release, the
separate Grimhollow lane chooses each profile's controller, vendors that release, tunes the
two bodies, rebakes matching profiles and collects the owner playtest and SG-21 evidence.
