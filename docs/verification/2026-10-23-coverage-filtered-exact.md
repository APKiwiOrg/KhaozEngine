# Coverage filtered-exact prototype

This branch is an exactness prototype, pending native verification and separate performance approval.
It does not change the NC2 word contract, the kernel contract or the exact reference.

## Backend admission

The creation-fixed `CoverageGpuPipeline.FilterPath` reports Exact, FilteredPreserve or FilteredFlush.
`PathFor(batch)` reports its actual path. A full reservation above Vulkan's guaranteed 128 MiB
storage-buffer range keeps the exact batch, and exact backends allocate no pair storage.
Only Vulkan can select a filter. It requires queried binary32 signed-zero/infinity/NaN preservation,
RTE and a supported explicit denormal mode. Preservation is preferred. The normal-domain guard also
permits explicit flush-to-zero. Missing properties select the existing exact pipeline.

`GpuShaderCompileOptions.RequireFloat32Eft` adds RoundingModeRTE32 and the selected denormal declaration
to SPIR-V. `FlushFloat32Denorms` chooses FTZ. Both flags contribute to the front-end and disk identities.
The Vulkan factory checks the properties before shader-module creation and again before compute-pipeline
creation. Metal and D3D11 refuse an EFT compute request. Every filter add, subtract and multiply must
carry NoContraction, and RelaxedPrecision is forbidden.

The [Vulkan SPIR-V environment](https://docs.vulkan.org/spec/latest/appendices/spirvenv.html), Precision of
Individual Operations, specifies correctly rounded binary32 add, subtract and multiply. Its Correctly
Rounded section makes RoundingModeRTE mean IEEE roundTiesToEven. The
[float-controls properties](https://docs.vulkan.org/refpages/latest/refpages/source/VkPhysicalDeviceFloatControlsProperties.html)
independently gate RTE and each denormal mode.

Metal stays exact. The [Metal Shading Language specification](https://developer.apple.com/metal/Metal-Shading-Language-Specification.pdf),
2026-06-04, section 8.2 on page 368 permits ties-to-even or toward-zero arithmetic. Section 8.4's
correctly-rounded operations do not select RTE. Disabling fast math therefore does not supply the
required rounding guarantee. Section 8.1 also permits flushing denormal inputs and outputs.

D3D11 stays exact under the documented
[floating-point rules](https://learn.microsoft.com/en-us/windows/win32/direct3d11/floating-point-rules).
Its permitted add/subtract/multiply deviations do not establish the EFT prerequisite. Native sample
agreement cannot enable either backend.

## Enclosures and rounding

Inputs first use the unchanged inclusive inventory flush. Each nonzero product operand has exponent
in [-100,100], and each nonzero product's operand-exponent sum must lie in [-78,100]. Failure refuses
the independently owned work item.

Under RTE and NoContraction, splitter-4097 TwoProduct supplies an exact high/low pair. Four TwoSum
operations accumulate each pair. The only discarded terms are the two intermediate low tails, so the
new nonnegative radius is an outward sum of the old radius and both absolute tails. The center is the
retained pair. This is an enclosure of the exact six-product Edge or eight-product Dot polynomial.
No empirical epsilon or relative-error estimate is used for the double-single enclosure.

The product lattice is at least 2^-124, since each binary32 operand contributes at most 23 fractional
bits below its exponent. Splitting and the EFT center graph remain on that lattice. All nonzero EFT
intermediates are normal. Split operands remain below overflow, and summing eight bounded products
also remains finite. Explicit FTZ and preservation therefore give the same EFT identities in this
guarded domain. Radius operations use positive outward rounding without the inventory flush. A
positive radius is never rounded down to zero.

For sign-only inside tests, the first filter uses an outward sum S of absolute rounded products and
an outward radius of 1.1e-6*S plus 32*2^-126. The coefficient exceeds gamma(17,2^-24), covering at most
16 rounded operations and inflation from rounded product magnitudes to exact magnitudes. Overflow
or an undecided sign falls through to the double-single filter. A sign-only result also proves a
finite rounded value in the guarded range. That temporary value is not used as a construction.

A double-single sign is accepted only when the high component dominates the outward sum of the
absolute low component and radius, or the enclosure is the exact point zero. A construction needs
more. The complete ball is compared against both exact RN32 midpoint pairs using the same EFT
accumulator. Strict containment accepts. An exact midpoint accepts only the even candidate.
Candidates outside exponents [-100,100] refuse, except exact zero. The smallest midpoint half-step
is 2^-125, still normal. Negative values use magnitude symmetry and restore the exact sign.

B30 determines each side's binade from its unrounded ball before choosing the common exponent.
Both comparisons against powers of two must certify that binade. Scaling changes exponent bits
exactly and refuses if any nonzero component would leave the guarded normal range. Each scaled
side then receives its own RN32 certificate. Rounded raw sides never select the scale.

Scalar division and square root retain the unchanged two-word integer midpoint correction. The
light worker has a four-scalar ball and no 18-limb job/result payload. The existing continuation
sources preserve endpoint precedence, candidate order, expression trees and finite checks.

## Queue ownership and replay

Each actor reserves checked storage for every face and every possible host/support pair over its
currently reserved supports. Support replacement rebuilds that reservation before publication.
Every producer initializes its record state. Records are actor-local and invocation-owned.

The first face probe can refuse to a whole-face exact replay. Overlay fallback is an independent
host/support pair. Two GPU compaction kernels scan flags and write stable private queue indices.
They allocate no capacity dynamically and cannot overflow their complete reservation. Pair dispatches tile into z workgroups above the guaranteed 65535-group x limit. Exact
consumers gate themselves from the GPU count, including the empty queue. No CPU count readback or
indirect-dispatch dependency is introduced.

A pair record stores completion state, its source-ordered successful-prefix maximum and success.
Exact replay overwrites the entire private result. Final face replay visits support order, folds
prefix maxima and stops at the first failed pair. This compact summary is sufficient because the
existing finite `covMax` is associative, including its specified signed-zero tie rule. Failed samples
never enter the maximum. Later speculative pair computations publish no global failure notes.

Face fallback latency and the initial one-invocation-per-actor compaction are prototype limitations.
No performance claim follows from the CPU study or these structures. Native word evidence, shader
sizes and queue counts belong in the round report. Vulkan validation cleanliness is not claimed
because [#1539](https://github.com/APKiwiOrg/KhaozEngine/issues/1539) remains unresolved.
