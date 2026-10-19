# Equipment coverage GPU arithmetic proof

This records Part C Tasks 1 to 3 against sections 3 to 9 of the
[kernel contract](EQUIPMENT-COVERAGE-KERNEL-CONTRACT-2026-10-18.md).
It covers the scalar inventory, shared vector graphs, exact integers, corrected division and square
root, exact predicates, B30 crossings and support queries. Complete construction equivalence belongs to later tasks.

## Evidence status

The Task 1 fix-round-1 local Release build passed with zero warnings and zero errors. All 103 focused tests passed,
including three native facts on MetalNative with an Apple M2 Max. The coverage fact compared all
13,949 words from 377 dynamic rows exactly. Scoped whitespace and SPIR-V validation passed.
The first hosted run, 39165413387, exposed Windows CRLF helper comparisons and WARP dynamic
component-selection poisoning. Fix round 1 addresses both below. Hosted run 39169835980 on commit
4f2bf49bb then passed every leg with no skipped facts. The run covered MetalNative (113 of 113),
Direct3D11 WARP with FXC (113 of 113, plus 32 FXC and struct-size checks) and Vulkan lavapipe (113 of
113). Its Vulkan synchronization-validation selections of 1, 7 and 493 tests passed with no VUID or
hazard lines.
Task 2's first hosted run, 39173376064 on 7d402576c, passed Vulkan but failed FXC compilation
and Apple Paravirtual pipeline creation. Its C6 correction below passes a zero-warning local
Release build and all 114 focused Metal tests. Hosted run 39177040590 on 3886609c2 then passed
every leg with no skipped facts: Apple Paravirtual 114 of 114, Direct3D11 WARP with FXC 114 of 114
plus 32 FXC and struct-size checks, and Vulkan lavapipe 114 of 114 with clean synchronization
validation. On Apple Paravirtual the pipelines reported maxTotalThreadsPerThreadgroup 576 for
CoverageExactWords and 1024 for CoverageResidual and CoverageRounding, each with
threadExecutionWidth 32.
No timing, load, performance budget or whole-coverage correctness claim follows from this record.
The 72 frozen cases and their 144-row `outputs.sha256` manifest are unchanged.

## Dynamic word probe

`CoverageGpuArithmeticProbe` uploads a row count followed by 25 words per row. Every numerical
operand comes from storage, including both vectors, all 16 matrix fields, clamp bounds and the
separate multiply/add control's addend. The count is an integer header, avoiding an unbound Metal
buffer-size helper. The shader writes 37 words per row into poisoned storage.

The table contains a 19 by 19 finite/zero/flush-boundary grid, six nonfinite operand rows and ten
literal vector guard cases. The guard cases cover all-equal vectors, each individual component
mismatch, signed-zero equality, each component's NaN guard, inclusive flushing and next-normal
survival. Every returned uint is compared with `CoverageReferenceArithmetic` and
`CoverageReferenceMath`, without a tolerance, canonicalization or coverage-side sign repair.
Independent literal assertions also pin the required boundary decisions and vector guard results.
Matrix inputs use 16 explicit scalar fields and all vector outputs use named components. The probe
does not dynamically insert matrix fields or extract vector components. In row 18, `cross.z` is
negative infinity while `cross.x` must retain `bfffffff`. This is the hosted WARP regression case.

| Observation | Required word or decision |
| --- | --- |
| F of negative smallest normal `80800000` | `80000000` |
| F of positive smallest normal `00800000` | `00000000` |
| F of next normal `00800001` | `00800001` |
| Min of positive and negative zero | `80000000` |
| Max of positive and negative zero | `00000000` |
| Clamp negative zero with positive-zero lower bound | `80000000` |
| `(3f800001 * 3f7ffffe) + bf800000`, separate operations | `00000000` |
| Finite of NaN | false |
| NaN not equal to a finite value | true |

The multiply/add control would produce `a8800000` if contracted. It therefore distinguishes
separate RN32 operations from a single fused operation. Generic precise-maths tests separately
exercise dynamic signed-zero products, negative underflow to zero, zero addition/subtraction,
NaN propagation and signed infinities. Their NaN checks classify exponent and mantissa as the
IEEE contract permits. The coverage probe itself compares all words exactly.

## Scalar lowering and F

`CoverageGpuArithmeticGlsl.Source` owns the shared GLSL inventory. Add, subtract and multiply each
flush both operands, compute one authored `precise float`, then flush the result. Abs and negation
use integer sign masks. Min/max explicitly propagate NaN and use sign OR/AND at zero ties. Clamp
uses comparisons and selects. Inequality negates ordered equality, so a NaN operand remains unequal.
Finite tests use the exponent mask. Reads and explicit field stores use the same F helper.

The emitted SPIR-V has exactly three scalar floating arithmetic instructions in the inventory.
The declaration pass in Task 1A adds no IDs and does not change these result IDs.

| Helper | SPIR-V instruction | Result | Decoration |
| --- | --- | --- | --- |
| covAdd | OpFAdd (129) | `%161` | NoContraction (42) |
| covSub | OpFSub (131) | `%176` | NoContraction (42) |
| covMul | OpFMul (133) | `%191` | NoContraction (42) |

`CoverageArithmeticSpirvAudit` checks every arithmetic result and the operand/result F call counts
inside each scalar helper. F contains two OpBitwiseAnd instructions, an OpULessThanEqual at the
inclusive `00800000` threshold, and no floating arithmetic. Removing source `precise` qualifiers
is a negative control that exposes missing NoContraction sites in the compiled module.

Dot2, Dot3, cross, subtraction, point and linear transforms call only these scalar helpers in the
reference graph's order. Mat4 is a field container. There is no native matrix multiply, vector dot,
reassociation or tree sum. Equal3 and finite3 retain scalar short-circuit component order.

## HLSL and Direct3D11

The emitted helper intermediates are `precise float _161 = a + b`,
`precise float _176 = a - b` and `precise float _191 = a * b`. Explicit F calls remain around
operands and results. The headless checks inspect the emitted shader and pin the compute
`D3DCOMPILE_IEEE_STRICTNESS` flag to `00002000`.

The native compute fact creates its shader through the real factory with PreciseMath enabled, so
the hosted Windows leg compiles with FXC and executes the same dynamic probe on WARP. Run 39169835980
recorded exact Direct3D11 word equality there.

C5 prohibits `OpVectorExtractDynamic` and `OpVectorInsertDynamic` in every coverage kernel and
probe. It also prohibits non-constant vector or matrix component indices in `OpAccessChain` and
`OpInBoundsAccessChain` on Function or Private storage. FXC may implement dynamic component selection
with arithmetic, so an unselected infinity or NaN can poison the selected finite lane even under
IEEE strictness. Use named components, literal matrix indices or scalar arrays.

`CoverageSpirvAccessAudit` walks the compiled pointer and composite types, including array and
structure steps and chained pointer results. Dynamic array indices remain allowed when every
vector or matrix component step is constant. `ShaderSourceValidationTests.CoverageComputeKernels`
applies it to every module in the coverage-kernel corpus, and the scalar inventory audit shares it.
Negative controls cover Function and Private vectors and matrices, nested arrays, in-bounds access
chains and both forbidden vector instructions. A retained patch-file control plants dynamic access
in the actual probe and must fail the corpus validation before the exact patch is reversed.

## MSL and Metal

PreciseMath enables SPIRV-Cross invariant floating math and disables Metal fast math on the compute
library. The emitted scalar helpers are single-operation implementations:

```cpp
spvFAdd(l, r) -> fma(T(1), l, r)
spvFSub(l, r) -> fma(T(-1), r, l)
spvFMul(l, r) -> fma(l, r, T(-0.0))
```

A helper's single-operation fma is distinct from contracting separate source operations.
The explicit inventory F calls remain between multiply and add. Native words prove the zero-sign
and separate-rounding decisions on Metal rather than inferring them from those helper names.

Task 1A backports every multiplication-helper zero seed in pinned SPIRV-Cross 2.23.0. The recogniser
accepts only a complete pinned helper or its corrected body, and rejects unfamiliar definitions.
It scans every `spvFMul`-prefixed identifier independently of declaration punctuation. Outside an
accepted definition, calls to accepted helpers are recognised in every expression position, including
nested calls, function arguments, arithmetic operands, ternaries and parentheses. A preceding
identifier other than `return`, a preceding `#`, or a function body after the matching call parentheses
prevents call recognition. Comment-separated declarations, aliases, macros and unknown identifiers
fail compilation. Comments and string literals are not identifiers. Both the pinned constants and
emitted helpers are compared with normalized line endings, while replacements preserve source line endings.
CRLF-checkout controls exercise CRLF pinned constants against LF, CRLF and CR emitted helpers.
It preserves the pinned MatrixMatrix dimensions. The separate rectangular dimension defect and
backport removal are tracked in [#1495](https://github.com/APKiwiOrg/KhaozEngine/issues/1495).
The precise cache identity includes the revised IEEE-preservation and MSL lowering policy.

## SPIR-V and Vulkan

The common precise module declares capability SignedZeroInfNanPreserve (4466), extension
`SPV_KHR_float_controls` for pinned SPIR-V 1.0, and execution mode
`SignedZeroInfNanPreserve 32` (4461) on its entry point. The validated declaration pass leaves the
original instruction graph, IDs and NoContraction decorations intact. Both HLSL and MSL emit from
that same module. Shaderc optimization remains Zero.

The Vulkan backend queries `shaderSignedZeroInfNanPreserveFloat32` through
VkPhysicalDeviceFloatControlsProperties and reports `GpuCapabilities.SupportsShaderFloatControls`.
This capability is Vulkan-specific. Float controls are core in Vulkan 1.2, below this backend's
Vulkan 1.3 floor, and have no device feature enable bit. Precise shader creation refuses a missing
property through ShaderValidationException before native module creation. The unsupported-device
factory regression covers both compute and vertex shader requests.

Run 39169835980 recorded exact native Vulkan word equality on lavapipe and clean validation-layer
results. Local SPIR-V validation alone would not have substituted for executing that backend.

## Reproduction and remaining gates

Run a Release build, then these focused checks through build-slot. Local native execution uses
`KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native`.

```bash
build-slot --label coverage-arithmetic -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~PreciseShaderCompileTests|FullyQualifiedName~ComputePreciseMathGpuTests|FullyQualifiedName~CoverageGpuArithmeticTests|FullyQualifiedName~CoverageComputeArithmeticGpuTests|FullyQualifiedName~ShaderSourceValidationTests' --logger 'console;verbosity=normal'
```

Set `KE_SHADER_CORPUS_DUMP` to an absolute artifact directory to retain the probe's `.spirv`, `.hlsl`
and `.msl` files. `spirv-dis --raw-id` exposes the IDs above. Validate the precise probe with
`spirv-val --target-env vulkan1.3`. The compiler corpus includes all five CoverageArithmetic target
and layout rows. No existing shipped-program hash is rebaked to accept a numerical change.

Root's hosted filter is the same five-class expression above. It includes the generic
exceptional-value rows and the module-wide coverage audit.
Require zero-error Vulkan validation logs and no skipped native facts. Run 39169835980 met both on
all three legs.

## Task 2 integer representation and RN32 conversion

`CoverageGpuExactWordsGlsl` defines `CovExact` as 18 little-endian uint limbs and a separate
sign. Signed add, subtract, product, comparison and bit length operate on these integers alone.
Finite binary32 words decode to a signed integer times `2^-149`. A maximum finite coordinate
uses 277 bits, a coordinate difference fits below `2^278`, and a two-product determinant and its
sum fit below `2^557`. The 576-bit representation therefore covers the admitted predicate domain.
Checked add and diagnostic product overloads expose overflow to the native probe. Production
composition uses `WorkerSource`, which omits the general schoolbook product entirely.

Under C6 the C# generator emits limb operations with literal indices. There is no dynamic
private-array read or write. A requested word is selected through 18 constant-index branches.
`CoverageGpuWideDiagnosticGlsl` supplies the general multiply only to the independent diagnostic
probe. Its explicitly rolled outer and inner loops share one 32-bit product site. A constant-index
store selector writes each destination. For each nonzero outer limb it records the outgoing carry
and any nonzero multiplier limbs beyond the retained window as overflow. All terms are nonnegative,
so discarded terms cannot cancel. Within the window each row adds its partial product and carry
to the prior rows rather than overwriting a carry slot. The arithmetic and checked full-width
result are unchanged. The compiled SPIR-V audit requires constant private-array indices for
all exact-word probes.

The wide `CovExact` interface stays in Task 2 for Task 3, with its existing 576-bit storage and
`covRound32` scale parameter. Its fixed accesses meet C6 without coupling rounding to determinant
storage. Task 3 must prove its own operation bounds and use this layer only where those bounds
require it. Division and square root never include or instantiate it. The shared 32-bit product
definition has an include guard, so wide predicates and compact rounding compose in one shader.

The multiply primitive uses four 16 by 16 partial products and explicit unsigned carry comparisons.
It deliberately avoids `umulExtended`, `uaddCarry`, float64 and int64, so Shader Model 5.0 does not
need a 64-bit intermediate or an extended-integer intrinsic lowering. In the partial-product
construction each cross product plus its incoming 16-bit carry fits uint. Each schoolbook
accumulation is at most `(2^32-1)^2 + 2*(2^32-1) = 2^64-1`, so the computed high word and two carry
comparisons also fit uint. The tests exercise all-ones carry chains, borrow chains, signed
cancellation, asymmetric partial products and products reaching limb 17. No baseline or size
exemption changes are needed.

`covRound32(integer, powerOfTwo)` converts the exact integer times `2^powerOfTwo`, before F.
For nonzero magnitude with bit length L, let E be `L-1+powerOfTwo`. The rounding quantum is
`2^max(E-23,-149)`. The helper keeps the integer above that quantum and reads the exact guard
bit and sticky bits below it. It increments for an above-midpoint value, or an exact midpoint
whose retained integer is odd. Carry propagation handles the next binade, including overflow
to infinity. The same rule handles subnormal results, the zero midpoint and a carry from the
largest subnormal to the smallest normal. Values below the half-minimum-subnormal interval
return signed zero. No exact helper flushes a residual component.

Literal RN-even checks include `(2^24+1)*2^-24 -> 3f800000` and
`(2^24+3)*2^-24 -> 3f800002`. Multi-limb midpoint cases distinguish an exact tie from the
same magnitude plus a sticky bit in limb zero. Signed zero, binade carry, subnormal ties,
the finite overflow midpoint and the value immediately below it are also covered.

## Task 2 exact residuals and search bound

`CoverageGpuRoundingGlsl` applies ruling C3 on every backend. `covDivRn32` and `covSqrtRn32`
flush their operands and compute a native seed. Their seed-taking helpers handle signed zeros,
NaNs, infinities and invalid operations before entering a finite search. NaN inputs retain
their sign and payload with the quiet bit set. Invalid operations return `ffc00000`.
Division's result sign is the operand-sign XOR, including underflow and infinity. Square root
preserves negative zero after operand F and rejects negative nonzero operands.

### C6 compact residual bound

`CoverageGpuResidualGlsl` represents a nonnegative magnitude as two uint words and an explicit
integer power of two. Decode a positive finite word with encoded exponent E as `s*2^e`.
For E=0, s is the fraction and e=-149. Otherwise s includes the hidden bit and e=E-150.
Thus every operand significand has at most 24 bits. Signs, zeros and nonfinite operands resolve
before any finite residual work. Division searches positive magnitudes and restores the sign XOR.

For any finite positive ordinal lower word, including zero, its boundary midpoint is exactly
`(2*s+1)*2^(e-1)`. Within a binade its successor is `(s+1)*2^e`. At the last subnormal the
smallest normal has the same quantum. At a normal binade end the successor is `2^24*2^e`,
so the same expression holds. At maximum finite the virtual successor is `2^128`, giving
`(2^25-1)*2^103 = 2^128-2^103`. Infinity itself is handled before constructing a midpoint.
The midpoint numerator therefore has at most 25 bits, even at overflow.

Division compares `sA*2^eA` with `(sB*midpointNumerator)*2^(eB+midpointExponent)`.
Its product is strictly below `2^49`. Square root compares `sA*2^eA` with
`midpointNumerator^2 * 2^(2*midpointExponent)`, whose product is strictly below `2^50`.
The shared 32 by 32 partial-product primitive produces both exact words. No float residual,
shader int64, shader float64, flush or wide determinant operation participates.

For nonzero integers U and V with bit lengths L and K, compare the leading powers `eU+L`
and `eV+K` first. If they differ, their magnitude intervals are disjoint and decide the result
without alignment. This includes arbitrarily separated input exponents. If the leading powers
agree, alignment to the smaller exponent would occupy exactly max(L,K) bits, at most 50.
The implementation instead shifts each significand left to bit 63. Each shift is `64-L` or
`64-K` and loses no bit. Equal leading powers make the two normalized integers share one scale.
Unsigned high-word then low-word comparison is therefore exact. Zeros resolve before the shifts.
The two-word representation is sufficient and is tighter than C6's four-word upper bound.

All product and normalization code is straight-line and uses explicit scalar fields. The
rounding shader contains no `CovExact`, limb array or limb loop. This removes the large private
state previously copied through every midpoint search. Native Metal probes record each compiled
pipeline's `maxTotalThreadsPerThreadgroup` and `threadExecutionWidth` before dispatch.

At an exact midpoint the boundary belongs to the even endpoint word. This defines a monotone
predicate for whether RN32 is at or below an ordinal word. Both boundaries of the native
candidate are checked first. A confirmed candidate is returned unchanged. Otherwise an ordinal
bracket expands by powers of two, saturated at zero and `7f800000`. The search records a lower
word whose boundary is below the selected word and an upper word whose boundary includes it.
It then bisects until the ordinal words are adjacent. The entire positive word domain spans
less than `2^31`, so no search needs more than 31 bisections. Expansion also terminates at an
endpoint within 31 doublings. This is a domain proof, independent of native accuracy or divisor
range. No uncorrected native result is published. F is applied only after the RN32 word is selected.

| Backend | Correction and lowering obligation |
| --- | --- |
| Metal | Native divide and sqrt are seeds under PreciseMath with fast math off. The exact integer midpoint check confirms or corrects them. The full-domain bracket proof applies even if the native seed is an endpoint. Local native execution checks pre-F and post-F words, including injected bad seeds. |
| Direct3D11 | The same residual scaling, even-midpoint predicate and 31-bisection bound apply under FXC IEEE strictness. There is no one-neighbor assumption. Partial products use uint only. New modules pass the C5 SPIR-V access audit and emit HLSL for Shader Model 5.0. Run 39177040590 compiled it with FXC and matched every word on WARP. |
| Vulkan | The same exact proof applies, including divisors outside the native accuracy interval and sqrt with no assumed ULP bound. PreciseMath retains SignedZeroInfNanPreserve. New modules pass SPIR-V validation. Run 39177040590 matched every word on lavapipe with zero-error synchronization validation. |

## Task 2 native evidence and reproduction

`CoverageBinary32IntegerOracle` is test-only BigInteger arithmetic. It uses a 600-bit fixed-point
rational quotient with a remainder sticky bit for division and integer Newton square root at
300 fractional bits for roots. These resolutions are below every relevant binary32 midpoint.
It then rounds with an independent quotient/remainder implementation. It does not call production
exact-word helpers or use floating arithmetic. Literal checks and the fixed corpus also compare
its final F words against `CoverageReferenceArithmetic.Div` and `Sqrt`. Invalid reference results
are checked by NaN class, since section 3 admits no NaN payload and requires the same nonfinite
guard. ARM64 .NET invalid arithmetic can return `7fc00000` where the deterministic integer
oracle selects `ffc00000`. The device probe still compares every oracle word exactly, including
NaNs. No accepted finite or infinity result is classified or canonicalized.

`CoverageExactWords`, `CoverageResidual` and `CoverageRounding` are registered in the coverage
shader corpus. Their SPIR-V passes C5 and C6 access audits. Storage-driven native probes poison outputs before
dispatch and compare uint words exactly. The limb probe checks every limb, sign, overflow,
comparison, bit length and RN32 word. The rounding probe checks RN32 before F, the final F word
and the production helper's final word independently. It checks each bisection count is at most 31.
For every fixed input it exercises the actual native candidate plus injected zero, infinity,
previous-word and next-word seeds, replacing only the approximate candidate. The corpus includes
the specified tiny division results, wide divisor exponents, overflow, both zero signs and signed
quiet/signaling NaN, infinity and zero divisor classes.

The initial red test failed because the planned exact-word and rounding GLSL types were absent.
The C6 red test rejected a non-constant private-array access in the original wide module.
After the C6 correction, the required six-class selection passed all 114 tests on MetalNative
with Apple M2 Max, with no failures or skips. It checked 38 integer rows, 29 compact residual
rows and all 315 rounding rows, with zero limb overflows and a maximum of 30 bisections.
The compact rows cover zero, disjoint leading powers, the 32-bit boundary, 49/50-bit products
and equal leading powers differing by one low bit, all against independent BigInteger values.
The composition test also compiles the wide and compact helpers together, catching duplicate
shared definitions before Task 3 consumes both layers.
The emitted SPIR-V retains a
NoContraction OpFDiv and a GLSL.std.450 Sqrt. Type-width checks reject any float or integer type
wider than 32 bits. Text comparisons normalize line endings for Windows checkouts.
Metal reports maximum threads per group of 576 for the wide probe and 1,024 for each compact
probe, with execution width 32 for all three. Emitted HLSL has only literal wide limb indices,
no wide helper loops and no wide type in either compact module. This addresses the observed
FXC source pattern. It does not establish native FXC compilation or Paravirtual pipeline creation.
This is a bounded correctness proof,
with no timing, load, stress, performance-budget or complete-construction claim.

```bash
build-slot --label c02-fix-r1b-final-build -- dotnet build KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release
build-slot --label c02-fix-r1b-final-metal -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~CoverageGpuExactWordsTests|FullyQualifiedName~CoverageComputeRoundingGpuTests|FullyQualifiedName~CoverageComputeArithmeticGpuTests|FullyQualifiedName~CoverageGpuArithmeticTests|FullyQualifiedName~ShaderSourceValidationTests|FullyQualifiedName~PreciseShaderCompileTests' --logger 'console;verbosity=detailed'
```

The root hosted filter is the same six-class expression. Run 39177040590 executed it on all three
hosted legs for the C6 commit. Shader compilation and local Metal results alone would not have
substituted for those backend executions.


## Task 3 determinant bounds and conversion

`CoverageGpuExactJobsGlsl`, composed by `CoverageGpuPredicatesGlsl`, expands each determinant
into signed products of original XY coordinates. It flushes each coordinate before decoding it. Z never participates in a predicate.
A finite coordinate in integer units of `2^-149` has magnitude strictly below `2^277`.
An exact difference is therefore below `2^278`, a difference product below `2^556`, and
an edge or two-dimensional dot determinant below `2^557`. The expansion also has a safe
partial-sum bound. Edge has six products and dot has eight, each strictly below `2^554`.
Every intermediate absolute sum is strictly below `8*2^554 = 2^557`. Eighteen uint limbs
provide 576 bits, with 19 spare bits. There is no overflow truncation or range heuristic.

Each individual product decodes two at-most-24-bit significands and multiplies them with
`covExactMul32`. Its exact unsigned magnitude is below `2^48`, so only two product words
are needed. A normal binary32 encoded exponent E contributes a shift of E-1 in integer
units. The combined product shift is between 0 and 506, so the shifted product is below
`2^554`. Flushed zeros return the zero accumulator before hidden-bit decoding. A 48-bit product shifted by r bits within a word occupies at most three adjacent words.
The placement generator emits 18 literal destination assignments selecting those three scalar
words, including the r=0 case without a shift by 32. No generic 18-word shift is needed.
Signed accumulation places those words in the wide result. The general
18-by-18 multiply is never called by the predicate or support query modules.

`covEdge` and `covDot` retain the exact integer sign and apply the existing RN-even conversion
at exponent -298 followed by F. Exact cancellation has sign zero and positive-zero bits.
A negative nonzero determinant that rounds or flushes to zero retains sign -1 and `80000000`.
Invalid XY inputs return sign zero and NaN, which the query guards reject.

For B30 let L be the maximum of the two exact side bit lengths. This is also the bit length
of their maximum magnitude. With `k = 299-L`, that maximum times `2^(k-298)` lies in `[1,2)`.
The crossing continuation submits k-298 to the shared RN32 job, so scaling is exact and precedes conversion.
Only opposite, nonzero signs enter this path. The larger rounded side remains at least one,
so F cannot erase the denominator. The final subtract and corrected divide retain the pinned
inventory tree. Endpoint and collinear overlay branches never call the crossing division.

## Task 3 support layout and order

The composing kernel supplies readonly `uint CovStaticWords[]`. A support argument is the
absolute word offset of its 16-word header. Its six table offsets are relative to that header,
so Task 4 can relocate a cold upload without changing any table word. Positions are XYZ words.
Source indices, boundary indices, the 257 cell offsets and candidate indices are uint words.
Each triangle stores all eight source fields in Mesh, Primitive, Triangle, Node, MaterialSlice,
A, B, C order. Reserved header words 13 to 15 stay zero. The sole temporary packer is test-only
`CoverageGpuTestSupportPacking.Append`. Task 4's production packer must reproduce these words
and replace that helper. No second production packer is introduced here.

`covHeight` uses only the point's 16-by-16 cell list. It checks inclusive global and triangle
AABBs, retains candidate order, and aborts on a candidate's invalid predicate or height.
The first accepted candidate returns its stored source index. `covCell` uses the pinned
subtract, corrected divide and multiply followed by the threshold comparison count.
It does not cast a float to an integer. Boundary queries retain strict less-than updates,
so the first edge wins ties. Height keeps the corner, canonical edge and barycentric trees.
Overlay visits each sheet triangle in order, then each host/sheet corner pair, then the nine
edge pairs in reference order. It uses exact signs for all discrete branches and retains
both height trees and the binary32 crossing point graph. No B22/B24 heuristic is restored.

Overlay retains the three-corner and nine-edge-pair traversal in typed continuation indices,
selecting named whole-vector and scalar fields through branches. Its side cache uses three named
three-sign records rather than an indexed array. This avoids expanding nine forced-inline
intersection graphs while retaining reference visit order. There are no shader arrays for overlay
vertices or sides, no dynamic vector or matrix component accesses and no dynamic private-array
writes. Separate physical predicate and query probes are registered in the coverage shader corpus. Their native tests additionally run `CoverageSpirvAccessAudit` with constant
private-array indices required, validate precise compilation and retain SPIR-V/HLSL/MSL artifacts
when `KE_SHADER_CORPUS_DUMP` is set. Every native Metal pipeline logs its thread ceiling and
execution width and asserts a ceiling of at least the 64-thread workgroup.


## Task 3 initial native evidence and reproduction

This section records the initial f16a90107 implementation. Its hosted compile and pipeline
failures are superseded by the C8 fix below.

The RED invocation failed with CS0103 for the missing predicate module. The completed Release
build has zero warnings and zero errors. All 18 tests in the required four-class selection
passed on MetalNative with Apple M2 Max, with zero skipped tests. The two native probes matched
755 predicate/crossing/grid rows and 2,314 support-query rows. Predicate rows compare all 18
magnitude limbs, exact signs, bit lengths and RN32/F words against the shared independent
BigInteger oracle from `CoverageReferencePredicateTests`. Both frozen group-224 incident-face
fixtures are shared directly, with every existing assertion retained. B30 expectations use independent scaled integer rounding and rational division.
Support rows compare every accepted word with the reference and separately pin the first source
triangle, boundary tie, reserved header words and relocated header layout. Invalid NaN results
are checked by class, as in Task 2, because they are rejected intermediates with no accepted payload.

Both modules passed the strict C5/C6 compiled access audit. Their emitted SPIR-V contains only
32-bit numerical types and no dynamic private-array accesses. Exact arithmetic is integer-only.
The predicate HLSL/MSL emissions contain no generic wide multiply or generic wide shift.
The support emission retains reference corner and edge-pair loops with named-field selection.
Native pipeline limits are 512 threads for predicates and 384 for support, with execution width
32 for both, above the required group size of 64. An earlier explicit nine-call overlay expansion
was cancelled before support completion and is not counted as a successful native proof.
The final lowering replaces that expansion and uses the proved three-word product placement.

```bash
build-slot --label c03-shared-fixtures-build -- dotnet build KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release
build-slot --label c03-shared-fixtures-tests -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~CoverageComputePredicateGpuTests|FullyQualifiedName~CoverageComputeSupportGpuTests|FullyQualifiedName~CoverageReferencePredicateTests|FullyQualifiedName~CoverageReferenceCrossingTests' --logger 'console;verbosity=detailed'
```

Set `KE_SHADER_CORPUS_DUMP` to retain both new modules' SPIR-V, HLSL, MSL and five corpus rows.
Only their ten new rows are added to the committed corpus. Existing shader hashes and frozen
coverage outputs are unchanged. Hosted run 39181650116 on this commit then failed. FXC cs_5_0 did
not finish compiling within 87 minutes, and Apple Paravirtual refused both pipelines. Lavapipe
passed but slowly. Local Apple M2 Max limits did not establish Paravirtual limits. The C8 section
below records the fix. This task makes no timing, load, stress, performance-budget or full-construction claim.


## C8 shared exact workers and artifact gates

The finite `CovExactJob` tags are Edge, Dot, Round, Div and Root. `CovExactResult` owns the
same exact accumulator used for the returned sign and rounded value. Edge's six terms and Dot's
eight terms keep their original signs and order. One rolled term loop has one product-placement
site and one checked signed-add site. The worker has one RN32 site and one `covRoundOperation`
site. No exact diagnostics recompute a predicate. Production `WorkerSource` omits the diagnostic
wide schoolbook multiply, and a compiled call-graph check forbids it in every other coverage module.

`covRoundOperation` shares special-value handling and one whole-domain C3 search between division
and square root. The search continuation tests its seed, predecessor, expanding bracket and
bisection at one midpoint-residual site. The root/division factor is selected before one compact
residual product site. The two-word residual representation, exact comparisons and 31-bisection
bound are unchanged. The rounding probe sends injected and native seeds through this same site.
Its existing pre-flush, post-flush, native-comparison and search-count assertions remain intact.

Height, boundary, triangle height, crossing and overlay have specific typed continuation records.
Each step requests one exact/scalar job or advances without one. Each physical query's rolled pump
calls `covExactWorker` at one syntactic site. Continuations retain inclusive AABBs, cell-only
source order, the first bad-candidate abort, strict boundary ties, endpoint and collinear order,
and both pinned height trees. Overlay's corner, edge-pair and source-triangle indices advance in
reference order. B30 keeps both unrounded 18-limb sides until their common normalization is known.
No peer flag, new domain restriction or approximate early-out is introduced.

The four predicate modules replace the old combined predicate probe. The three support modules
replace the old combined support probe. The diagnostic wide probe also uses rolled schoolbook
loops with constant-index word selectors, preserving every existing integer row. All worker,
threshold and traversal loops carry `DontUnroll`, emitted as `[loop]` in HLSL. The C5/C6 audit
continues to require constant private-array accesses and forbids dynamic vector/matrix components.

`CoverageShaderSizeTests` and the coverage branch of `ShaderCorpus.EmitCompute` enforce:

- At most 30,000 exhaustively inlined instructions and 100,000 LF-normalized UTF-8 bytes per
  ordinary HLSL/MSL target.
- Exactly one expanded call site for each retained heavy worker, including nested midpoint work.
- `DontUnroll` on every retained loop and no forbidden C5/C6 access.
- An exhaustive-inline measurement bound to the input module's SHA-256 in
  [coverage-sizes.csv](../../KhaozEngine.Render.Tests/Gpu/shader-corpus/coverage-sizes.csv).
  A changed module fails until remeasured. An additional structural call-tree bound retains
  function/parameter/call bookkeeping and allows eight scaffold instructions per extra return.
  Every measured exhaustive count is also checked against this conservative bound.

The measurement tool runs `spirv-opt --merge-return --inline-entry-points-exhaustive
--eliminate-dead-functions`, validates the result, requires exactly one function and zero calls,
and counts binary instructions after the header. It refuses any cap violation before replacing
the manifest. The gate therefore checks actual exhaustive counts without requiring the native
optimizer on every CI host. The original investigation's reader, artifacts and measurements were
reused to record the before/after table. These are instruction and byte counts, not timings.

Before C8, from the retained Task 2/3 artifacts:

| Pipeline | Raw SPIR-V | Exhaustive inline | HLSL bytes | MSL bytes |
| --- | ---: | ---: | ---: | ---: |
| CoveragePredicates | 6,395 | 172,018 | 51,611 | 57,136 |
| CoverageSupport | 9,884 | 1,362,517 | 86,685 | 94,784 |
| CoverageArithmetic | 2,151 | 8,920 | 17,911 | 20,791 |
| CoverageExactWords | 14,633 | 37,693 | 120,765 | 122,839 |
| CoverageResidual | 513 | 551 | 3,726 | 4,130 |
| CoverageRounding | 1,451 | 20,926 | 12,399 | 13,604 |

After C8, from the final emitted modules:

| Pipeline | Raw SPIR-V | Exhaustive inline | Structural bound | HLSL bytes | MSL bytes |
| --- | ---: | ---: | ---: | ---: | ---: |
| CoverageArithmetic | 2,151 | 8,920 | 10,114 | 17,911 | 20,791 |
| CoverageExactWords | 5,197 | 17,260 | 23,521 | 37,828 | 39,886 |
| CoveragePredicatesCell | 1,963 | 2,395 | 2,863 | 16,910 | 20,581 |
| CoveragePredicatesCrossing | 6,312 | 10,933 | 13,422 | 54,436 | 58,341 |
| CoveragePredicatesDot | 5,824 | 9,132 | 11,193 | 48,884 | 52,270 |
| CoveragePredicatesEdge | 5,818 | 9,128 | 11,187 | 48,850 | 52,225 |
| CoverageResidual | 513 | 551 | 626 | 3,726 | 4,130 |
| CoverageRounding | 1,257 | 1,399 | 1,620 | 11,675 | 12,588 |
| CoverageSupportBoundary | 6,664 | 11,855 | 14,390 | 57,409 | 63,314 |
| CoverageSupportHeight | 8,081 | 17,168 | 20,474 | 75,446 | 81,901 |
| CoverageSupportOverlay | 8,952 | 19,589 | 23,467 | 84,512 | 91,470 |

The largest physical module is Overlay, at 19,589/30,000 inline instructions,
84,512/100,000 HLSL bytes and 91,470/100,000 MSL bytes. No cap was raised. No additional
producer/worker dispatch split was needed for these query entry points.

The zero-warning Release build passed. The focused native Metal selection passed 144 tests,
including all 38 wide-integer rows, 29 residual rows, 315 rounding rows, 755 predicate rows,
2,314 support rows and the unchanged 144-hash reference manifest. The maximum observed search
count remains 30 bisections. After binding the exact measurements, 14 headless size/corpus tests
passed. All 34 original/inlined before-and-after coverage modules passed SPIR-V validation.

On Apple M2 Max, exact diagnostics retain a ceiling of 576 threads. Rounding and residuals report
1,024. Edge, Dot, Crossing and Cell report 768, 704, 384 and 1,024. Height, Boundary and Overlay
report 384, 512 and 384. Every queried pipeline reports execution width 32 and clears the
64-thread workgroup floor on the M2 Max. Hosted run 39193580270 on commit 47a62eec8 then passed
every leg with no skipped facts:

- Apple Paravirtual 102 of 102, with every pipeline created. The ceilings were 384 for Height,
  Overlay and Crossing, 512 for Boundary, 704 for Dot, 768 for Edge and 1,024 for Cell, Residual
  and Rounding, all at execution width 32.
- Direct3D11 WARP with FXC cs_5_0 102 of 102, plus 32 FXC and struct-size checks.
- Vulkan lavapipe 102 of 102.

Its Vulkan synchronization-validation selections of 8, 22, 2, 1, 7 and 496 tests all passed. No
timing or performance claim is made.

To refresh measurements after an intentional coverage shader change, first dump all size-test
modules with `KE_COVERAGE_SIZE_DUMP`. The changed-module test is expected to fail until the new
measurement is recorded. Run the following tool through build-slot, then rerun the headless gates:

```bash
build-slot --label coverage-inline-sizes -- python3 KhaozEngine.Render.Tests/Gpu/shader-corpus/measure-coverage-sizes.py <dump-directory> KhaozEngine.Render.Tests/Gpu/shader-corpus/coverage-sizes.csv
```

Tasks 4 to 7 compose Arithmetic, ExactWords.WorkerSource, Rounding, Predicates and Support in that
order. Static support words are unchanged. The `cov*Begin`/`cov*Step`/`Cov*Done` interfaces are the
shared seam. A combined physical phase must advance its graphs through one common worker call,
rather than calling several synchronous query wrappers. Task 6 splits conformation into height,
boundary and completion phases, and correction into membership/discrepancy, overlay and finish phases.
Its typed continuations use invocation-owned Work records with checked layout reservations.
Every physical module enters the same size manifest and native gates. Future Gather/ValidateFaces
modules must enter those gates too. These isolated queries do not establish a complete
construction or a future combined phase's feasibility.
