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

All scalar branch polynomials have degree at most five. Coefficients lie in the real algebraic
field generated by the exact installed Y-column length and its XZ length. This field has degree
at most four over the rationals. Keep their positive root selections, not decimal approximations.
Isolate roots over that selected field, or take the field norm polynomial, of degree at most 20,
isolate its real roots, and filter conjugate and squaring roots by the original equations.
Root isolation over an algebraic extension by this reduction is described by
[Strzebonski and Tsigaridas](https://arxiv.org/abs/1101.4369).

The field norm must be taken after an identically zero polynomial has been removed. Use
square-free factors and exact endpoint signs. This retains repeated and tangent roots without
requiring a sign change. Root records contain an integer polynomial, a root index and a rational
isolating interval. Retain algebraic records for exact equality, comparisons and tie handling.
Refining until two interval midpoints differ is not a terminating comparison algorithm.

A concrete finite bound follows from the actual norm polynomial. Let its degree be N, integer
height be M, and `W = 1+M` a root bound. Its square-free discriminant is a nonzero integer.
Every other root separation is at most `2*W`. The product formula for the discriminant therefore
gives the deliberately conservative bound
`delta = M^(-(N-1)) * (2*W)^(-(N*(N-1)/2-1))` for any distinct-root separation when `N >= 2`.
Degree one is solved directly. Sturm counts and bisection isolate all roots after at most
`ceil(log2(4*W/delta))+2` levels. This is a coefficient-derived finite bound, not an accuracy stop.
The discriminant argument and degree/height separation principle are also discussed by
[Bugeaud and Mignotte](https://irma.math.unistra.fr/~bugeaud/travaux/PolSurvRev1.pdf).

Process one polynomial at a time. At most N root intervals are live, with an iterative
depth-first stack bounded by the preceding level count. A Sturm chain has at most N+1
polynomials and `(N+1)^2` coefficients. Fraction-free subresultants are determinants of
matrices of order at most 2*N. If original coefficient bit length is B, Hadamard gives
`2*N*(B+ceil(log2(2*N))+2)` bits per such coefficient. Dyadic endpoint numerators need at most
`B+levels+3` bits. These bounds determine finite reusable scratch from the input coefficients.
Binary32 poses and bounded sizes give a finite B without a new geometric threshold.
Candidate tables need at most 18 entries for a side solve and eight for a cap solve. Raw side
admission needs at most 26 cuts and 25 interior samples per sign. Admission's end-point distance
solve needs at most five stationary roots per clamp branch. Tables are reused per raw member.
This proves termination and finite storage. Task 6 must choose and account for its concrete
limb representation. Task 9 still measures cold bytes, retained scratch, stack and warm cost.
These conservative bounds grant no exception to those gates.

Candidate comparisons have their own finite bound. Including the two remaining positive roots
v and E gives algebraic degree at most 80 per candidate over the selected source field extension.
The difference of two candidate heights has degree at most 6400 over the rationals, by the
degree bound for their compositum. Form its defining resultant, remove zero factors and use
the same separation and Sturm bounds with its actual coefficient height. Exact zero is an
equality certificate, not a refinement loop. This larger conservative scratch is reused for
one comparison at a time. Endpoint sign evaluation needs at most the Sturm coefficient bound
plus `2*N*(B+levels+3)+ceil(log2(N+1))` bits. These symbolic bounds establish termination
even at exact ties. They are not a recommendation to allocate the worst-case arrays on every Find.

For a witness contribution, compare and enclose Y and `T_x(a)` using the same algebraic source
record. Certify the minimum, normal gate, membership and band inequalities by exact signs when
outward intervals overlap. Float publication encloses the attained point and contribution.
The record may remain algebraic while the published bounds are finite binary64 or binary32.
Neither reach nor the disc radius is expanded to hide a rounding error.

Task 4's `SupportSourcePolynomialWorkspace` combines equal source monomials before evaluating
their shared coefficient box. Reuse that correlation for normalized-axis identities, dot products,
quartic coefficients, seam and band equalities. Clear positive norm denominators before asking
for a sign. A normalization square root is an algebraic variable satisfying its defining quadratic.
Do not replace it with independent component errors. The existing degree-eight, nine-variable,
64-expression workspace is a bounded source-expression owner, not a univariate root solver.
Extend with a separately bounded algebraic root owner where needed. Preserve that workspace's
bounds and zero warm allocation. If the installed-map enclosure contains different feasible
strata, retain and enclose all of them. An interval overlap alone never proves absence or permits
discarding an admitted pose. Any genuine publication or scope refusal keeps SG-1 and SG-7's
existing atomic semantics.

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
