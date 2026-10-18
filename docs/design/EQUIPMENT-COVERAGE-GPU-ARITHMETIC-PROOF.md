# Equipment coverage GPU arithmetic proof

This records Part C Tasks 1 and 2 against sections 3, 5, 6 and 8 of the
[kernel contract](EQUIPMENT-COVERAGE-KERNEL-CONTRACT-2026-10-18.md).
It covers the scalar inventory, shared vector graphs, exact integers, corrected division and square
root. Exact predicates and complete construction equivalence belong to later tasks.

## Evidence status

The fix-round-1 local Release build passed with zero warnings and zero errors. All 103 focused tests passed,
including three native facts on MetalNative with an Apple M2 Max. The coverage fact compared all
13,949 words from 377 dynamic rows exactly. Scoped whitespace and SPIR-V validation passed.
The first hosted run, 39165413387, exposed Windows CRLF helper comparisons and WARP dynamic
component-selection poisoning. Fix round 1 addresses both below. Hosted run 39169835980 on commit
4f2bf49bb then passed every leg with no skipped facts. The run covered MetalNative (113 of 113),
Direct3D11 WARP with FXC (113 of 113, plus 32 FXC and struct-size checks) and Vulkan lavapipe (113 of
113). Its Vulkan synchronization-validation selections of 1, 7 and 493 tests passed with no VUID or
hazard lines.
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
Checked add and product overloads expose overflow to the native probe. The production overloads
use the same implementation under the proved domain bound.

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

For adjacent positive ordinal words lower and upper, decode their integer magnitudes as M(lower)
and M(upper), and let S be their exact sum. Their midpoint is `S*2^-150`. For the infinity
endpoint only, M(infinity) means the virtual finite successor `2^128`, whose integer magnitude
is `2^277`. This makes the overflow boundary exactly `2^128-2^103`, without doing arithmetic
with an infinity value.

Division compares `A<<150` with `B*S`, where `a=A*2^-149` and `b=B*2^-149` are positive operand
magnitudes. This is the exact sign of `a-b*midpoint`, scaled by positive `2^299`.
Square root compares `A<<151` with `S*S`. This is the exact sign of `a-midpoint*midpoint`,
scaled by positive `2^300`. M(finite) is below `2^277` and S is below `2^278`, so division's
product is below `2^555` and square root's product is below `2^556`. The shifted left sides
are below `2^427` and `2^428`. All fit 18 limbs, including the overflow midpoint. Integer
comparison supplies the residual sign without subtracting rounded float values.

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
| Direct3D11 | The same residual scaling, even-midpoint predicate and 31-bisection bound apply under FXC IEEE strictness. There is no one-neighbor assumption. Partial products use uint only. New modules pass the C5 SPIR-V access audit and emit HLSL for Shader Model 5.0. Native FXC/WARP execution is hosted-only and remains a root verification gate. |
| Vulkan | The same exact proof applies, including divisors outside the native accuracy interval and sqrt with no assumed ULP bound. PreciseMath retains SignedZeroInfNanPreserve. New modules pass SPIR-V validation. Native Vulkan execution and zero-error synchronization validation are hosted-only and remain root verification gates. |

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

`CoverageExactWords` and `CoverageRounding` are registered in the coverage shader corpus, so the
module-wide C5 audit checks both. Their storage-driven native probes poison outputs before
dispatch and compare uint words exactly. The limb probe checks every limb, sign, overflow,
comparison, bit length and RN32 word. The rounding probe checks RN32 before F, the final F word
and the production helper's final word independently. It checks each bisection count is at most 31.
For every fixed input it exercises the actual native candidate plus injected zero, infinity,
previous-word and next-word seeds, replacing only the approximate candidate. The corpus includes
the specified tiny division results, wide divisor exponents, overflow, both zero signs and signed
quiet/signaling NaN, infinity and zero divisor classes.

The red test failed because the planned exact-word and rounding GLSL types were absent. The focused
local green run passed on MetalNative with Apple M2 Max. It checked 34 integer rows and 315
rounding rows, with zero limb overflows and a maximum of 30 bisections. The three focused tests
and four corpus/coverage-module checks passed with no skipped facts. The emitted SPIR-V retains a
NoContraction OpFDiv and a GLSL.std.450 Sqrt. Type-width checks reject any float or integer type
wider than 32 bits. Text
comparisons normalize line endings for Windows checkouts. This is a bounded correctness proof,
with no timing, load, stress, performance-budget or complete-construction claim.

```bash
build-slot --label c02-build -- dotnet build KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release
build-slot --label c02-tests -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~CoverageGpuExactWordsTests|FullyQualifiedName~CoverageComputeRoundingGpuTests|FullyQualifiedName~CoverageComputeArithmeticGpuTests' --logger 'console;verbosity=normal'
```

The root hosted filter is the same three-class expression. Direct3D11 and Vulkan native results
are pending until the root's hosted legs run this commit. Shader compilation and local Metal
results do not substitute for those backend executions.
