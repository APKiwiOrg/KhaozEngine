# Exact temporal entry identity

Task 8 local proof for #1501. The implementation and exact Metal entry contract are GREEN.
The two owner-deferred appearance gates remain RED. Hosted confirmation is still required.

## Measured introducing commit

Each checkpoint used a clean detached scratch worktree, a Release build and one filtered GPU run of
`TemporalEntryIdentityGpuTests`. The scratch worktree was removed after measurement. The task branch
was never switched. Every build and test used `build-slot`, with `--heavy` for GPU runs.
The device was MetalNative on Apple M2 Max. TRX confirms zero skipped GPU facts in every run.

| Commit | Meaning | Passed | Failed | Exit |
| --- | --- | --- | --- | --- |
| `1472e9609` | Before visibility code | 4 | 0 | 0 |
| `89ec308c3` | Task 4 lit visibility output | 4 | 0 | 0 |
| `b42e2e2b6` | Task 5 visibility history and previous jitter | 4 | 0 | 0 |
| `0fd8418fb` | Task 6 visibility release | 0 | 4 | 1 |

`0fd8418fb` is the measured introducing commit. Its failures reproduce #1501 exactly.

| Case at the introducing commit | Colour differences | Lock differences | Confidence and marks |
| --- | --- | --- | --- |
| Performance | 2191 values, 28 frames from frame 20, maximum 0.010253906, 21 half steps | 8 values, 3 frames from frame 27, maximum 0.00024414062, 2 half steps | Exact |
| Quality | Exact | 15 values, 8 frames from frame 22, maximum 0.00048828125, 4 half steps | Exact |
| Native | Exact | 34 values, 18 frames from frame 17, maximum 0.00048828125, 2 half steps | Exact |
| Outlined Quality fact | Exact in its plain control | 15 values, 8 frames from frame 22, maximum 0.00048828125, 4 half steps in its plain control | Exact |

The outlined fact first runs its mandatory plain Quality control. Before the repair it fails there,
so that RED does not claim the outlined draw was reached. After the repair both halves execute.

## Root cause and causal proof

The carried-lock release expression had no precision boundary. Task 6 added another use of the
lock value before visibility can clear it. With that program shape the compiler was free to
reassociate and contract the shared lock-release arithmetic differently in the fused and split
programs. This is arithmetic identity failure, rather than differing visibility input, previous
jitter, a split early return or a different release rule.

The expanded long-walk tests reproduce the same state and colour differences while checking exact
R8 storage and previous jitter on every resolving frame. No binding, history-copy, uniform, release
condition or half-rounding change was needed to repair them.

The first experiment marked the local `lockValue` precise. All 10 selected facts passed, but emitted
MSL showed that its dependency chain also constrained history sampling and other upstream work.
That broad experiment was discarded.

The final calculation is a shared `temporalReleaseLock` helper in
`KhaozEngine.Render3D/Internal/ShaderSources.TemporalAccumulate.cs`. Its ordinary float parameters
bound the precision propagation. Only its motion-release calculation and lock product use `precise`.
A focused run with this narrower boundary passed all 8 selected facts, including the seven identity
facts and the shader-corpus emission fact.

A counterfactual then removed only the helper's two `precise` qualifiers. The helper, tests, inputs,
visibility decisions and all other production code stayed unchanged. The three moving-shadow facts
passed, and all four inherited identity facts failed with the exact counts and magnitudes in the
table above. Restoring the qualifiers restored exact identity in the final focus and guard runs.
This isolates the missing operation-order boundary in that shared arithmetic.

Emitted MSL provides the compiler evidence. Without the boundary, the lock product is ordinary
multiplication and subtraction, and motion release uses multiplication by `0.3333333432674407958984375`.
With the boundary, the lock product and its factors use SPIRV-Cross `spvFMul` and `spvFSub` helpers
with `clang::optnone`, and motion release retains division by `3.0`. Calls to those helpers are
confined to this lock calculation. Reconstruction, history sampling and surface preparation keep
their previous arithmetic. This identifies the affected optimizer freedom without claiming a
particular final native machine instruction was disassembled.

## Fix and integration coverage

- Both entries use the same bounded lock-release helper, operands and multiplication order.
- Main's integer-step `temporalHalf` construction is unchanged.
- The existing software-Vulkan colour limits remain exactly `0.0625` and `0.0025` of compared values.
  There is no visibility or state exception, and no new tolerance for hardware colour.
- The inherited long walk now compares stored R8 bytes and previous jitter every resolving frame,
  alongside bit-exact colour, confidence, lock and mark checks.
- `MovingShadowEntriesKeepVisibilityDecisionsAndHistoryExact` drives uploaded moving shadow edges
  through the real fused and split renderers at Native, Quality and Performance. It compares R8
  bytes, prior R8 history, previous jitter and bit representations of colour and state every frame.
  An unchanged frame retains the seeded 0.875 lock and full confidence. Each of two large rises
  releases the lock and reproduces cold-history confidence and colour exactly. Each of two falls
  releases the lock while retaining full confidence. The inherited outlined Quality scene still
  exercises the actual outline pass and verifies its magenta history contribution.
- `EntryFlipKeepsVisibilityBytesExact` interleaves entries and display sizes 8, 12 and 16. Twelve
  resolving frames and twelve opposite-entry repeated renders preserve exact stored/read R8 bytes
  and previous jitter. Resize resets comparison readiness and saved jitter.
- `DebugAndCountsReadTheSamePreWriteVisibilityAsTheResolve` covers both entries. A completed write
  contains 255 while the read history remains zero. The History debug view reproduces the resolve's
  confidence `0.06665039` with zero locks, and all 9216 count samples report clipping. Reading the
  new write instead would retain a carried lock and full confidence, so the assertions distinguish
  pre-write history from post-write history.
- The existing shader-source contract follows the extracted helper and retains its own-motion
  suppression check. There is no production diagnostic hook or entry-specific rule.

## Final identity results

| Case | Resolved frames | Colour | Confidence | Lock | Mark | R8 and previous jitter |
| --- | --- | --- | --- | --- | --- | --- |
| Native walk | 48 | Exact | Exact | Exact | Exact | Exact |
| Quality walk | 48 | Exact | Exact | Exact | Exact | Exact |
| Performance walk | 48 | Exact | Exact | Exact | Exact | Exact |
| Outlined Quality control | 48 | Exact | Exact | Exact | Exact | Exact |
| Outlined Quality draw | 48 | Exact | Exact | Exact | Exact | Exact |
| Native moving shadow | 6 | Exact | Exact | Exact | Exact | Exact |
| Quality moving shadow | 6 | Exact | Exact | Exact | Exact | Exact |
| Performance moving shadow | 6 | Exact | Exact | Exact | Exact | Exact |

The outline draw stores 1942 magenta history pixels on its final frame against zero in the control.

## Verification

All commands ran synchronously, one build or test command at a time. GPU runs used
`KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native`, Release and `--no-build` after a Release build.
Counters below were read from completed TRX results. GPU selected counts include zero skips.

| Run | Total | Passed | Failed | Skipped | Exit |
| --- | --- | --- | --- | --- | --- |
| Narrow-helper causal RED without precise | 7 | 3 | 4 | 0 | 1 |
| Required Task 8 GPU focus | 50 | 49 | 1 | 0 | 1 |
| Required non-shadow guard | 61 | 61 | 0 | 0 | 0 |
| Residual moving-rim check | 2 | 1 | 1 | 0 | 1 |
| Shader-table and corpus bake | 9 | 9 | 0 | 0 | 0 |
| Solution CPU selection | 5662 | 5369 | 0 | 293 | 0 |

The final GPU runs have 113 test executions, 111 passes and only the two appearance facts already
deferred by the orchestrator. There are no new failing GPU facts.

The CPU command was `dotnet test KhaozEngine.slnx -c Release` with this filter and `KE_GPU_TESTS` unset.
It built the solution and exercised all matching CPU classes across the solution.

```text
FullyQualifiedName~Temporal|FullyQualifiedName~Motion|FullyQualifiedName~Binding|FullyQualifiedName~Uniform|FullyQualifiedName~Ubo|FullyQualifiedName~Wiring|FullyQualifiedName~History|FullyQualifiedName~Shader
```

Of the 293 skips, 291 are env-gated GPU facts. The other two existing skips are
`SqlServerContentAuthoringStoreTests.ASecondPublishClosesTheOldRevisionAndLeavesBothInTheHistory`
(requires `KE_CATALOG_SQLSERVER`) and
`WallContactOwnColumnTests.The_perturbation_control_for_every_sensitivity_claim_in_this_chain`
(an edit-dependent measurement). They are not CPU passes or GPU proof.

The required GPU focus was exactly the brief's four-class filter. The non-shadow guard was exactly
the six-class filter supplied in the dispatch. The residual check selected
`FullyQualifiedName~TemporalMovingShadeRimGpuTests` once. No hosted workflow or timing run was started.
Release render builds completed with zero warnings and zero errors.

## Residual gate movement

| Deferred measurement | Before identity correction | After identity correction | Status |
| --- | --- | --- | --- |
| Quality textured grain | 0.008031 | 0.008031 | RED against 0.008 |
| Quality M 1.0 leading textured error | 0.0030106876217401947 | 0.0030106876217401947 | RED against 0.00271205 |
| Quality M 1.0 leading textured temporal error | 0.004819472651560228 | 0.004819472651560228 | RED against 0.00432135 |

There is no movement in either deferred gate. The rim row's raw change is
`0.03180997173537265` and reference change is `0.0318161181303171`.

## Files changed and shader rebake

Production arithmetic changes only in
`KhaozEngine.Render3D/Internal/ShaderSources.TemporalAccumulate.cs`.

Test changes are in `KhaozEngine.Render.Tests/Gpu/TemporalEntryIdentityGpuTests.cs`,
`KhaozEngine.Render.Tests/Gpu/TemporalEntryFlipMemoryGpuTests.cs`,
`KhaozEngine.Render.Tests/Gpu/TemporalVisibilityHistoryGpuTests.cs`,
`KhaozEngine.Render.Tests/Gpu/TemporalVisibilityComparisonGpuTests.cs` and
`KhaozEngine.Render.Tests/Render3D/TemporalResolveShaderTests.cs`.

The local rebake updates `KhaozEngine.Render.Tests/Gpu/hlsl-hashes/d3d11-hlsl.sha256.txt`,
`KhaozEngine.Render.Tests/Gpu/msl-hashes/metal-msl.sha256.txt`,
`KhaozEngine.Render.Tests/Gpu/spirv-hashes/vulkan-spirv.sha256.txt` and
`KhaozEngine.Render.Tests/Gpu/shader-corpus/corpus.txt`.

Exactly these ten fragment programs move in all three hash tables and the current corpus.
No vertex program, prepare pass or binding-layout row moves.

```text
TemporalAccumulate
TemporalAccumulateAtDisplaySize
TemporalAccumulateAtDisplaySizeHalf
TemporalAccumulateHalf
TemporalCountProbe
TemporalCountProbeHalf
TemporalDebugView
TemporalDebugViewHalf
TemporalResolve
TemporalResolveHalf
```

## Self-review and concerns

The diff preserves thresholds, half rounding, stored marks, release ordering, history ownership,
R8 format, resource bindings and the existing software-Vulkan exception. No baseline or tolerance
was increased. The precise boundary is local to the demonstrated defect, and its necessity was
checked by removing only the qualifiers. Test helpers remain in the test project.

An early debug test used a new command recording without preparing that recording's ring-buffer
uniform slice. Its two test-setup failures were corrected before judging the debug contract.
The final test prepares its own slice while retaining the original previous jitter. The counts
are asserted in samples, the unit the production probe returns. The four identity failures, rather
than those setup errors, are the behavioral RED evidence.

Hosted Direct3D 11 FXC/WARP and Vulkan confirmation remains required, especially software-Vulkan
colour and exact state. The local shader rebake is compilation evidence, not hosted pixel proof.
No timing claim is made. The owner still decides the two appearance gates in Task 10.
Issue #1501 remains open until this branch lands on main. Integration, release, versioning and
consumer adoption belong to the orchestrator.

## Repository verification

Staged dash, prose and file-size guards pass. Whole-tree dash, prose and file-size guards pass.
The documentation-version guard passes, with no version or changelog changes.
The final targeted whitespace verification exits zero and reports zero files changed.
An earlier format workspace load emitted a warning. Diagnostic output identifies an analyzer
project reference without matching metadata for
`KhaozEngine.Localization.Analyzers/KhaozEngine.Localization.Analyzers.csproj` in the formatter's
Debug workspace. The Release builds and solution CPU selection remain clean.
