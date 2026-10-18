# Equipment coverage GPU arithmetic proof

This records Part C Task 1 against sections 3, 5 and 8 of the
[kernel contract](EQUIPMENT-COVERAGE-KERNEL-CONTRACT-2026-10-18.md).
It covers the scalar inventory and the shared vector graphs. Corrected division, square root,
exact predicates and complete construction equivalence belong to later tasks.

## Evidence status

The fix-round-1 local Release build passed with zero warnings and zero errors. All 103 focused tests passed,
including three native facts on MetalNative with an Apple M2 Max. The coverage fact compared all
13,949 words from 377 dynamic rows exactly. Scoped whitespace and SPIR-V validation passed.
The first hosted run, 39165413387, exposed Windows CRLF helper comparisons and WARP dynamic
component-selection poisoning. Fix round 1 addresses both below. Fresh native Vulkan and Direct3D11
WARP execution, Vulkan validation-layer results and actual FXC compilation remain **hosted-only and
pending**. Source qualifiers and cross-compilation alone do not close those gates.
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

Actual FXC compilation and Direct3D11 word equality are hosted-only and pending. The native compute
fact creates its shader through the real factory with PreciseMath enabled. The hosted Windows leg
therefore compiles with FXC and executes the same dynamic probe on WARP.

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
accepted definition, only the pinned emitter's direct assignment or return call to an accepted
helper is recognised. Comment-separated declarations, aliases, macros and unknown identifiers fail
compilation. Comments and string literals are not identifiers. Both the pinned constants and emitted
helpers are compared with normalized line endings, while replacements preserve source line endings.
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

Native Vulkan word equality and validation-layer results remain hosted-only and pending. Local
SPIR-V validation does not substitute for executing that backend.

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
Require zero-error Vulkan validation logs and no skipped native facts. Hosted Vulkan and D3D11 gates
remain pending until their actual results are recorded.
