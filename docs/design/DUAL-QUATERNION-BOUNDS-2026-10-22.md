# Ordinary rigid DQ bounds and the rounded denominator

S2.6 proof for algorithm version 1. This is an ordinary model/world enclosure, not an NC2,
refinement, conformation, no-clipping or continuous-carry certificate. The reference graph is
`DualQuaternionPalette` and `DualQuaternionSkinning`, through S2.5 at `5c03837a7`.

## Contract and arithmetic

`DqBoundsSource(vertices, boneCount)` validates the entire DQ source domain through
`SkinningValidation.ValidateSource`, computes an outward binary64 maximum source-position norm,
and retains only immutable radius/count metadata. It never retains the vertex span. Recreate it
when any source words change. A default value is unvalidated and refuses admission.

`TryBoundModel(in source, palette, out bounds)` scans joints, not vertices. The palette must have
exactly the validated source's joint count, at most 128. Each real and dual word must be finite.
The outward exact squared norm of each real part must lie in `[1-sigma, 1+sigma]` below. Section 2
proves that preparation satisfies this interval. An arbitrary constructed quaternion that fails
this check has no normalization lemma and refuses. No tolerance is added to source validation.

`TryBoundWorld(in modelBounds, in world, out worldBounds)` accepts finite ordered model corners
and a finite exact affine last column `(0,0,0,1)`. Scale, shear, reflection and collapsed axes are
allowed here. It transforms all eight corners with outward binary64 intervals. It also encloses
binary32 affine evaluation error, independently of the existing maximum-axis sphere shortcut.

All results describe the exact stored input words and the pinned scalar graph. `fl` denotes a
binary32 round-to-nearest operation with separate products and sums. Use these exact constants:

```text
u       = 2^-24
eta     = 2^-126
sigma   = 64u = 2^-18
Smin    = 1-32u = 1-2^-19
Smax    = 1+32u = 1+2^-19
nLower  = 1/8
gamma_k = ku/(1-ku)
```

The absolute `eta` allowance is deliberately larger than half a subnormal ulp. For every finite,
nonoverflowing operation, including an underflowed or flushed result,

```text
|fl(z)-z| <= u |z| + eta.
```

This does not authorize changing input words, flushing operands before multiplication, reassociation
of the pinned DQ graph, or approximate reciprocals/roots. Square roots and reciprocals of the
normal, bounded quantities used below have the usual relative error at most `u`. NC2's separate
read/flush/division/root contract is still a stage 4 obligation.

For a four-product, three-add left-associated dot, expansion of each operation's error gives

```text
|fl_dot4(a,b)-dot(a,b)|
    <= gamma_4 sum_j |a_j b_j| + 7 eta (1+u)^3.                 (1)
```

There are at most four relative factors along a product-to-root path and at most three subsequent
additions propagating an absolute error. The geometric-product bound
`(1+u)^k-1 <= gamma_k` proves (1), including cancellation. The corresponding dot3 expression has
`gamma_3` and `5 eta (1+u)^2`. Neither inequality divides by a possibly cancelled dot.

The proof below uses `gamma_3 < 4u`, `gamma_4 < 5u` and
`7(1+u)^3 < 8`. All are strict rational inequalities at the specified `u`.

## 1. The admitted rounded weight total

Let `t=fl(fl(fl(wX+wY)+wZ)+wW)` and `S=sum(w_i)` in exact arithmetic. The source validator requires
finite nonnegative words in all slots, including valid indices in zero-weight slots. A total below
`1e-8f` returns the source position before the blend. For the other admitted path,

```text
1-16u <= t <= 1+16u.
```

The subtraction from one in validation is exact in this neighborhood by Sterbenz's lemma. Values
outside the neighborhood cannot pass a rounded residual of magnitude at most `2^-20=16u`.
Nonnegative partial sums cannot hide an overflow or a larger positive weight by cancellation.
The three additions give

```text
(1-u)^3 S - 3 eta (1+u)^2 <= t
    <= (1+u)^3 S + 3 eta (1+u)^2.

(1+u)^3 (1-32u) + 3 eta (1+u)^2 < 1-16u,
(1-u)^3 (1+32u) - 3 eta (1+u)^2 > 1+16u.
```

Consequently `Smin < S < Smax < 2`. The weak bounds `Smin <= S <= Smax` used subsequently cover
both admitted endpoint words. No exact normalization of the weights is assumed. Each prefix sum
and each weight is at most `Smax`.

## 2. Prepared real-quaternion norm and preparation branches

Preparation validates the composed matrix, not its raw factors. Each rounded Gram diagonal is
within `2^-16` of one. Its terms are nonnegative squared words. Thus each of the nine matrix basis
words has absolute value less than two. A word of magnitude at least two would contribute a rounded
square of at least four, which cannot pass that diagonal check. Nonfinite Gram values refuse.

Write `cW,cX,cY,cZ` for the exact four candidate expressions before rounding. Their exact sum is
four. Each rounded candidate has absolute error less than

```text
7 gamma_3 + 3 eta (1+u)^2 < sigma.
```

The strict greatest-candidate selection therefore chooses a rounded candidate `c` satisfying
`1-sigma <= c < 8`. This covers every W/X/Y/Z tie and both sides of each comparison. No assumption
that the chosen branch equals an ideal matrix extraction branch is required. In particular,
`1/2 < c < 8`, the rounded `h=sqrt(c)` lies between `1/2` and four, and the selected raw component
`h/2` has magnitude at least `1/4`. Each of the other raw components has magnitude below 16.
The latter loose bound follows from a sum/difference of two words below two and `s=fl(0.5/h) <= 1`.
All these operations are normal and finite except potentially insignificant small components.

Let `v` be these four raw extracted words and `s0=||v||^2` exactly. Then

```text
1/16 <= s0 < 1024.
(1-8u)s0 <= shat <= (1+8u)s0,
```

where `shat` is the pinned rounded dot of `v` with itself. Equation (1) proves the second line
because `gamma_4 + 112 eta (1+u)^3 < 8u`. Thus root and reciprocal normalization are finite,
normal and bounded away from zero.

Let `a=fl(1/fl(sqrt(shat)))` and `z=a sqrt(s0)`. Root and reciprocal errors give

```text
(1-u)/((1+u)sqrt(1+8u)) <= z <= (1+u)/((1-u)sqrt(1-8u)).
```

Both inequalities `1-8u < z < 1+8u` follow without decimal approximations by squaring positive
sides and checking these rational inequalities:

```text
(1-u)^2 > (1-8u)^2 (1+u)^2 (1+8u),
(1+u)^2 < (1+8u)^2 (1-u)^2 (1-8u).
```

The four final products obey `||fl(av)-av|| <= u z + 2 eta`, so

```text
1-16u < ||q|| < 1+16u,
1-sigma < ||q||^2 < 1+sigma,                                 (2)
```

since `(1+16u)^2 < 1+64u` and `(1-16u)^2 > 1-64u`. The exact identity bypass has squared norm one.
Canonical sign changes are exact negations, so (2) survives all W/X/Y/Z sign branches and zeros.
The runtime binary64 norm interval is much narrower than the slack between these inequalities and
the checked endpoints. With binary64 unit roundoff `2^-53`, its products and four-term sums widen
each endpoint by less than `2^-48`, while (2) leaves more than `16u` of endpoint slack. It certifies
actual words rather than trusting their construction history.

For completeness, a prepared dual component's error relative to its exact written expression
`0.5*(t,0)*q` is at most

```text
0.5 gamma_3 sum_of_its_three |t_j q_k| + 4 eta.
```

This follows from its three products, two ordered additions/subtractions and final half multiply.
Overflow in preparation refuses. The bounds scan uses the actual prepared dual words and outward
norms, so it already includes this error without assuming exact Study orthogonality or reconstructing
translation from the input matrix. Positive determinant and the Gram tolerance remain preparation
admission conditions. They are not substituted for the normalized-word proof (2).

## 3. Rounded blend and hemisphere decisions

Use only positive slots in stored X/Y/Z/W order, without merging duplicate indices. Let `R_i` be
the actual rounded real prefix and `Q=max ||q_i|| < 1+sigma < 2`. For any prefix of at most four slots,
the norm triangle inequality and the per-component rounding model give

```text
||R_i|| <= (1+u)^5 Q Smax + 16 eta (1+u)^4 < 2.              (3)
```

One step uses a product followed by an add. Its vector absolute rounding allowance is at most
`(4+2u)eta`. Propagating it through four additions yields at most `16 eta (1+u)^4`.
The weighted product factors are at most `(1+u)^5`. This establishes (3) inductively, without
assuming favorable hemisphere decisions. All real products, sums and dots are far from overflow.

By (1), (2), (3) and Cauchy-Schwarz, each rounded decision dot differs from its exact dot by less than

```text
gamma_4 Q ||R_i|| + 7 eta (1+u)^3
    < 4 gamma_4 + 7 eta (1+u)^3 < sigma.
```

Let `q'_i` denote the real input after the actual decision. Whether the rounded dot is positive,
negative, either signed zero, or straddles an exact zero, its decision guarantees

```text
dot(R_i,q'_i) >= -sigma.                                    (4)
```

A negative rounded dot negates the exact dot. A nonnegative rounded dot retains it. In either
case a contrary exact sign has magnitude below the dot-error bound. Flipping both halves does not
change either norm. This handles hemisphere ties and cancellation, rather than assuming exact
hemisphere selection.

For one actual real step, write

```text
R_(i+1) = R_i + w_i q'_i + e_i.

||e_i|| <= u ||R_i|| + (2u+u^2) w_i Q + (4+2u)eta
          < 2u + 4(2u+u^2) + (4+2u)eta < sigma.              (5)
```

This treats the product error and subsequent sum error separately. In particular, it does not
replace the rounded prefix by an exact weighted sum. Since `||R_i+w_i q'_i|| < 6`, expanding the
squared norm and dropping the nonnegative `||e_i||^2` gives

```text
||R_(i+1)||^2
    >= ||R_i||^2 + (1-sigma) w_i^2 - 2 w_i sigma - 12 sigma.
```

Sum over at most four slots and apply `sum(w_i^2) >= S^2/4`:

```text
N = ||R_final||^2
  >= (1-sigma) Smin^2/4 - 2 Smax sigma - 48 sigma
   > 1/4 - 64 sigma = 0.249755859375.                        (6)
```

For a direct symbolic check, `(1-sigma)Smin^2/4 > 1/4-sigma/2`, `Smax<2`, and
`52.5 sigma < 64 sigma` imply the strict final line. No exact-arithmetic `1/4` bound is asserted
for the rounded blend.

The implemented `n` is another four-product, three-add dot of the actual rounded `R_final`.
By (1) and (3), `|n-N| < 4 gamma_4 + 7 eta (1+u)^3 < sigma`. Therefore

```text
n > 1/4 - 65 sigma = 0.249752044677734375 > 1/8.             (7)
```

Equations (6) and (7) prove the proposed internal floor for the entire admitted weighted path,
including both weight-total endpoints, four equal weights, every prepared extraction/sign branch,
all rounded hemisphere branches and subnormal contributions. Unweighted and exact-identity paths
return source words before forming `n`. They do not assert a positive norm for an all-zero blend.
Neither the evaluator graph nor its existing tiny-arithmetic guard is changed by this task.

## 4. Dual sum and the rational position graph

Let `dmax` be the outward maximum exact norm of the prepared dual words. The same recurrence as (3),
without any sign assumption, proves

```text
||D_i|| <= D = (1+sigma) dmax + 64 eta.                       (8)
```

Specifically `(1+u)^5 Smax < 1+sigma` and `16 eta (1+u)^4 < 64 eta`.
When all dual words are zero, all their products and sums are exactly signed zeros, so use `D=0`.
This includes admitted nonunit rounded weight totals and rounded ordered dual additions.

Let `A(R)` be the exact unnormalized Hamilton rotation matrix and `Ahat` the written binary32
matrix. Its exact row norm is `N`, each coefficient has magnitude at most `N<4`, and
`||A(R)p||=N||p||`. For a diagonal coefficient, (1) bounds the square/add errors by
`gamma_4 N + 7 eta (1+u)^3 < sigma`. For an off-diagonal coefficient, the two products, sum or
difference and exact power-of-two doubling give error at most

```text
(2u+u^2)N + (7+4u)eta < sigma.
```

Thus every coefficient error is below `sigma`. For a source with outward radius `r`, the
coefficient errors contribute at most `sqrt(3)sigma r < 2 sigma r` per row. The row dot3 has
absolute product sum at most `sqrt(3)(N+sigma)r < 9r`. Its rounding error is less than
`sigma r + 8 eta`. Consequently the total per-component rotation-numerator error is bounded by

```text
Er = 3 sigma r + 8 eta.                                    (9)
```

Every row partial sum, including rounding, has magnitude at most `9r+8 eta`. This is also an
overflow guard, not only a final-result estimate.

The exact translation numerator is `T=2 vector(Dsum*conjugate(R))`. Quaternion norm multiplicativity
gives `||T|| <= 2 ||Dsum|| sqrt(N)`. The written four products and three signed adds have total
absolute product sum at most `||Dsum|| ||R|| < 2D`. Doubling gives per-component error at most

```text
Et = sigma D + 32 eta.                                    (10)
```

For example its sharper dot-error bound is `4 gamma_4 D + (14(1+u)^3+1)eta`, which is below (10).
The largest translation partial sum or doubled result is bounded by `4D+Et`. The bounds function
requires both this magnitude and `9r+8eta` to be at most `float.MaxValue`. This also keeps all dual
weighted products/adds finite, because their prefix/product magnitudes are at most `2D`.

Write `alpha=fl(1/n)`. The normal reciprocal satisfies

```text
alpha <= amax = (1+u)/nLower,
N/n <= b = 1+sigma/nLower,
N alpha <= b(1+u).
```

The exact rigid rotation has radius `r`. The ideal normalized translation is bounded by
`2D/sqrt(N) <= 2D/sqrt(nLower)`. Define `rnd(x)=(1+u)x+eta` for nonnegative magnitudes. Including the
separate numerator errors, reciprocal rounding, the two separate scalar output products, and
only then their final addition, use

```text
rotated    = rnd(r b(1+u) + Er amax),
translated = rnd((2D/sqrt(nLower)) b(1+u) + Et amax),
extent     = rnd(rotated + translated).                    (11)
```

This is the implemented envelope. Each posed position component is in `[-extent,extent]`.
The factor `b` is necessary because the numerator's exact `N` and rounded denominator `n` differ.
The ideal rotation-radius and `2 norm(Dsum)/sqrt(nLower)` terms alone would omit these errors.
Exact identity and unweighted source positions are enclosed too, since `extent >= r`.

All nonnegative calculations in (8) to (11) round binary64 upward after every primitive. The root
in the translation denominator rounds downward. Overflow, nonfinite arithmetic or an invalid
real-norm interval refuses admission. There is no rest-sphere or LBS influence-union substitution.

## 5. Affine world intervals and the returned MeshBounds sphere

For a world axis, the exact affine form is `x*a+y*b+z*c+t`. Its extrema over a box occur at its
corners, also with negative coefficients or shear. Compute each corner with downward/upward
binary64 products and sums. Let

```text
M = max|x| |a| + max|y| |b| + max|z| |c| + |t|,
Eworld = sigma M + 8 eta.
```

Equation (1) bounds any binary32 four-term affine evaluation, with or without individually fused
products, by `Eworld` about the exact result. The factor is independent of the corner and covers
interior points too. Expand every corner interval by this error before taking axis extrema.
Require `M+Eworld <= float.MaxValue`, so cancelling huge intermediates cannot hide overflow.
No maximum-axis sphere scaling is used.

`MeshBounds` computes its own binary32 `Center` and `Radius`. Finite corners alone do not guarantee
a finite or outward sphere. The candidate expands the target intervals on all faces by
`max(2^-60, sigma*maximum_absolute_endpoint)`, all outward. This is a construction choice, not a
proof that a candidate must succeed. Convert each endpoint to binary32, stepping one representable
word outward if the nearest conversion was inward. Nonfinite conversions refuse.

After construction, inspect the actual stored sphere and certify with outward binary64 arithmetic

```text
sum_axis max(center_axis - targetLow_axis,
             targetHigh_axis - center_axis)^2 <= radius^2.
```

The left side rounds upward and the right side downward. It encloses the farthest target-box corner
from the actual rounded center, hence every point in the target box. A nonfinite center/radius or
failed comparison refuses. No assumption about the direction of `MeshBounds`' own rounding is used.
The empty-source case has no vertices and may return `MeshBounds.Empty` after palette validation.

This representation can refuse very large but finite deformation, for example when the existing
sphere constructor's squared diagonal overflows. Huge representable translations below that limit
are enclosed. A refusal is explicit admission failure, not an infinite sphere or a fallback. The
source and palette domains, `1/8` floor and pinned deformation graph are unchanged.

## Verification and scope

`DqBoundsTests` exercises the LBS-union escape, independent binary64 Hamilton posed vertices and
the actual scalar evaluator, exact and near identity, unweighted bypass, all-zero and subnormal
weights, both total endpoints, four equal orthogonal inputs, hemisphere flips and ties, a decision
that rounds an exact negative dot to zero, huge translations,
prepared Gram-boundary input, invalid metadata/palettes, affine shear/reflection/collapse, final
outward conversion and nonfinite/projective/overflow refusal. Checking every independently posed
vertex component also checks the extrema of the independently enumerated posed-vertex box.
The tests are an O(vertices) independent control. They are not the domain proof.

Every constant comparison above is an inequality of exact dyadic rationals, after the two displayed
positive squarings for normalization. Substituting `u=1/16777216` and `eta=1/2^126` proves them by
integer multiplication. A scratch `fractions.Fraction` check verified all 18 grouped comparisons,
without sampled inputs, during implementation. Equations (1) to (11), not that check or the finite
corpus, establish the enclosure over the full admitted rounded graph.

The outward stepping primitives use the documented [.NET Math.BitIncrement](https://learn.microsoft.com/en-us/dotnet/api/system.math.bitincrement?view=net-10.0),
[Math.BitDecrement](https://learn.microsoft.com/en-us/dotnet/api/system.math.bitdecrement?view=net-10.0),
and [MathF.BitIncrement](https://learn.microsoft.com/en-us/dotnet/api/system.mathf.bitincrement?view=net-10.0)
adjacent-value semantics. Binary64 products of input binary32 words are exact before widening.
Longer sums, norms, divisions and roots use the stated outward interval arithmetic.

Stage 3 still has to connect this admission result to every ordinary presentation/culling path
and prove its own native implementation. Stage 4 still needs its own precise graph, refinement
rounding, conformation recession and carry bounds. This ordinary result grants none of those.
