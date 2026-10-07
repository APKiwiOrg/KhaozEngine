# Bounded geometry arithmetic proof record

Status: first primitive slice only. This does not prove transforms, finite-face membership, a complete
capsule feature query or a swept path. Fourteen primitive tests and scoped format passed. Shared
source/proof review is pending. Later gates remain open in the approved feature plan.

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

## Square roots

`Math.Sqrt` only proposes candidate endpoints. A negative input interval refuses. A positive lower
candidate is moved one representable value down and an upper candidate one up. Zero stays zero.
Before returning, exact product comparison verifies `lower * lower <= inputLower` and
`upper * upper >= inputUpper`, with nonnegative endpoints. If either comparison is unresolved or
fails, the operation refuses. Thus a particular platform's observed square-root rounding accuracy
is not used as the certificate. Monotonicity of square root supplies the enclosed interval once
those exact inequalities hold.

## Finite checks and remaining gates

The primitive tests use independent rational comparisons against known nonbinary sums, products and
quotients, cancellation hidden by ordinary multiplication, subnormal roots, zero, overflow and
refusal propagation. They do not use production interval operations to construct expected values.
The first RED is a missing `GeometryInterval` compiler diagnostic, not observed numerical failures.

This slice has no represented quaternion/matrix enclosure, normal reconstruction, finite polygon
classifier, complete candidate enumeration, cache or selected-view implementation. It does not
establish the spec's 0.25 mm position, 0.1 mm separation-width or 0.00001 normal-error ceilings for
any geometric query. Those require the later operation-level composition proof and independent
geometric tests. Swimming may share reviewed arithmetic, but retains its own coverage and bracket
proof. Passing these primitive tests cannot authorize either movement consumer.
