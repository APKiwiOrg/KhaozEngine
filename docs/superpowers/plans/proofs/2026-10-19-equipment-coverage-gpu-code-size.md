# Equipment coverage GPU code-size investigation (Part C Task 3, ruling C8)

Research only, on `feature/equipment-coverage-gpu-path` at `f16a90107`.

## Recommendation

Choose **C, single primitive call sites through bounded job loops**, retaining the already verified dense integer arithmetic initially. Score **44/50**. Apply a physical kernel split from D wherever the artifact cap below would otherwise be exceeded. Do not start by replacing the arithmetic with a new expansion algorithm or by restricting the numerical domain.

The important detail is *primitive* call sites. One call to `covHeight` or one outer loop around `covEdge` does not solve nested duplication. Lower the six/eight product polynomial to one product site and one signed-add site, and route every exact predicate request, rounding request and corrected div/sqrt request in a physical kernel through shared worker sites. Keep the query continuation and reference traversal order explicit. A later exponent-aware representation from A can replace that worker without changing its callers, if separately justified.

This preserves sections 3, 4, 6, 7 and 8, B27, B30, C3 and all admitted input words. It needs a plan/interface amendment for query continuation scratch and artifact-size gates. It needs no new refusal code and no numerical contract change.

There is no published universal FXC or Apple Paravirtual source-size limit that proves pipeline acceptance. The construction below bounds duplication and private state, and puts artifacts below the known accepted control. Native C6 pipeline acceptance remains mandatory. This report does not claim that an unimplemented design has passed either compiler.

## Scope and evidence

The requested starting guard returned the expected branch, `f16a90107` and an empty status. No subagent, stash, commit, push, hosted dispatch, native GPU pipeline, timing experiment or redesign implementation was used.

Read the C3/C5/C6/C7 rulings and hosted records, both task reports including Task 2's fix-round bounds, Shared interfaces and Tasks 5 to 7, contract sections 3, 4, 6 to 9, and the complete reference predicate and support implementations. The findings below apply to this commit, rather than to the original Task 2 implementation before its compact residual fix.

### Measurement method

`size-probe/SizeProbe.csproj` is an isolated console harness inside this ignored workspace. It links the current arithmetic, residual, rounding, predicate and support source generators. A workspace copy of the exact-word generator exposes the existing width as a measurement parameter. Its 18-limb logic is unchanged. No production file was changed for the emitted-code measurements.

Each kernel makes exactly one observable call to the named helper. Inputs come from storage buffers, and result words, signs, overflow words and all wide result limbs are stored. Thus constants cannot fold away the operation. Each entry point has 64 threads. Unreachable helpers are absent from the compiled module. The baseline storage-copy kernel is 129 SPIR-V instructions, 651 HLSL bytes and 943 MSL bytes.

The production `SpirvFrontEnd.ToSpirv` and both `SpirvCrossCompile` compute emitters use `PreciseMath: true`. These are emission measurements, without FXC invocation or Metal library/pipeline creation. SPIR-V instruction counts parse binary word counts after the five-word header and include declarations and control flow. Function counts count SPIR-V definitions and emitted top-level definitions. Line counts use LF-normalized UTF-8 text. HLSL includes its entry wrapper, and MSL includes any emitter helper definitions.

The second measurement applies `spirv-opt --merge-return --inline-entry-points-exhaustive --eliminate-dead-functions`, using SPIRV-Tools v2026.4, and reemits HLSL/MSL. This makes duplication directly measurable. It is not FXC output, DXBC instruction count or a prediction of final register allocation. All such modules have one SPIR-V function and zero function calls. Neither loop unrolling nor general optimization is requested. The nine original and nine inlined isolated modules all passed `spirv-val --target-env vulkan1.3`.

The smaller widths below are structural experiments only. They deliberately truncate the mathematical domain and are not candidate correct implementations.

## 1. Size per helper

| One call | SPIR-V instructions | Functions SPV/HLSL/MSL | HLSL bytes / lines | MSL bytes / lines | Exhaustively inlined SPIR-V instructions |
| --- | ---: | --- | ---: | ---: | ---: |
| CovExact multiply | 10,557 | 5/6/6 | 90,563 / 2,347 | 92,182 / 2,410 | 18,869 |
| CovExact signed add | 2,246 | 7/8/8 | 15,227 / 548 | 16,990 / 613 | 3,698 |
| covRound32 | 1,577 | 7/8/9 | 11,537 / 612 | 13,531 / 685 | 2,168 |
| covEdge | 4,460 | 25/26/27 | 33,322 / 1,547 | 36,574 / 1,636 | 25,214 |
| covDot | 4,525 | 25/26/27 | 33,958 / 1,569 | 37,238 / 1,658 | 33,964 |
| covCrossing | 5,515 | 36/37/39 | 42,634 / 1,957 | 46,749 / 2,062 | 54,213 |
| covHeight | 7,289 | 54/55/62 | 60,022 / 2,658 | 66,758 / 2,831 | 242,147 |
| covBoundary | 6,324 | 48/49/56 | 50,543 / 2,251 | 56,861 / 2,417 | 77,315 |
| covOverlay | 7,997 | 61/62/69 | 67,152 / 2,979 | 74,515 / 3,153 | 1,043,599 |

The ordinary HLSL emitter preserves shared function definitions. It therefore hides much of FXC's eventual duplication. The exhaustive-inline text experiment exposes that difference.

| One call, after exhaustive inlining | HLSL bytes / lines | MSL bytes / lines |
| --- | ---: | ---: |
| CovExact multiply | 156,653 / 3,731 | 158,012 / 3,790 |
| CovExact signed add | 41,967 / 1,133 | 43,272 / 1,192 |
| covRound32 | 32,277 / 1,060 | 33,847 / 1,127 |
| covEdge | 344,250 / 9,312 | 345,835 / 9,377 |
| covDot | 462,716 / 12,405 | 464,299 / 12,470 |
| covCrossing | 757,737 / 20,140 | 759,213 / 20,204 |
| covHeight | 4,342,053 / 90,837 | 4,344,339 / 90,841 |
| covBoundary | 1,270,521 / 28,537 | 1,273,634 / 28,631 |
| covOverlay | 25,654,539 / 392,157 | 25,637,510 / 391,682 |

The inlined HLSL has two top-level definitions, the body and entry wrapper. Inlined MSL can also retain emitter helper definitions. SPIR-V has one function in every row. All function counts, including those text helpers, are in `size-measurements.csv`.

### Existing accepted and failed probes

| Existing probe artifact | SPIR-V instructions | Functions | HLSL bytes | MSL bytes | Recursive expansion proxy |
| --- | ---: | ---: | ---: | ---: | ---: |
| Task 2 ExactWords, accepted on Paravirtual | 14,633 | 18 | 120,765 | 122,839 | 36,195 |
| Task 2 Residual | 513 | 6 | 3,726 | 4,130 | 458 |
| Task 2 Rounding | 1,451 | 17 | 12,399 | 13,604 | 21,110 |
| Task 3 Predicates, failed hosted gate | 6,395 | 43 | 51,611 | 57,136 | 162,310 |
| Task 3 Support, failed hosted gate | 9,884 | 73 | 86,685 | 94,784 | 1,300,951 |

The proxy recursively substitutes each function body at each call and counts each retained loop body once. It includes function/parameter bookkeeping, so it differs from the optimizer's actual inline count. It is useful for comparing the old control with the failed probes without rebuilding their pipelines. It is not a hardware instruction count.

The failed Support source is smaller than the accepted ExactWords source, yet has about **35.9 times** its recursive expansion count. The failed Predicates source has about **4.48 times** that count. A cap on original HLSL bytes alone would accept the failing probes and reject a known working control.

The large general wide schoolbook multiply is **not reachable from Task 3 predicates**. Task 3 already uses 24-by-24-bit products. The remaining multiplication is at the call-graph level: repeated wide signed adds, rounding, and corrected division within nested query helpers.

## 2. Growth

### Limb width

| Limbs | Multiply SPV | Add SPV | Round SPV | Edge SPV | Edge fully inlined SPV | Edge HLSL bytes | Edge MSL bytes |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 2 | 511 | 535 | 553 | 1,660 | 6,318 | 12,838 | 16,108 |
| 4 | 983 | 749 | 681 | 2,011 | 8,681 | 15,368 | 18,634 |
| 8 | 2,598 | 1,177 | 937 | 2,711 | 13,405 | 20,433 | 23,695 |
| 12 | 5,110 | 1,605 | 1,193 | 3,411 | 18,129 | 25,546 | 28,804 |
| 16 | 8,517 | 2,032 | 1,449 | 4,110 | 22,852 | 30,730 | 33,984 |
| 18 | 10,557 | 2,246 | 1,577 | 4,460 | 25,214 | 33,322 | 36,574 |

Six widths, 2, 4, 8, 12, 16 and 18, were measured. With `w` limbs, least-squares fits for raw module instructions are:

| Helper | Instruction fit | Maximum absolute residual |
| --- | --- | ---: |
| Generic multiply | `28*w*w + 67.865385*w + 263.346154` | 0.270 |
| Signed add | `106.932692*w + 321.339744` | 0.468 |
| Round | `64*w + 425` | 0 |
| Edge | `174.971154*w + 1310.788462` | 0.731 |
| Dot | `174.971154*w + 1375.788462` | 0.731 |
| Crossing | `174.971154*w + 2365.788462` | 0.731 |

Instruction-fit R-squared is at least 0.99999979. Residuals below one instruction arise from constant reuse. The recursive expansion fits are exact over these widths:

- Multiply is `56.5*w*w + 92.5*w + 86`.
- Add is `170*w + 239`.
- Round is `79*w + 301`.
- Edge is `1105*w + 3637`.
- Dot is `1501*w + 4772`.
- Crossing is `2236*w + 10701`.

Generic multiplication has `w*(w+1)/2` retained partial products plus quadratic overflow checks. Task 3 avoids that operation, explaining its linear width growth. Width reduction alone does not remove call-site growth.

Emitted-text byte fits are also stable. Edge HLSL is `1280.144231*w + 10238.057692` and MSL is `1279.067308*w + 13509.160256`, with maximum residuals of 54 bytes and R-squared above 0.999974. Generic multiply HLSL is `255.893861*w*w + 317.324704*w + 1887.713018`, maximum residual 83 bytes. Identifier digit counts account for the small text deviations. Complete fits for every measured scalar helper and both text targets are in `size-fits.txt`.

Even an unsound four-limb substitution only reduces isolated inlined Edge from 25,214 to 8,681 instructions. It does not eliminate the 32 Edge sites and 43 Div sites in the rolled overlay call graph.

### Number of call sites

Independent calls use distinct storage input/output ranges, so every result remains observable.

| Edge call sites | Raw SPIR-V | Fully inlined SPIR-V | HLSL bytes before inlining | MSL bytes before inlining |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 4,460 | 25,214 | 33,322 | 36,574 |
| 2 | 4,478 | 50,198 | 33,538 | 36,814 |
| 4 | 4,553 | 100,239 | 34,202 | 37,536 |
| 8 | 4,705 | 200,323 | 35,530 | 38,986 |

For the identically structured 2/4/8-site wrappers, fully inlined size is `25021*n + 155`, within one instruction. HLSL before inlining is exactly `332*n + 32874` bytes. The one-site isolation wrapper has a different local-variable setup and is not used to fit those intercepts. The recursive proxy for the 2/4/8 wrappers is exactly `23490*n + 4`.

Thus going from two sites to eight grows the original HLSL by only 5.9 percent while the inlined module grows almost fourfold. FXC duplication is required by [HLSL's inline function semantics](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/dx-graphics-hlsl-function-syntax). A source-level helper is not a shared machine-code subroutine on this target.

### Exact helpers inside loops

| One Edge site in a loop | Raw SPIR-V | Fully inlined SPIR-V | HLSL bytes | MSL bytes | Original SPIR-V loops |
| --- | ---: | ---: | ---: | ---: | ---: |
| Constant 3 iterations | 4,476 | 25,213 | 33,372 | 36,598 | 1 |
| Constant 256 iterations | 4,477 | 25,214 | 33,374 | 36,600 | 1 |
| Runtime count | 4,478 | 25,215 | 33,383 | 36,611 | 1 |

All three have `OpLoopMerge` control mask 0 and retain that one loop after exhaustive inlining. The support candidate loop, boundary loop and overlay loops also remain in emitted HLSL/MSL. Pre-generating constant limb indices does **not** itself force those enclosing loops to unroll. Their bodies no longer contain the dynamically addressed private array writes that caused Task 2's X3550 failure.

FXC still has its own loop heuristics. This investigation did not run it and cannot say which heuristic it selected in the cancelled hosted run. [Microsoft's for-loop documentation](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/dx-graphics-hlsl-for) describes both automatic unrolling and `[loop]`. The redesign must emit and audit `DontUnroll`/`[loop]` for job, term, bin and traversal loops. It must not depend on a compiler deciding that a loop is too large to unroll.

The current overlay already has 32 static `covEdge` sites and two further exact Edge evaluations inside its one crossing site after function expansion, even with loops rolled. Explicitly expanding the three-corner and nine-edge-pair loops would raise exact determinant sites from 34 to 158 per support-triangle body. There is no evidence that such expansion is necessary for the observed failure.

## 3. Largest production kernel implied by Tasks 5 to 7

The largest phase is **Task 6 CorrectFaces**, which performs two membership heights for each of three corners, then the analytic overlay and final corrected division. Height itself contains four membership Edges and up to four more Edges in `covTriangleHeight`. Overlay calls both height graphs at each accepted intersection/corner.

| Representative physical phase shape | Raw SPIR-V | Raw HLSL / MSL bytes | Exhaustively inlined SPIR-V | Inlined HLSL / MSL bytes |
| --- | ---: | ---: | ---: | ---: |
| Conform, Height then Boundary path | 8,237 | 69,624 / 76,957 | 319,098 | 5,760,479 / 5,762,603 |
| CorrectFaces with corner loop retained | 9,197 | 79,392 / 87,345 | 1,535,238 | 35,301,650 / 35,281,398 |
| CorrectFaces with six literal Height sites | 9,098 | 78,585 / 86,426 | 2,495,469 | 52,529,397 / 52,505,855 |

These are measured call-structure probes, **not implemented production kernels or numerical-equivalence proofs**. They omit matrices and some guards, and their incidental scalar arithmetic is only enough to keep the required helper calls observable. The six-site variant also omits the loss/correction arithmetic, so its increase understates the cost of fully expanding the complete original corner body.

The rolled CorrectFaces shape is 4.81 times the measured Conform shape and 47.1 percent larger than isolated Overlay by inlined instructions. Merely expanding the corner membership sites adds 960,231 instructions, even in the simpler comparison wrapper. Its recursive call graph has 50 exact Edge evaluations and 62 Div sites, including B30's two exact sides. The triangle count can be 1,024, but a runtime triangle count scales executed work, not compiled size, while that loop stays rolled.

Task 5's generated-attribute validation has about 12 corrected divisions and two roots per static generated-vertex path. Task 7's expensive GatherAttributes path has two gather sites. Each gather contains a 24-division/four-root TryBasis, another three-division/root Unit, and a final three-division/root Unit. That totals 60 Div sites and 12 Sqrt sites across the two static gather paths. Current recursive costs are 3,622 instructions per Div and 3,463 per Sqrt, giving 258,876 heavy-helper instructions before the ordinary vector graph. ValidateFaces adds one exact Edge and approximately 24 Div/six Sqrt sites if its basis and orientation paths share one physical entry point. These are estimates from the pinned graphs, rather than emitted Tasks 5/7 implementations. They identify another reason to share div/sqrt workers, but they do not approach CorrectFaces' measured exact-query expansion.

Compiling a phase-selection switch containing every physical phase would add their call graphs together. Create separate phase entry modules as the plan implies.

## 4. Proved widths on the existing domain

Let `q = 2^-149` and `M = (2^24-1)*2^104`, the maximum finite binary32 magnitude. A surviving nonzero coordinate is `sign*s*2^e`, with `2^23 <= s < 2^24` and `-149 <= e <= 104`. F removes the smallest normal itself, but it does not remove the larger normals with their low significand bits.

| Quantity | Proved magnitude/storage bound | Reason |
| --- | --- | --- |
| Coordinate significand | 24 bits plus sign and exponent | Binary32 encoding |
| Dense coordinate in q units | 277 magnitude bits, 9 limbs | `M/q = (2^24-1)*2^253` |
| Exact coordinate difference in q units | 278 magnitude bits, 9 limbs | Maximum is `2*M/q` |
| Original-coordinate product | 48 significand bits, 2 words, plus exponent | Multiply two 24-bit significands |
| Dense original-coordinate product in q-squared units | 554 magnitude bits | Maximum is `(2^24-1)^2*2^506` |
| Product of coordinate differences | 556 magnitude bits, 18 limbs | Maximum is `4*M*M/q^2` |
| Final Edge determinant | 556 magnitude bits, 18 limbs | A triangle in the square `[-M,M]^2` has doubled area at most `4*M*M` |
| Final difference Dot | 557 magnitude bits, 18 limbs | Two independent products of differences can sum to `8*M*M` |
| Signed partial sums of the expanded polynomials | At most 557 magnitude bits | At most eight original-coordinate product magnitudes |
| Division midpoint numerator | 25 bits | `2*s+1`, including the overflow endpoint |
| Division midpoint product | 49 bits, 2 words | 24-by-25 bits |
| Square-root midpoint square | 50 bits, 2 words | 25-by-25 bits |
| Exact comparison of aligned compact residuals | 64-bit normalized window, 2 words | Compare leading powers first, then normalize at most 50 significant bits |

These are magnitude widths with a separate sign, not two's-complement widths. The 556-bit Edge improvement over the plan's conservative 557 does not save a limb. Dot genuinely reaches 557 bits when both difference products are positive maxima. The exact Edge maximum is attained by three corners of the square. Generic independent two-product determinants retain the conservative 557-bit bound.

Flushing inputs does **not** imply that the smallest nonzero difference is `2^-126`. Set `t = (2^23+1)*q`. Both `t` and `t+q` survive F, but their exact difference is q. With `a=(t,t)`, `b=(t+q,t)`, `p=(t,t+q)`, Edge is exactly `q^2 = 2^-298`. A large common floating scale therefore cannot discard all small components without losing exact signs or midpoint tails.

Dense 278-bit differences and 557-bit accumulators are sufficient and necessary for those *dense representations*. They are not required for each primitive instruction or each term. Expanding original-coordinate products avoids forming a wide difference product. A shifted 48-bit product occupies at most three adjacent radix-32 words. The unshifted product itself always fits two words.

### A complete bounded chunk construction for A

A full-domain exponent-aware alternative can retain six or eight signed `(low, high, exponent)` products. Use radix `2^16`, with at most **35 bins** covering the existing 557-bit range. Decode the original significands once and form their exact two-word products. A 48-bit product shifted by 0 to 15 bits contributes to at most four radix-16 bins.

For each bin, independently accumulate positive and negative digits. With at most eight terms, each digit sum plus carry is at most `8*65535 + 7 = 524287`, requiring **19 bits**, and the carry is at most 7. Traverse bins from low to high, propagating both carries. The highest differing positive/negative bin determines the exact sign. A second traversal recomputes those canonical digits and subtracts the smaller stream from the larger with a one-bit borrow. Recomputing avoids storing a 35-bin private array.

During the magnitude pass retain the leading three 16-bit bins, their exponent, and an OR-sticky bit for everything lower. This retains at least 33 leading significant bits, sufficient for the 24-bit RN32 significand, guard and sticky under any power-of-two scale. Handle zero and signed underflow explicitly. B30 uses each exact sign and leading exponent to choose the common scale before rounding either summary. Equal leading exponents need no full-magnitude ordering to choose that scale.

This is exact even after complete cancellation of high terms. Looking only at leading *input* chunks would not be exact. Every signed term must participate in canonical carry and borrow before dropping tails to sticky. There is no instruction whose width grows to 557 bits. Term records can use invocation-owned Work storage with checked offsets and dynamic **storage-buffer** addressing, avoiding C5/C6's forbidden private-array indexing. At most eight four-word records use 32 scratch words per active job, plus scalar continuation and summaries. There are two bounded 35-by-at-most-8 digit traversals. A constant-length loop is still required to remain rolled.

## 5. Options and scores

All scores use 10 as best. For implementation risk, a high score means *less* risk. Totals are unweighted. Artifact budgets are estimates and proposed implementation gates, not measurements of a redesign. No native pipeline acceptance is claimed here.

| Option in its deployable form | Correctness confidence | Size margin | Contract fidelity | Implementation risk | Maintainability | Total |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| **C, dense arithmetic with shared primitive job sites, selective D** | **10** | **9** | **10** | **7** | **8** | **44** |
| A, exponent-aware chunks plus C | 9 | 10 | 10 | 5 | 7 | 41 |
| D, split down to bounded exact/query workers | 10 | 9 | 10 | 5 | 6 | 40 |
| E, spread <=64 plus C | 9 | 10 | 5 | 7 | 8 | 39 |
| B, scaled tagged expansions plus shared workers | 8 | 8 | 10 | 4 | 5 | 35 |

A or B inside the current call graph, a C loop only outside a whole query, and a D split only between logical stages are **not sufficient solutions**. The measured primitive/body budgets below are the deployable forms being scored.

### A. Exponent-aware integers

**Exactness.** Use the two-pass radix-16 construction above. Products remain 48-bit significands with exponents. Canonicalization covers the complete range, and signed zero, RN-even, overflow and B30 operate on exact summaries. Keep Task 2's already compact integer residuals.

**Expected size.** The existing two-word product primitive is 66 SPIR-V function instructions, and the accepted residual module containing compact products/comparisons is 513 instructions and 3,726 HLSL bytes. A rolled scalar bin loop has one copy of digit extraction, two carry streams and final borrow/summary logic, independent of width and term count. An estimated 2,000 to 5,000 fully inlined instructions for the exact worker is conservative relative to those primitive bodies, including decoding, term selection, both traversals and rounding. A query pipeline with C should fit the proposed 30,000-instruction and 100,000-byte-per-text-target gates. This estimate is not a measured implementation.

A alone, replicated at the current overlay's 34 exact determinant sites plus 43 Div sites, could still exceed 200,000 expanded instructions. Combine it with C or split those sites with D. Representation improvement alone is insufficient.

**Risk.** New canonicalization and rounding proofs, exponent-bound endpoints, carry/borrow and sticky-tail errors, plus storage scratch capacity. More Work traffic and loop iterations are unmeasured. No performance claim follows from smaller compiled size.

**Impact.** No domain or numerical reference change. Section 7 permits a different exact representation. Document the new internal representation and extend independent integer-oracle tests. Frozen hashes stay unchanged.

**Effort.** High. A new exact accumulator, summary conversion and proofs, plus the C continuation work. This is a useful alternative worker, but introduces mathematical changes before testing whether call-site control alone solves the defect.

### B. Scaled binary32 expansions

**Why naive TwoSum/TwoProduct fails.** D3D11 flushes nonzero subnormal operands/results, while Metal permits that behavior even with fast math disabled. Error-free transformations need the discarded error component, not merely an accurate main result. For example, adding `a=(1+2^-23)*2^-125` to `b=2^-102` has an exact TwoSum error of `2^-148`, which is subnormal and can disappear. Normal input coordinates do not protect such errors. An unscaled product of two normal values near `2^-100` also underflows.

These conclusions follow from [Direct3D11 floating-point rules](https://learn.microsoft.com/en-us/windows/win32/direct3d11/floating-point-rules) and the [Metal Shading Language specification](https://developer.apple.com/metal/Metal-Shading-Language-Specification.pdf), sections 8.1 and 8.5. The downloaded Metal document's printed pages 368 and 376 are retained as text extracts. Metal allows subnormal-before-rounding flushing as well as input flushing. Its flushed zero sign is not a portable error-sign representation. The contract's explicit inclusive F is stricter again if used inside an unscaled expansion. [Shewchuk's primary predicate material](https://www.cs.cmu.edu/~quake/robust.html) supplies the expansion approach, not a proof that these GPU denormal modes preserve it.

**A proved safe local scale.** Decode each nonzero coordinate as a signed `m*2^E`, with normal binary32 `1 <= abs(m) < 2`, and keep E as an integer. Use Veltkamp-Dekker split with 4097 on these normalized mantissas, then TwoProduct without contraction. Splitter intermediates are below 8194. Every nonzero product/error component is a multiple of `2^-46`, and all such arithmetic stays normal and finite.

For a band of product exponents spanning at most 32, scale components exactly by bit/exponent operations to its top exponent. All components and TwoSum intermediates are integer multiples of `2^-78`, while the sum of at most eight products is below 32. Therefore no nonzero exact error in that band is subnormal or overflows. Zero is exact. Retain all expansion components, and use the already verified noncontracting scalar operations. Do not materialize an out-of-range power-of-two scale as a float.

**Full-domain exactness.** One common float scale cannot accommodate a possible 506-exponent spread. Preserve separate exponent-tagged bands and merge/cancel them exactly. A concrete full-domain implementation can feed the exact TwoProduct components into A's canonical integer chunk merger, then use its exact sign/RN32 summary. This is a hybrid with extra representation machinery. A pure single-band version needs an explicit <=32 admission under E and does not preserve this corpus. A claimed pure full-domain expansion alternative still owes a complete inter-band cancellation and rounding algorithm. Do not accept 'the largest component decides' before canonical cancellation.

**Expected size.** A TwoSum body uses six adds/subtracts, and a standard split TwoProduct uses a small fixed collection of multiplies/adds/subtracts. Shared worker sites can keep arithmetic body size independent of the number of terms. The hybrid should have the same order of size as A plus a few hundred primitive arithmetic instructions, within the same artifact gates under C/D. A conventionally unrolled expansion sum of up to 16 components can instead produce quadratic helper duplication. It is not the scored form.

**Risk and impact.** Depends on the verified scalar rounding mode, exact scaling, compiler contraction, safe local bands, and the integer merger anyway. Retain the integer midpoint residuals under C3. Full-domain hybrid needs no numerical contract/reference change. Single-band B would need an owner-visible domain change and fails the <=32 corpus condition below.

**Effort.** Very high for an independently maintainable GPU expansion layer. The full-domain hybrid offers no code-size advantage over A demonstrated by these measurements. It is the weakest choice for this failure.

### C. Shared primitive call sites through job loops

**Exactness.** Reuse the existing constant-index signed add, two-word original-coordinate product placement and RN32 logic. A finite continuation loop selects six Edge terms or eight Dot terms in their current signs/order and calls each primitive at one syntactic site. Higher query continuations submit Edge, Dot, B30-side, Round and Div/Sqrt jobs to those sites, rather than calling a nested predicate from every branch.

For B30, retain both exact side accumulators or owned scratch until their common scale is chosen. Never reconstruct them from rounded Value fields. The job loop advances only after each result is available to the reference branch. Keep invalid/zero/same-sign crossing checks and all C3 seed correction. No approximate early-out or CPU fallback is added.

**Expected size.** Current recursively expanded primitive costs are 982 for original-coordinate product placement, 3,047 for checked signed add, and 1,631 for wide Round. One site of each is about **5,660** instructions before job selection. A shared corrected div/sqrt worker is of the same order as the measured 3,622-instruction Div. This gives roughly **10,000 arithmetic-body instructions**, independent of how many predicates a query performs. Allowing scalar graph/continuation overhead gives an estimated **15,000 to 30,000** fully inlined instructions per physical query kernel, with roughly **50 to 100 KB** of ordinary HLSL/MSL. Exact counts must be gated, not guessed.

The known accepted ExactWords probe has a 36,195 recursive count and 120,765/122,839 text bytes. The proposed caps are below that control. Keep outer query/traversal loops rolled, and spill continuation data to invocation-owned Work words if it would keep extra wide private values live. The generic 18-by-18 multiply remains unreachable in production.

**Why one level is insufficient.** One call to current `covOverlay` still expands to 1,043,599 SPIR-V instructions. One common `covEdge` site while retaining separate Dot/Crossing implementations also leaves repeated product/add/round chains. Hoisting must cover the whole exact/C3 call graph. Refactoring a helper name without changing those sites is not C.

**Risk and impact.** The numerical algorithm is already verified. New risks are continuation state, short circuit behavior, scratch ownership, code generation accidentally cloning worker bodies, and live private state. Keep the state machine specific to these finite query graphs, with typed states and explicit source-order transitions. Do not introduce a general shader bytecode VM. No numerical contract, reference algorithm or refusal-code change. Add the artifact-size and one-site invariants to the arithmetic proof and task plan.

**Effort.** Medium to high, mostly a control-flow lowering and test split. It establishes a replaceable exact-worker seam if A is later justified. It makes the fewest simultaneous changes to already proved numerical behavior, which is why it ranks first.

### D. Kernel splitting

**Exactness.** Split inside the expensive query graph, rather than merely separating Skin/Conform/Normals. For example, generate ordered candidate/predicate requests, evaluate compact batches, resume membership/height, perform ordered overlay accumulation, and finish face correction before the existing reduction. Keep private candidate output and select only after all current checks. Barriers separate producer and consumer dispatches as section 9 requires.

**Expected size.** Isolated current Edge is 25,214 fully inlined instructions. Current Dot is 33,964, and Crossing is 54,213. Splitting only to those three unmodified kernels is therefore insufficient for the proposed cap. Split Crossing into its two exact-side evaluations, normalization/Round and compact Div, and route Dot's eight polynomial terms through a shared primitive site or split them too. A primitive signed-add worker is 3,698 fully inlined instructions, Round is 2,168, and the generic multiply's 18,869 is unnecessary for these predicates. Thus a genuinely primitive-bounded split can remain below 30,000 per pipeline without changing arithmetic.

**Risk and impact.** Extra dispatches, scratch tables, capacity limits, barriers and ordered result consumption. Precomputing a predicate for a branch the reference never visits must not publish a refusal. Reductions must use the reference's consumed events and keys. Do not issue one dispatch per individual triangle or edge. Use bounded tiled batches without new CPU readback or data-dependent host loops. No numerical reference change, but new physical subphases amend the plan's exact phase list, layout and barrier tests. Preserve the seven logical stages.

**Effort.** High. More infrastructure and sequencing work than C. Useful selectively to enforce the artifact cap or contain continuation state, rather than as a blanket expansion into hundreds of dispatches. Its performance cost is unknown and is not measured here.

### E. Explicit domain admission

**Concrete domain.** For each requested Edge, Dot and each B30 exact side, ignore zero products. Decode the exponents of the six/eight original-coordinate products. Require `max(productExponent)-min(productExponent) <= 64`. All-zero polynomials pass. Keep the existing nonfinite checks first. Factoring out the minimum exponent leaves each 48-bit product shifted by at most 64, and at most eight terms need fewer than `2^(48+64+3)` units. A **115-bit/four-limb** signed-magnitude accumulator is therefore sufficient. B30 keeps each result's separate exponent until common normalization.

**Corpus evidence.** A temporary diagnostic patch observed those spreads in `EdgeExpansion` and Dot while running the actual `Every_fixture_reference_output_matches_the_hash_manifest` test. It did not change arithmetic or admission. The one focused test passed all 144 manifest rows. Across 10,792,560 observed Edge expansions/Dots, including B30's two sides, maximum spread was **58**, and **zero** calls exceeded 64. Therefore the proposed <=64 check would not change these 144 constructions. A <=32 check would reject **1,140 predicate evaluations**, and cannot be substituted while claiming unchanged frozen outputs. Counts are evaluations, not distinct cases.

`reference-spreads.json` retains the full histogram, and `reference-spread-probe.patch` contains the exact diagnostic changes and final file write. The patch was saved, then reversed with `git apply -R`. No E admission implementation exists in the tree. These results prove corpus membership, not that a future admission implementation is correct or that arbitrary player content has the same spread.

**Expected size.** Four-limb structural measurements give 749 raw instructions for signed add, 681 for Round, and 8,681 fully inlined instructions for the current Edge shape. Factoring exponents and handling shifts adds code. With C's shared product/add/round sites, a four-limb worker should be well below the full-width C estimate and the same artifact caps. Without C/D, even this smaller Edge replicated through the overlay is still too large, especially with 43 Div sites.

**Risk and impact.** This refuses currently valid inputs, including widely separated exponents whose determinant may be finite and important. It is a real contract change, not a silent implementation limit. CPU reference and GPU must test the same requested predicates, at the same traversal point, and propagate a distinct owner-approved domain-refusal code and phase/slot attribution. Amend sections 2, 3, 7 and 9 and admission documentation. Do not disguise it as F, a NaN, UnsupportedAdapter, or an existing mathematical failure. A coarse cold support-only admission would not cover posed host coordinates and could refuse unused branches, so it is not equivalent.

**Effort.** Medium with owner approval, explicit failure plumbing, reference/GPU parity tests, corpus rerun and full-domain adversarial negatives. Lower implementation effort does not compensate for losing contract fidelity when C preserves the domain.

## 6. Required task changes for the recommendation

### Task 2

- Retain the existing 18-limb diagnostics and independent integer oracle. Keep the accepted two-word midpoint residuals, native seed handling and whole-domain C3 search. Do not restore wide residuals or use backend-native div/sqrt as final answers.
- Separate the production worker composition from diagnostic-wide-probe composition. Ensure the general wide schoolbook multiply is never reachable from production predicates.
- Expose a shared lowering/worker path for Div and Sqrt so multiple scalar/vector callers do not each instantiate the correction search. Preserve public helper semantics and special values in their existing isolated tests. A root/signed-division operation tag may select the existing compact residual comparison.
- Add size invariants to every exact/rounding kernel: constant private limb indices, no dynamic vector components, rolled worker loops, and measured inline/text/private-state caps. Keep native WARP/FXC and Paravirtual acceptance as C6 gates.

### Task 3

- Add a finite exact-job worker seam and typed continuation state. Edge/Dot build their six/eight signed product sequences through one product site and one signed-add site. Keep exact sign and one RN32/F conversion. Continue testing dense results against the independent BigInteger oracle, not only rounded outputs.
- Convert `covHeight`, `covBoundary`, `covTriangleHeight` and `covOverlay` into resumable query graphs that use that seam. Preserve inclusive AABBs, cell-only source-order traversal, first bad-candidate abort, strict boundary ties, endpoint/collinear order, B30 and both pinned height trees.
- Retain both unrounded B30 sides until normalization. A continuation must never read a peer's same-phase refusal flag to suppress work or turn a missed candidate into a later hit.
- Split test probes by actual physical worker/query pipeline. The current predicate probe computes Edge/Dot both for the returned scalar and again to dump limbs, multiplying inline sites. Obtain exact diagnostics from the same result or a separate diagnostic entry point. Split the support operation switch if necessary, rather than requiring a diagnostic mega-kernel larger than production.
- Record ordinary and exhaustive-inline artifact sizes in the proof and corpus tooling. Enforce one shared heavy primitive site per worker and the 30,000-instruction/100,000-byte-per-target caps. If a graph exceeds a cap, split it before requesting native acceptance. Require 64-thread pipeline creation and word equality on FXC/WARP, Paravirtual, native Metal and Vulkan. No compile timing gate is introduced.

### Task 5

- Extend the checked Work layout and reservations with bounded, per-actor/per-element continuation and result scratch. Keep the six storage bindings and existing batch ownership. No private dynamically indexed vector or array writes.
- Compile each named physical phase separately. Lower repeated interpolation/normalization div/sqrt calls through the shared worker or a bounded split. Prevent an all-phases switch from reintroducing duplication.
- Keep source fallback available before Before, original-record flush/copy rules, group alias rules and deterministic event rows. Update the shared test session only to observe the same production phases.

### Task 6

- Lower ConformGroups and especially CorrectFaces using the resumable source-order query path. Keep the three-corner loop and candidate/edge loops explicitly rolled. Finish actual/nominal membership and discrepancy before overlay/deficit, including their short circuits and note order.
- Reuse one exact/C3 worker site across the whole physical phase. If continuation state or artifact size cannot stay under the cap, add explicit physical producer/worker/resume subphases from D, with barriers and typed Work records. Amend the internal phase list and tests, rather than silently changing RecordPhases behavior.
- Finish all face records before ReduceFaces/GatherCorrections. Preserve immutable pointwise After, first-code attribution, no peer suppression, full-target validation before transition and complete refusal behavior.

### Task 7

- Lower ValidateFaces and the two Gather paths through the shared worker for corrected Div/Sqrt and the exact orientation predicate. A separate normal/tangent entry point or basis-preparation subphase is permissible if pinned recomputation, corner order and bitwise outputs remain identical. Do not hoist or reuse a result that changes the specified scalar graph without proving equivalence.
- Preserve triangle-order orientation validation, stored gather order, tangent W, protected record copies and all-or-nothing Select. New subphase results remain private until the existing stage gates succeed.
- Run the unchanged 144-row reference-manifest gate, all 72 GPU mode-0 comparisons, all six certification-negative construction cases and the earlier construction/refusal cases. Refresh only intentional coverage shader corpus rows, preserving unrelated rows and all frozen input/manifest bytes.
- Add worst-shape artifact checks, including CorrectFaces, rather than accepting isolated helper compilation as proof of production feasibility.

### New exit gates before continuing Tasks 5 to 7

1. Root accepts the control-flow/size-gate amendment. No numerical contract amendment is requested for C.
2. A worst-shape C prototype, implemented in a later approved task, has no duplicated heavy-worker sites, no forbidden accesses, explicitly rolled loops, at most 30,000 exhaustively inlined SPIR-V instructions and at most 100,000 bytes in each ordinary HLSL/MSL target. These are proposed engineering ceilings below the known accepted control, not vendor limits.
3. Native FXC cs_5_0 compilation, Paravirtual pipeline creation at 64 threads and the independent word corpus pass. A cap pass without native acceptance is insufficient. If native acceptance fails, use D to reduce the pipeline or investigate the explicit compiler/resource result. Do not increase caps without evidence.
4. The 144 reference hashes and all required GPU words remain unchanged. Performance stays a separate owner-approved future gate. No compile/run timing experiment is authorized by this report.

## 7. Reproduction and retained work product

All newly created files are under this workspace. The harness can reproduce the original emissions with:

```bash
build-slot --label ec-size-probe-build -- dotnet build .superpowers/sdd/2026-10-19-equipment-coverage-part-c-gpu-path/size-probe/SizeProbe.csproj -c Release
build-slot --label ec-size-emission -- dotnet .superpowers/sdd/2026-10-19-equipment-coverage-part-c-gpu-path/size-probe/bin/Release/net10.0/KhaozEngine.Render.Tests.dll .superpowers/sdd/2026-10-19-equipment-coverage-part-c-gpu-path/size-artifacts
```

The inline pass is the three-flag `spirv-opt` command above, applied once per selected module. Reemit those modules with the same harness's `reemit <directory>` mode, through build-slot. `size-probe/measure.py` regenerates CSV/JSON measurements. `size-fits.txt` records fits. `size-artifacts.sha256` identifies the measured artifacts. `size-artifacts/` and `size-inlined/` retain sources/binaries/text targets. The instrumented reference test's patch, histogram and pass log are also retained. New retained logs omit incidental duration fields. No per-frame images or GPU captures were created.

The measurement harness builds passed with zero warnings/errors. The single focused reference test passed all 144 hashes. Eighteen original/inlined isolated modules validated. The temporary reference instrumentation was reversed, and the final branch/HEAD/status guard is the original branch at `f16a90107` with a clean tracked tree. No implementation, integration or release has been performed.
