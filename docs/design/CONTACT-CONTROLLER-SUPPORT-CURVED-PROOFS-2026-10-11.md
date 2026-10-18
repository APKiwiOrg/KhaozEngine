# Attained curved footprint witnesses

Task 5 of [the support gates](CONTACT-CONTROLLER-SUPPORT-GATES-2026-10-11.md).
This is the geometry certificate consumed by Task 6, not a production API or a cost certificate.
SG-1, SG-4, SG-7, SG-19 and SG-23 remain binding. The named certificates below cover every
orientation admitted by phase 2b, including composed compound leaves.

## Installed domain and common obligations

`SupportNeighborhoodCurved.Capture` and `Leaf` are the admission owners. They require finite
positions of magnitude at most 2048 per component, quaternion squared norm in
`[1 - 2^-20, 1 + 2^-20]`, a resolved orientation-preserving installed operator, and
`0 < R <= 64`, `0 <= H <= 64`. Cylinders require `H > 0`. Compounds contain 1 to 64 leaves.
The parent, local and composed poses have the same proof obligations. Composition uses the
represented `Compound.GetWorldPose` result, not an ideal product of normalized quaternions.
There is no restriction on tilt, yaw, axis sign or distance from an upright or horizontal pose.

Let `C` be the installed centre, `d` the normalized installed local Y column, `R` the radius
and `H` the half length. A sphere ignores `d` and has `H = 0`. Write `a` for the XZ query axis,
`r` for its closed disc radius, `c > 0` for the cosine gate, and `V` for SG-3's closed admission
column. The expanded radius is used only for membership. Extrema always use the original `D`.
Membership is of the raw surface, including its lower half and downward or vertical caps.
After membership, retain the whole part. Do not clip its maximum to the vertical band of `V`.

On a smooth part, an upper point has outward normal `n.Y >= 0`. Include limiting points with
`n.Y = 0` in the compact geometric domain. They have attained witnesses but no finite tangent
support. Every evaluation of `T_x(a)` first proves `n.Y > 0`. A walkable point satisfies
`n.Y >= c`, including equality. The footprint first maximizes Y over the closed upper domain
in `D`. If this maximum is steep, maximize Y on the closed walkable domain when it is nonempty.
The contribution is `min(Y, T_x(a))`. The separate axis role is the highest upper point of that
same part on the axis column. It does not use the footprint's walkable replacement.

At tied maxima, retain all tied strata until classification and attained-point selection are
settled. A tied walkable maximum is already a maximum of the walkable region. Flat stretches
use an attained point nearest the query axis, then a fixed coordinate order if still tied.
This only chooses a witness within a raw part. It does not change the released inter-member
tie order, raw feature identity, inclusive reach, join suppression or analytic sampling.
One raw capsule includes both ends and its barrel. A cylinder has three independent raw parts.
Curved parts never join. Axis and footprint occupy SG-7's two role slots, even when coincident.

## Sphere certificate

Project `C` to XZ. Let `A = |a - C.xz|` and `rho = max(A - r, 0)`. The upper projection is the
closed circle of radius `R`. It intersects `D` iff `rho <= R`. Its nearest point to `C.xz` is
`q = C.xz` when `A <= r`, otherwise `q = C.xz + (a - C.xz) * rho / A`.
The attained footprint is `(q.x, C.Y + sqrt(R^2 - rho^2), q.z)` with radial normal.
Height decreases and normal Y decreases with rho. Thus a steep highest point implies an empty
walkable region. Equality `rho^2 = R^2 * (1 - c^2)` is walkable. At `rho = R`, normal Y is zero.
The axis role uses rho `A`, if `A <= R`. At `A = R` it also retains the zero-normal witness.

For raw admission, squared distance from `C` to `V` has minimum at the nearest XZ point and
clamped Y. Its maximum is `(A + rV)^2 + max((b - C.Y)^2, (s - C.Y)^2)`.
The surface meets `V` iff `minimum <= R^2 <= maximum`. Both inequalities are needed when `V`
lies inside the sphere. Connectedness and continuity prove sufficiency, including tangencies.

Strata are apex, axis interior, disc boundary, axis tangency and disc tangency. All use rational
operations and positive square roots. No sampled angle, contact probe or epsilon is involved.

The independent per-point reading has the same value on a sphere. At fixed projected radius
`rho`, disc membership bounds `(a - C.xz) dot (q - C.xz)` below by
`(A^2 + rho^2 - r^2) / 2`, while height is `C.Y + sqrt(R^2 - rho^2)`.
For `A <= r` the apex attains the global height bound. Otherwise put `rho0=A-r`,
`v0=sqrt(R^2-rho0^2)` and `p0=v0-r*rho0/v0`. The derivative of the tangent upper bound has
the sign of `rho^2-A^2+r^2`. If `p0 <= 0`, `R^2 <= rho0^2+r*rho0`, so that derivative is
negative throughout the upper domain. If `p0 > 0`, height can exceed p0 only until
`rho^2 < R^2-p0^2 = rho0^2+2*r*rho0-r^2*rho0^2/v0^2`.
The derivative is still negative there. Everywhere else height is at most p0.
Thus no point improves the nearest-point contribution. Zero-normal tangency has an empty reading.

For the exact decimal diagnostic `C = (0, -1/4, 0)`, `R = 1/4`, `a = (7/20, 0)`, `r = 1/5`,
the footprint is `(3/20, -1/20, 0)`, normal `(3/5, 4/5, 0)`, tangent and contribution `-1/5`.
There is no axis role. The installed binary32 query is a separate row and is evaluated from
its exact represented inputs. Its narrow enclosure must not pretend those inputs are 7/20 and 1/5.

## CapsuleEndsAndBarrel certificate

For `H = 0`, use Sphere. Otherwise split the boundary into spheres centred at `C +/- H*d`
restricted respectively by `n dot d >= 0` and `n dot d <= 0`, and the finite barrel
`C + t*d + R*n`, `-H <= t <= H`, `n dot d = 0`, `|n| = 1`.
The seams belong to both descriptions but publish one raw part.

On an end, offer Sphere's nearest point only when its hemisphere inequality holds. If this
point fails the hemisphere inequality, any constrained end maximum must be on the seam.
This follows by moving toward the spherical apex inside the disc until either the unconstrained
maximum or the hemisphere boundary is reached. The seam is exactly the barrel at `t = +/-H`.
CylinderSide below includes it. The same argument applies to a walkable end, since its additional
restriction is a concentric projected circle. If the nearest full-sphere point is steep, that
sphere has no walkable intersection at all. Consequently the two offered end points and the
barrel maximum cover both unrestricted and walkable maxima, without independently solving
a clipped spherical patch or assuming that its centre-nearest point is always on that patch.

Upright capsules use the upper end. At radial tangency, their barrel's upper seam has normal Y
zero. Lying capsules have a horizontal crest segment and spherical continuations beyond its
endpoints. Nearest projection onto the finite core segment gives their unrestricted maximum.
Tilted capsules use the finite barrel solve and the two hemisphere-checked spherical candidates.
When a barrel maximum moves to an end, the position and normal agree at the seam.

The axis candidates are the upper quadratic intersections with each sphere, checked against
its hemisphere, plus the finite barrel axis intersections below. Choose highest Y, retaining
seam equality. A normal-Y-zero axis point never divides a tangent by zero.

For raw admission, distance to the finite core segment is continuous on connected `V`.
The capsule surface meets it iff its minimum squared distance is at most `R^2` and its maximum
is at least `R^2`. The minimum is the minimum over `t in [-H,H]` of distance squared from
`C+t*d` to the solid column. Split at its two Y clamp points and projected circle crossings.
The exterior branch is a quadratic plus a clamped linear square minus
`2*rV*sqrt(projectedQuadratic)`. Its derivative gives a quartic after one squaring.
Check the original derivative sign, clamp conditions and all endpoints.
Distance squared to a segment is convex, so a maximum on the solid column is on a circular
rim at `b` or `s`. On each core-clamp branch, half-angle parametrization makes it a rational
function with numerator degree at most four and denominator `(1+v^2)^2`. Its stationary
numerator has degree at most five. Include core-clamp crossings and the omitted point `v = infinity`.
These finite lists also cover a column wholly inside the capsule.

## CylinderSide certificate

Changing `d` to `-d` leaves the side unchanged. Orient it with `k = d.Y >= 0` and put
`u = sqrt(d.X^2 + d.Z^2)`. First handle `u = 0` exactly. The projected side is a circle, not a
filled disc. It meets `D` iff `max(A-r,0) <= R <= A+r`. Its highest point is at the upper end
of the finite axis segment. Choose the nearest admitted circle point. Normal Y is zero,
so there is no support or walkable region. If the axis lies on this circle, its highest endpoint
is also an attained axis role. Raw membership additionally intersects the finite Y interval
with `V`. Do not drop this context part because it cannot support.

For `u > 0`, define orthonormal directions
`e = (d.X/u, 0, d.Z/u)`, `f = (-e.Z, 0, e.X)` and `w = (-k*e.X, u, -k*e.Z)`.
Write `P = (C.xz-a) dot e`, `Q = (C.xz-a) dot f`. The entire upper side is parametrized by

```text
-1 <= z <= 1
v = sqrt(1-z^2)
n = v*w + z*f
x = C + t*d + R*n
E = sqrt(r^2 - (Q+R*z)^2)
t0 = (R*k*v-P)/u
tLow  = max(-H, t0-E/u)
tHigh = min( H, t0+E/u)
```

Feasibility is `E^2 >= 0` and `tLow <= tHigh`. This is an exact projection-domain test.
For `k > 0`, Y increases with t, so choose `tHigh`. For `k = 0`, choose the feasible t nearest
the axis. The resulting height is the minimum of

```text
Fend  = C.Y + k*H + R*u*v
Fdisc = C.Y - k*P/u + (R*v+k*E)/u
```

For the horizontal case `k=0`, let `delta=max(|P|-H,0)`. If `delta > r` the side misses D.
Otherwise its available lateral radius is `sqrt(r^2-delta^2)` and the nearest lateral offset
is `max(|Q|-sqrt(r^2-delta^2),0)`. This includes an axis beyond the finite log end.
Height and normal Y decrease with this offset, and the tangent at a decreases away from its
nearest feasible value. Therefore no walkable replacement is needed and the geometric and
per-point contributions agree on a horizontal side. The t coordinate chooses an attained
point in the finite strip, never an extension beyond an end.

The walkable solve adds `u*v >= c`. If `c > u` it is empty, with equality retained when `c = u`.
The unrestricted maximum can be steep while this region is nonempty. Therefore the horizontal
nearest-line shortcut must not be applied to a tilted finite side.

Enumerate the two range endpoints, slope endpoints, the end/disc crossings at `t = +/-H`,
`z = 0`, and stationary points of the disc branch. At an endpoint t the crossing polynomial is

```text
A = (P+u*t)^2 + Q^2 + R^2*k^2 - r^2
B = 2*R*Q
J = R^2*u^2
K = 2*R*k*(P+u*t)
(A+B*z+J*z^2)^2 - K^2*(1-z^2) = 0
```

It has degree at most four. Reject extraneous roots with the original unsquared equality.
The disc branch derivative is zero only when
`z*E + k*(Q+R*z)*v = 0`. Squaring gives another quartic.
Again retain only the original sign and feasibility. These candidates, plus the branch crossings,
cover every endpoint and every interior maximum. A repeated root or isolated feasible point is
retained. An identically zero crossing means coincident branches, not infinitely many candidates.
Use either identical height branch and its endpoints and stationary strata.
There are at most 18 scalar candidates per solve: eight end crossings, four stationary roots,
two range endpoints, two slope endpoints, zero and a deterministic flat-stratum point.

The axis substitution `x.xz = a` gives a quadratic in `y-C.Y`. Its coefficients are
`u^2`, `-2*k*((a-C.xz) dot d.xz)` and
`|a-C.xz|^2 - ((a-C.xz) dot d.xz)^2 - R^2`.
Choose the upper root, require `|t| <= H` and `n.Y >= 0`. Include a repeated root.

For raw admission, use both signs of v and replace r by rV. Intersect the feasible t interval
with the Y band. For `k > 0` its additional bounds are
`(b-C.Y-R*u*v)/k` and `(s-C.Y-R*u*v)/k`.
Feasibility changes only when one lower bound meets one upper bound. Three of the nine pairs
are ordered by construction. The other six are equalities between an end, disc or Y bound.
Each equality reduces to degree at most four after removing radicals.
There are at most six pairs and 24 roots per sign, plus two range endpoints. Evaluate all
cuts and one interior point of every intervening interval. No narrow interval can be skipped.
For `k = 0`, Y bounds give explicit v cuts. For `u = 0`, use the circle test already proved.

## CylinderCaps certificate

Solve each cap independently with centre `K = C +/- H*d`, normal `n = +/-d` and radius R.
The cap is the closed planar disc `n dot (x-K) = 0`, `|x-K|^2 <= R^2`.
When `n.Y > 0`, its XZ projection is a closed ellipse. Its Y is affine there, so its maximum
over `D` is at the cap's uphill rim point, the foot disc's uphill point inside the ellipse,
or an intersection of their boundaries. Include a feasible interior point for a constant plane.
The axis role is the plane intersection above a, if inside the cap. Walkability is constant
over the cap. A steep cap cannot gain a walkable replacement. Equality is walkable.

In a cap-plane orthonormal basis `(w,f)`, substitute
`x = K + R*(w*(1-v^2)+f*2*v)/(1+v^2)` into the foot circle.
The resulting polynomial has degree at most four. Keep every real root and the omitted rim
point at infinity, check actual disc and cap inequalities, then compare heights exactly.
Coincident boundaries have constant or sinusoidal height on their common rim. Use its uphill
point or a fixed attained point if constant. This handles the identically zero polynomial.
There are at most eight candidates including the two uphill points and a flat interior point.
Cap/side ties at the rim keep their independent normals and raw IDs.

For membership only, reverse a downward cap normal without moving K, compute min and max Y
over its projected disc intersection, and test overlap with `[b,s]`. A nonempty intersection
is convex, so its height image is an interval. A vertical cap uses its projected line segment:
intersect that segment with the foot circle, then choose the transverse coordinate nearest
the cap centre. The available Y interval is `K.Y +/- sqrt(R^2-transverse^2)`.
A vertical or downward cap has no positive-normal support. It remains a raw member when in V.
A vertical cap still has an attained upper boundary on its projected line segment. Its footprint
uses the transverse coordinate nearest the cap centre in that intersection. An axis on the projected
line gets the corresponding upper circular point. Both roles retain the cap's zero-Y normal and
publish no tangent support. This distinguishes an attained geometric boundary from an empty reading.

## TiltedPose certificate and finite arithmetic

The preceding formulas exhaust `u=0`, `k=0`, and `u*k>0`. They include signed axes, inverted
caps, zero-length capsules and arbitrarily near-degenerate orientations. There is no tilt cutoff.
End restrictions and finite side bounds must be tested before selecting a witness.

Finite arithmetic certifies enclosures of the installed source, rather than exact algebraic
records or exact ordering of irrational candidate heights. The geometric domains, attainment,
hemisphere restrictions and witness obligations above are unchanged. Selection downstream compares
enclosure midpoints. Inclusive band overlap and walkability consume the certified bounds, with
walkability established by the normal's Y-component lower bound. `SupportFootprintWitness.UpwardLower`
and `SupportFootprintMember.UpwardLower` carry that bound independently of the scalar normal error.
Source-backed or walkable-boundary certificates preserve exact equality with the request cosine.
The scalar normal error continues to enclose the complete vector for tangent arithmetic.
Released `SupportElement` and neighborhood classification keep their existing contracts.
Arithmetic error never expands reach or D.

Closed-form strata are spheres, upright and horizontal capsule ends and barrels, upright and
horizontal finite cylinder sides, and their horizontal or vertical caps. Evaluate their rational
operations and positive square roots outward. Keep the shared unnormalized installed Y column
beside the released normalized enclosure. It defines one source for normalization identities,
projected dot products, end restrictions, seams and band equalities. Do not substitute component
midpoints for that source.

Every sign expressible as a polynomial in the installed source uses Task 4's
`SupportSourcePolynomialWorkspace` when outward bounds do not decide it. Clear positive norm
denominators and square radical comparisons only after proving the original sign guards. These
closed-form predicates have degree at most eight. Equal source monomials combine before their
shared coefficient box is evaluated. Preserve the workspace's degree-eight, nine-variable and
64-expression bounds and its zero warm allocation. Equality remains a closed stratum, including
slope equality and an authored top exactly at a proposal band boundary.

Tilted finite sides and tilted capsule barrels have no closed-form maximum in this implementation.
Certify them by interval branch-and-bound over the compact part parameter domain. Each cell bounds
membership, its upper-surface height and normal, and the walkable restriction when required.
Retain cells whose bounds permit a feasible point or tied maximum. Proved infeasible cells and
cells whose height upper bound is below an attained lower bound may be discarded. Retain seam,
end, repeated-root and isolated-point strata. A rounded sample is an attained lower bound only
after its membership is certified. An interval overlap never proves absence.

Ruling RO replaces scalar-cell exhaustion with certified region termination. A terminal cell
has one of three certificates. Its objective upper bound is below the best attained lower bound,
so it cannot contain a maximum. Its objective is monotone for every installed source in the box,
so its maximum belongs to separately retained boundary candidates. Or it encloses a stationary
region for every source, with existence and coverage proved by an interval Newton or Krawczyk
certificate, or uniform opposite derivative signs at its ends with the necessary uniqueness
proof. Retain that stationary region whole. Its value, position and normal enclosures bound
the attained source-dependent witnesses without enumerating their scalar descendants.

Every retained region must prove existence, feasibility and coverage. Partition at boundary
crossings before applying a branch's derivative certificate. Include end, seam, repeated-root,
isolated-point and tied strata. Do not assume the tilted projected domain is convex. Monotonicity
on one feasible interval cannot discard another interval. Candidate ties retain and enclose
all possible maximizing strata until classification and attained-point selection are settled.

If a region certificate cannot be proved, subdivide it at an interior binary64 parameter and
continue certification. Preprovision a static bound on live and terminal cells. Refuse the whole
query, with untouched output spans, when that storage bound cannot carry the remaining coverage
proof. No epsilon, iteration cap, sampled maximum, incomplete support or virtual probe replaces
a failed certificate. Publish retained regions' enclosure widths as height and witness errors.
Source uncertainty remains in those errors. No exact irrational ordering solver, field norm,
resultant or Sturm-chain scratch is required.

The finite subdivision bound does not grant a cost, stack or scratch exception. The tilted owner
must account for its reusable cell storage and traversal stack. Closed-form owners likewise
account for their bounded candidate and source-expression storage. Task 9 measures cold bytes,
retained scratch, stack and warm cost under SG-12 and SG-20.

For each witness, enclose Y and `T_x(a)` from the same source and prove positive normal Y before
division. Downstream contribution remains their enclosed minimum. A proposal-guard sign that is
still undecided after the source-polynomial fallback refuses atomically. Keep every admitted pose
covered by the tilted owner. Task 6b supplies the region certificates below. Neither a virtual
probe nor incomplete support is a fallback.

### Task 6b region implementation

`SupportTiltedCurvedGeometry` orients the same unnormalized installed column upward when the
source box certifies a weak Y sign. A box crossing zero retains signed Y. The finite
side keeps the full parameter range, the original disc endpoints and the closed slope endpoints.
The positive normalizing length preserves a source's weak Y sign exactly. Intersect the
normalized component enclosure with that certified half-line before choosing an axial branch
or publishing a cap's component bound. Scalar normal error never supplies this certificate.
The stationary equation is solved only between zero and `-Q/R`. The unsquared derivative requires
z and `Q+R*z` to have opposite signs, so the squared equation's other roots cannot be stationary
points of the original branch. The displayed strictly negative second derivative proves uniqueness
of a disc-branch stationary maximum on its smooth domain. Source-uniform endpoint signs and a
nonzero derivative enclosure certify existence and coverage of each retained root region.

When Y includes zero, the disc branch uses `|k|` and its unsquared stationary function is
`z/sqrt(1-z*z) + |k|*(Q+R*z)/sqrt(disc*disc-(Q+R*z)^2)`. Its derivative is strictly positive
on the smooth domain, including k=0. A uniform existence bracket therefore covers every source's
unique stationary point. If no such bracket covers the common domain, retain the union of the
source-dependent domain tails in the existing bounded root owner. Signed Y selects the corresponding
upper or lower finite axial endpoint. A box crossing zero retains their hull, including the tied
horizontal crest. No source box is dropped because its Y sign is indefinite.

At c=1 a walkable normal is vertical. On a capsule it is a global solid-height maximum.
On a finite side it requires k=0 and is a global horizontal crest. On a cap it requires
a horizontal plane and all cap points share its height. Consequently any such point in D
already attains the unrestricted disc maximum. No distinct walkable replacement is needed
at this exact gate, including a source box where only some sources have a vertical normal.
Retain the original witness and its component enclosure without asserting Y=1 for other sources.
A cap also needs no distinct replacement at any c because its normal is constant. If its
geometric maximum is steep, every cap point is steep. If any cap point is walkable, its
geometric maximum is walkable too. Source-polynomial slope certificates may refine the
cap's upward component, including exact equality. An undecided certificate leaves its
original enclosure intact because the selected geometric witness remains valid for every source.
A selected barrel crest has the side's largest normal Y, u. A steep crest implies that the
whole side is steep. On a capsule any spherical end point with normal Y greater than u belongs
to the higher core end and exceeds the side's global height bound. It would already have replaced
the barrel in the unrestricted maximum. Therefore a retained barrel crest also needs no distinct
walkable replacement. Candidate hulls retain this certificate only when every retained candidate
is a crest. Other source families crossing the side's slope-availability boundary retain the
original quadratic `u*u*z*z + c*c - u*u` in the bounded root owner. A family with source-dependent
walkable existence refuses at the storage bound instead of failing before a coverage attempt.

Caps keep their raw signed normals. Their rim candidates do not divide by Y. A cap whose Y includes
zero offers its whole-circle crest when original disc membership is certified, or retains the full
possible vertical interval until a boundary candidate proves an attained lower bound. Axis roots
at finite ends and vertical caps use their original linear equations under the same bounded root
rules when a closed source predicate remains uncertain. A cap whose upper-role existence changes
across the source box takes full surface coverage with the upward predicate included. Uncertain
feasible cells remain live and refuse at the storage bound instead of publishing an uncertified role.

End and cap crossings use the original function `A+B*z+J*z*z-K*sqrt(1-z*z)`, with the rim sign
included in K. This avoids manufacturing a pair of almost coincident roots by squaring a small K.
Interval Newton contracts a certified region without removing any source's root. If a derivative
is unbounded at a pole, a one-sided derivative sign and continuity still prove monotonicity.
Certified midpoint signs can contract such a region before Newton becomes available. Every
polynomial sign which remains uncertain takes the existing source workspace, with positive
denominators and the original radical sign guards retained.

The objective's attained lower bound prunes an entire finite end when even its unconstrained
height upper bound is smaller. When Q is exactly zero and z=0 is certainly feasible, both height
branches decrease away from zero for every source. That point is a certified global maximum
even when the projected feasible set has multiple components. Otherwise the candidate list
retains every end crossing, stationary region, disc endpoint and slope endpoint. A possible
candidate cannot establish the attained lower bound. All candidates which can attain or tie it
remain in the published hull. The original maximum and the walkable replacement are separate.
The member's maximum bounds the unrestricted height. The footprint role carries the replacement
when the certified walkable region is nonempty, retaining an exact normal Y at the slope boundary.

CF-3 retains a signed source identity for each original disc-lateral endpoint. For sign s equal
to -1 or +1, the candidate is created only by `z=(s*disc-Q)/R` and retains `Q+R*z=s*disc`.
Its original quotient enclosure is not clipped into the circle domain. Since R is positive,
source signs of `s*disc-Q+R` and `R-(s*disc-Q)` certify `-1 <= z <= 1` for every represented
source. A strictly outside root is absent. An undecided domain does not establish attainment.
The defining equation proves `disc*disc-(Q+R*z)^2=0`, hence transverse extent is exactly zero.
Reconstructing the quotient with independent intervals and subtracting two enclosures of the
same squared quantity would lose this identity and cannot be used as an absence proof.

For a proved endpoint, evaluate `v=sqrt(1-z*z)` through source arithmetic. Its only possible
axial coordinate is `t=(R*k*v-P)/u`, with positive u. Source signs of
`R*k*v-P+u*H` and `u*H-(R*k*v-P)` certify the two finite-axis bounds. The original source
normal is `w*v+f*z`, with Y equal to `u*v`. A source sign of `u*v-c` certifies slope, including
equality. Only the joint domain, finite-axis and slope certificate makes this endpoint attained.
The certificate preserves the tiny signed installed Y and original normal enclosures. It supplies
the maximum's lower bound while possible stationary regions retain the enclosing upper bound.
It grants no attainment to a merely possible stationary region, clipped root or nearby pole.
Unproved relevant predicates still refuse atomically within the existing cell and candidate bounds.

Caps enumerate their crest, the disc uphill point and both signed rim crossings. Capsule ends
keep the sphere's nearest-point solve and its original hemisphere predicate. The finite barrel
covers the seams. Axis parameters clear the common normalization denominator before testing the
finite ends, rather than expanding two dependent normalized quotients.

Raw admission covers both side semicircles, each full cap disc and both capsule hemispheres.
Two-dimensional parameter cells enclose complete surfaces. Only outward absence bounds discard
a cell. An attained point with certified disc, surface and Y-band membership proves presence.
Uncertain source predicates retain their cells and take the source-polynomial fallback.

Proposal heights solve the exact distance-to-part objective along the lower sphere centre.
The finite side uses axial-end distance and distance from the circular radius, a cap uses its
plane distance and exterior radial distance, and a capsule uses distance to its finite core
with the probe radius added. These have three clamp branches with at most four scalar roots
per branch, hence at most twelve retained contact roots. The highest root is the first downward
contact. Subtracting the existing probe radius gives the body's lowest position. The proof
interval extends one additional probe radius beyond the raw surface envelope. Its endpoints
are at least two probe radii from the surface and have strictly positive objectives. This is
proof slack, not a new probe, a reach extension or a geometric error threshold. A source-backed
band equality is tested before any undecided proposal boundary is refused.

The root owner preprovisions two 1024-cell frontiers and twelve terminal records. Boundary
polynomials retain at most four regions, while contact functions retain at most twelve.
The admission owner preprovisions two more 1024-cell frontiers. Each parameter cut is an interior
binary64 point in a finite interval. Thus subdivision is finite without a refinement count.
An unproved certificate refuses before a frontier would exceed its fixed storage. Processed
absence and monotonicity certificates stream out instead of occupying terminal storage.
The simultaneous cell storage bound is 4096 live records plus twelve retained root regions.
The side candidate bound is eighteen, and the cap candidate bound is fourteen in this enumeration.
Their common preprovisioned 32-record array therefore covers both. Neither cell storage nor
candidate storage grows during a query. Source expressions retain the existing 4096-node arena
and degree-eight, nine-variable, 64-polynomial workspace. Warm reuse allocates zero bytes.

## Independent readings and oracle strata

The per-point reading is diagnostic and never selects the geometric witness. On a side its
tangent is independent of t, so for each z the same highest feasible t suffices. Optimize the
minimum of that height and
`C.Y-k*P/u+(R+Q*z)/(u*sqrt(1-z^2))`.
In addition to the geometric candidates, include stationary tangent points and height/tangent
crossings, filtering original signs. On a cap the tangent is constant. On each capsule end use
the hemisphere restriction. A certified enclosure may bound a supremum without pretending an
open positive-normal domain has an attained maximum.

For the exact rational diagnostic `h=-(x-1)^2-100*z^2`, its geometric point `(1/5,0)` contributes
`-24/25`. The point `(9/50,3/100)` contributes `-1097/1250`.
Writing `w=100*z^2`, the two readings coincide when `w=x-x^2`. The disc imposes
`99*x^2+x-4 <= 0`. Thus the independent supremum is
`(-1+sqrt(1585))/198-1`, achieved at equality with the disc. It exceeds both listed contributions.
This is a counterexample to identifying the per-point reading with SG-19, not to SG-19 itself.

The test oracle pins exact decimal and installed-float sphere readings, upright and zero-length
capsules, horizontal barrel and end strata, tilted crest and hemisphere ties, both signed cylinder
caps, axis misses, circle tangencies, a highest-walkable tilted side, and compound composition.
Hidden sphere, capsule and cylinder parts are evaluated independently under an installed wedge.
All compared closed-form quantities use exact rational bounds and integer-square-root enclosures.
The numerical per-point oracle reports a separate enclosure, including an empty reading for
parts with no positive-normal point. No backend production code is changed by Task 5.
