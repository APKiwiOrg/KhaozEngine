# Bounded geometry arithmetic proof record

Status: primitive and supplied-matrix arithmetic only. This does not prove installed-pose correspondence,
finite-face membership, a complete capsule feature query or a swept path. The first 14 primitives passed
tests, format and shared source review. The supplied-matrix extension passed 22 tests including those
14 regressions, with scoped format and shared arithmetic review passing. Supplied-pose arithmetic
passed 30 cases and scoped format. Its shared review is pending. Later gates remain open.

## Represented inputs and default

`GeometryInterval` encloses real numbers between two finite binary64 endpoints. `Exact(x)` denotes
the real number represented by a finite input double, not a claim about an earlier physical
measurement. Default has `IsResolved == false`, so its stored zeros have no mathematical meaning.
Non-finite, reversed or unresolved inputs cannot produce a resolved operation.

## Primitive arithmetic

Addition and subtraction take the monotone endpoint combinations. Multiplication and division take
all four endpoint combinations. Division refuses an interval containing zero. Each scalar operation
is a separate binary64 operation. The predecessor and successor of its rounded result bound the
exact real result under IEEE binary64 arithmetic, including underflow. No expression combines a
multiply and add into an assumed fused operation. Overflow or a non-finite outward endpoint refuses.
This proof does not enable an alternate fast-math mode or apply to decimal arithmetic.

For products and quotients, taking the smallest rounded corner's predecessor and the largest rounded
corner's successor encloses every real corner even if rounding makes two corners tie. Continuity
and monotonicity on a zero-free denominator rectangle then enclose the interior. Squaring is separate
because its minimum is exactly zero when the interval spans zero. Otherwise its extrema occur at the
endpoints. The square lower bound can be intersected with the known nonnegative range without an
empirical epsilon.

## Exact product comparison

Every finite binary64 value has the exact representation `mantissa * 2^exponent`. The mantissa uses
the 52 stored fraction bits plus the implicit leading bit for normal values. The exponent is -1074
for subnormals and `encodedExponent - 1075` for normal values. Signed zero is canonicalized.

Each product has at most 106 mantissa bits. Product comparison aligns exponents and compares signed
integers exactly. Before shifting, the helper checks the resulting operand bit lengths against 4096.
An over-budget comparison returns `Unresolved`, not zero or a guessed sign. The two mantissa products
are already bounded before that check. No input-dependent unbounded refinement loop exists.

## Exact squared-distance comparison

`CompareSquaredDistances` compares two sums of squared differences of supplied binary64 coordinates.
Each distance has one to three coordinates. The two distances may have different dimensions, but each
endpoint pair must have matching lengths. Invalid lengths, non-finite coordinates or a bit-budget
refusal return `Unresolved`.

Every subtraction aligns the exact signed dyadic operands. Before either shift or addition, the
helper reserves the possible carry within the existing 4096-bit operand ceiling. Squaring first
checks twice the difference mantissa's bit length, then forms the exact integer product. At most
three such squares are added with the same bounded alignment rule. Final comparison aligns the two
nonnegative sums using the existing exact comparison. No floating-point subtraction, multiplication
or summation determines the published sign. Conservative budget refusal is permitted even when later
cancellation could make the result small.

The eight fixed cases include a positive term lost by ordinary binary64 summation, a subtraction
whose subnormal difference is lost by ordinary rounding, exact tangency, translation and symmetry,
invalid inputs and over-budget refusal. The expected signs use separately stated dyadic identities.
This proves only the sign for the supplied represented coordinates. It does not turn uncertain
transformed coordinates into exact geometry, authenticate installed shapes, or certify a contact.

## Square roots

`Math.Sqrt` only proposes candidate endpoints. A negative input interval refuses. A positive lower
candidate is moved one representable value down and an upper candidate one up. Zero stays zero.
Before returning, exact product comparison verifies `lower * lower <= inputLower` and
`upper * upper >= inputUpper`, with nonnegative endpoints. If either comparison is unresolved or
fails, the operation refuses. Thus a particular platform's observed square-root rounding accuracy
is not used as the certificate. Monotonicity of square root supplies the enclosed interval once
those exact inequalities hold.

## Supplied represented matrix arithmetic

`EncloseSingleRounding` includes both its input interval and the predecessor/successor around the
binary32 encodings of its endpoints. Monotonic rounding then encloses every possible encoded value
inside the input interval. Non-finite casts or outward endpoints refuse. This deliberately keeps the
real expression and its rounded evaluation in one enclosure, including cancellation and underflow.

`RepresentedGeometryTransforms` follows the row-basis products and left-associated additions in
[BepuUtilities 2.4.0 Matrix3x3.Transform](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuUtilities/Matrix3x3.cs#L179).
Every product/addition includes its binary32 rounding enclosure. Point translation is handled the
same way. The unrounded value remains enclosed at each stage, so eliminating an intermediate rounding
does not invalidate the enclosure. This does not permit arbitrary expression reassociation.

The helper accepts a supplied matrix as represented data. It does not prove that the matrix came from
the installed body's pose, validate quaternion normalization, prove rigidity or certify a normal
transform. Direction transformation is not an inverse-transpose normal transform. The scalar primitive
argument covers each arithmetic composition, but provenance and geometric eligibility remain outside
this helper. Any unresolved component refuses the whole vector. Finite bounds may be too wide for the
feature error ceilings and must then be refused by that caller.

Eight fixed tests cover identity/translation, a row-basis quarter turn, cancellation that separates
the real and binary32 results, large-frame spacing, zero-translation directions, non-finite input and
overflow. Closed-form values and the pinned backend's evaluation are separate assertions. A passing
backend comparison alone is not the real-arithmetic proof.

## Supplied pose arithmetic

`PosePoint` encloses the multiplication/addition polynomial used by the pinned backend's
[CreateFromQuaternion](https://github.com/bepu/bepuphysics2/blob/v2.4.0/BepuUtilities/Matrix3x3.cs#L277),
including binary32 rounding after each operation. It uses the supplied quaternion without normalizing
it. Its represented squared norm must be enclosed wholly within `1 +/- 2^-20`. Non-finite values,
position components beyond 2048 m and local components beyond 64 m refuse before evaluation.

The same interval composition argument covers coefficients, row-basis products and translation.
This proves arithmetic for the supplied pose data, not that the pose belongs to a current installed
body. It also does not establish output-domain or error-ceiling acceptance. A resolved enclosure may
still be too wide or lie outside the feature domain and must be refused by that caller.

Eight additional fixed cases cover exact identity/half-turn/cyclic rotations, a general represented
unit rotation, invalid rotations, the unit-band boundary, input-domain boundaries and opposite
quaternion signs. Thirty tests passed with the earlier arithmetic regressions. Installed geometry,
normal reconstruction and the complete feature query remain separate proof gates.

## Finite checks and remaining gates

The primitive tests use independent rational comparisons against known nonbinary sums, products and
quotients, cancellation hidden by ordinary multiplication, subnormal roots, zero, overflow and
refusal propagation. They do not use production interval operations to construct expected values.
The first RED is a missing `GeometryInterval` compiler diagnostic, not observed numerical failures.

This slice has no installed-body/pose correspondence, normal reconstruction, finite polygon
classifier, complete candidate enumeration, cache or selected-view implementation. It does not
establish the spec's 0.25 mm position, 0.1 mm separation-width or 0.00001 normal-error ceilings for
any geometric query. Those require the later operation-level composition proof and independent
geometric tests. Swimming may share reviewed arithmetic, but retains its own coverage and bracket
proof. Passing these primitive tests cannot authorize either movement consumer.
