# Task 7h Z positive-zero lock bypass

The task starts on `fix/taa-vis-zero-lock` at `b4690959e`, clean. It changes only the release call site and test
plumbing. The ordered helper, its constants and the C# twin retain their arithmetic. Version 21.11.0 is staged.
Integration belongs to the orchestrator. No timing, benchmark, stress, billed workflow, merge, tag or release is
authorized by this task.

## Exactness

The shortcut is after decay and ridge classification. It qualifies the incoming lock by its bits, exactly
`0x00000000`, and tests the exponent bits of all three actual arguments to the helper. The edge argument is first
selected by `ownReprojected && useHistory`, so a rejected raw edge value does not disqualify a selected finite zero.
Negative zero, every nonzero lock and every non-finite actual argument retain the unchanged ordered helper.

`ShaderSources.TemporalLockRelease.cs` lines 25 to 31 give the proof. Positive internal width makes every
`temporalOrdered` bitwise identity. The motion subtraction by 1 and multiplication by the finite positive rounded
third cannot form NaN from finite motion. Reactive scaling by 2 may overflow to signed infinity. Edge scaling by
1 cannot introduce a NaN. Each clamp maps finite values and signed infinities into [0, 1]. Subtracting that result
from 1 gives a finite nonnegative factor, including positive zero at the upper endpoint. The ordered products of
these factors remain finite and nonnegative. Finally positive zero times that keep factor is positive zero.
There is no other argument or path in the helper. Keeping the incoming positive-zero bits without calling it is
therefore exact. A finite-input overflow needs no wider qualification.

`TemporalResolveMath.ReleaseLock` lines 211 to 223 already evaluates those operations separately, ending in
`value * keep`. It returns the same positive-zero bits for the qualified cases. It remains unchanged, preserving
the independent original-arithmetic reference. Device-free tests compare a qualified bypass against this twin
and the existing independently written probe twin for signed zeros, subnormals, largest finite values, infinities
and NaN payloads in every argument position. Exponent and mantissa boundary cases check the finite qualification.

## V6

The diagnostic variant composes `WithoutBarriers(WithoutCheck(source))`. V1 already removes the visibility
decision. V6 additionally changes the ordered helper's twelve barriers into identities on the non-bypass branch.
It retains the positive-zero bypass, prepare/mask work, R8 production and histories, the history copy, uniforms,
bindings and draw count. No timing is run here. V1 minus V6 is reported beside the existing V0/V1 and V0/V3 pairs.
Readbacks before and after all timed blocks report colour, confidence and lock differences, and require the
boxes to retain positive-zero locks. This diagnostic can change nonzero-lock rounding and is not a production
equivalence claim.

## Verification record

The call-site and V6 contract tests first failed against the starting source. The compiled branch fact then
failed because the barriers had no surrounding selection. After implementation all 88 focused tests passed.
The twin sweep covers 923,521 exceptional combinations and 10,240 exponent-boundary comparisons. No twin or
helper arithmetic changes were needed.

| Local gate | Result |
| --- | --- |
| Focused source, compiled branch, probe and V6 facts | Exit 0, 88 passed, zero failed or skipped |
| Four backend/corpus writer classes | Exit 0, 9 passed, zero failed or skipped |
| Release solution build | Exit 0, zero warnings and errors |
| Metal correctness, probe, lifecycle and both path-share classes | Exit 0, 65 passed, zero failed or skipped |
| Initial dashes, prose, file size, doc versions and diff guards | Exit 0 for every guard |
| Old DXBC fixture red gate | Exit 1, six stale shipped pins and six missing conditional-barrier failures, helper pin passed |

The first solution build caught xUnit2031 in the new DXBC test. Using Assert.Single's predicate overload fixed
that test authoring error before the successful solution build. Each hash table changed only the ten expected
fragment entries. The corpus changed their HLSL, MSL and SPIR-V rows. No vertex, layout or prepare row moved.
Metal is Apple M2 Max. P and B wrote all 4,194,304 cases, with P/B, P/C and B/C differences all zero. The 65,420
existing positive-zero rows now also exercise largest-finite scaling in each argument position. Nonzero rows
are unchanged, preserving the inputs responsible for historical software P/B differences. Both path-share
classes report zero stored-lock differences at every sampled frame.

The one free DXBC dump run is [39627482960](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39627482960),
on scratch branch fix/taa-vis-zero-lock-dxbc-dump at f0b2b656a9a087ea66bebebd28588bf1609472fb. Only the Windows
FXC step gains KE_DUMP_VISIBILITY_DXBC=1. It selects direct3d11-native, tier push, bake false and
FullyQualifiedName~D3D11FxcValidationTests. The SHA was verified against the run. The Windows job is
119316518921 on windows-2025-vs2026 20261018.275.1, with Microsoft HLSL Shader Compiler 10.1. It finished with
31 passed, one intentional dump failure and zero skipped. The watcher returned 1 for that expected failure.
All fourteen numbered listings had contiguous indices. Carriage returns were removed and the established final
blank line retained. All seven D prime listings and both decision helpers match their prior bytes. Only the
six full shipped listings and their six GLSL source pins changed. Every listing has zero precise markings.

## Blocking FXC result

FXC flattened the source selection in all six shipped programs. Its DXBC computes the finite qualification,
executes all twelve scalar barrier ORs at conditional depth zero, then uses movc to select the released result
or the incoming lock. The positive-zero source path and SPIR-V branch are exact, but Direct3D 11 does not avoid
the ordered arithmetic. The new structural DXBC gate deliberately fails on each shipped listing. No existing
comparison bound or rule is weakened to hide this result.

| Shipped program | Barrier OR instructions before / after | Scalar barriers before / after | Conditional depth before / after |
| --- | --- | --- | --- |
| Accumulate full | 12 / 12 | 12 / 12 | 0 / 0 |
| Accumulate half | 12 / 11 | 12 / 12 | 0 / 0 |
| At-display-size full | 11 / 12 | 12 / 12 | 0 / 0 |
| At-display-size half | 12 / 12 | 12 / 12 | 0 / 0 |
| Fused full | 12 / 12 | 12 / 12 | 0 / 0 |
| Fused half | 12 / 11 | 12 / 12 | 0 / 0 |

Virtual temp counts remain 36, 38, 34, 36, 46 and 47 respectively. Every shipped listing retains twelve gathers.
The D prime barrier instruction counts remain 12, 12, 12, 12, 12 and 11, all twelve scalar barriers, unconditional.

A compiler branch-control change would alter the programs and need another DXBC recapture. The brief allows
exactly one dump run, so shader changes stop here for the owning orchestrator. No second dump or all-leg
correctness workflow is dispatched. No timing result or performance saving is claimed. The candidate and the
failing structural proof are preserved on the named task branch for review, not integration.

The final device-free selection finished with exit 1, 883 passed, six failed and 224 expected GPU-off skips.
The only failures are TwelveScalarBarrierOrsAreConditionalOnlyInShippedPrograms for the six full programs above.
All existing DXBC reader, source-pin, comparison and mutation facts pass. The source/SPIR-V branch checks,
finite/exceptional twin sweeps and all nine writer checks with writer flags unset also pass.

The scratch branch was deleted remotely and locally, and its worktree removed after import. The full formatter
is not run after this blocker. The known ContourTests.cs formatting issue #1485 is not changed. No all-leg
correctness run is dispatched, so there are no Metal/WARP/lavapipe correctness counts to claim for this candidate.
The last solution build after preserving the blocked candidate finished with exit 0, zero warnings and errors.
The final artifact gate finished with exit 0. Dashes, prose and file size with --tree, doc versions,
git diff --check and the capture manifest verifier all passed through build-slot. The complete guard invocation
took 1234.425 seconds. The commit hook also passed the staged guards, including the four newly added files.
Candidate commit 8efe31117741c723426bcef67a162b9c1310d216 is pushed to fix/taa-vis-zero-lock, with the remote SHA
verified. This evidence-only closeout does not change the candidate programs.
No manual validation is needed for the captured compiler failure. Integration remains held.

## Round 2 selection control

Round 2 keeps the exact qualification and the ordered helper arithmetic. The only production behavior change
is `[[dont_flatten]]` on the existing fallback selection. Both temporal precision headers first require
`GL_EXT_control_flow_attributes`. The isolated B probe enables the same extension before including the extracted
production call site. No `precise`, test seam, compiler option or comparison bound is added.

The extension and attribute are in `ShaderSources.TemporalPrecision.cs` and `ShaderSources.TemporalAccumulate.cs`.
`SpirvFrontEnd.Compile` installs the existing shaderc pins explicitly: Performance optimization, Vulkan 1.0,
SPIR-V 1.0 and debug information off, then calls `CompileIntoSpv`. All ten release-bearing programs compile under
those options. `TemporalLockZeroBypassTests` locates their twelve barrier ORs in one fallback block and asserts
that its `OpSelectionMerge` has selection-control mask 2, `DontFlatten`.

`SpirvCrossCompile.GlslPairToHlsl` uses those modules. Its existing `Configure` selects shader model 5.0 and the
existing coordinate options. No additional emission option is required: every includer emits exactly one
`[branch] if` block containing all twelve barrier OR expressions. Removing only `[[dont_flatten]]` removes that
HLSL attribute. This exercises the actual pinned native compiler rather than assuming a default from newer
upstream source. SPIRV-Cross's [HLSL emitter](https://github.com/KhronosGroup/SPIRV-Cross/blob/main/spirv_hlsl.cpp)
maps `HintDontFlatten` to `[branch]`, consistent with the
[GLSL extension](https://github.khronos.org/Vulkan-Site/glslext/latest/glslext/ext/GL_EXT_control_flow_attributes.html)
and Microsoft's [HLSL selection control](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/dx-graphics-hlsl-if).
`D3D11ShaderBuild.Pair` sends this HLSL to its unchanged FXC compile path.

`MetalShaderBuild.Pair` compiles the same SPIR-V through the MSL emitter. Regeneration found MSL byte-identical,
including all ten temporal fragments, because this emitter drops the advisory hint. Only ten HLSL fragment rows
and ten SPIR-V fragment rows change in their hash tables and the corpus. Every vertex, layout, prepare row and
MSL row is unchanged. `VulkanShaderSet` passes the shared front-end modules through its module cache to Vulkan,
without cross-compilation. Its hashes therefore record the changed selection-control word.

The new source and selection-mask assertions failed first, with 2 passed and 3 failed. The unchanged arithmetic
twins passed. After the hint, all 19 source, compiled, probe-text and bypass tests passed. All nine existing
writer tests passed. The old DXBC red gate then had exactly 12 failures: six source-pin cases and the six
unconditional-barrier cases, with the decision-helper pin passing. The first import also updated six D prime
GLSL source pins for the unused extension. The broad device-free gate caught that header escaping the frozen-D
rewrite: six original shipped-D text pins failed, and the seventh failure was the measured temporary count
change from 34 to 35. The rewrite now strips the unused header along with the attributed call site. All frozen-D
text pins, D prime source pins and D prime listing bytes remain unchanged. Only the measured temporary count is
updated. The six DXBC branch facts already passed in that first broad run, which had 883 passed and 224 expected
GPU-off skips.

### Round 2 DXBC recapture

The single free dump is [39636752557](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39636752557), job
119349307880, on scratch head `0ecdd5e2ac98dfbf7624048153079931defadb45`. Its only change over candidate
`d26c19034ef6eb32b0f0ae83e637710829ee37ef` is enabling `KE_DUMP_VISIBILITY_DXBC=1` on the Windows FXC step.
The run's head SHA matched the resolved and pushed scratch head. It selects direct3d11-native, tier push,
bake false and FullyQualifiedName~D3D11FxcValidationTests. Windows is windows-2025-vs2026 20261018.275.1, with
Microsoft HLSL Shader Compiler 10.1. It finished with 31 passed, one intentional dump failure and zero skipped.
The watcher returned 1 for that failure.

All fourteen numbered listings had contiguous indices. The first import attempt rejected the missing retained
terminal blank line before writing any fixture. Restoring the existing newline convention made all seven D
prime listings and both decision helpers byte-identical. The six shipped listings and their six GLSL source pins
change. D prime's original pins and listings remain valid after the header restoration. Every listing has zero
precise markings. Every shipped program retains twelve gathers.

| Shipped program | Barrier OR instructions before / after | Scalar barriers before / after | Barrier depth before / after | Instruction slots before / after | Virtual temps before / after |
| --- | --- | --- | --- | --- | --- |
| Accumulate full | 12 / 12 | 12 / 12 | 0 / 1 | 1431 / 1432 | 36 / 36 |
| Accumulate half | 11 / 12 | 12 / 12 | 0 / 1 | 1427 / 1429 | 38 / 38 |
| At-display-size full | 12 / 12 | 12 / 12 | 0 / 1 | 1402 / 1403 | 34 / 35 |
| At-display-size half | 12 / 12 | 12 / 12 | 0 / 1 | 1401 / 1402 | 36 / 36 |
| Fused full | 12 / 12 | 12 / 12 | 0 / 1 | 1798 / 1799 | 46 / 46 |
| Fused half | 11 / 12 | 12 / 12 | 0 / 1 | 1793 / 1795 | 47 / 47 |

Each release shift and all twelve barrier ORs are now inside the fallback's real `if_nz` block. The qualified
positive-zero side bypasses that block. D prime retains its unqualified twelve scalar barriers at depth zero.
These are compiler-shape facts, not timing or speedup measurements.

### Round 2 local correctness

The Release solution build completed with exit 0, zero warnings and zero errors. The final device-free selection
is Temporal, ShaderSources, ShippedShader, Spirv, Corpus and LockRelease, plus both emitted-language equality
classes, excluding Perf and Bench names. GPU facts are off and every writer flag is unset. It completed with
exit 0, 890 passed, zero failed and 224 expected skips. All six DXBC branch facts pass, and every current-source
pin matches. The existing DXBC comparison and mutation facts retain their original rules.

Metal ran with KE_GPU_TESTS=1 and KE_GRAPHICS_BACKEND=metal-native on Apple M2 Max. Both the initial run and the
final run after restoring the frozen-D header passed all 55 selected tests, with zero failed or skipped. The
selection includes TemporalLockReleaseProbe, TemporalEntryIdentityGpuTests, TemporalVisibilityResolveAbGpuTests,
TemporalVisibilityEquivalenceGpuTests, TemporalVisibilityGatherGpuTests, TemporalResolveCostPathShareGpuTests
and TemporalShadowedFramePathShareGpuTests. P and B each wrote all 4,194,304 probe cases, with P/B, P/C and B/C bit
mismatches zero. The two path-share classes reported twenty stored-lock samples, all zero differences from the
production scenes rendered beside them. No timing class ran.

The scratch dump branch is deleted locally and remotely, and its clean worktree is removed. Downloaded dump logs
and emitted corpus artifacts were deleted after the metrics and record were read.

The full `dotnet format KhaozEngine.slnx --verify-no-changes --no-restore` ran to completion through build-slot.
It returned 2 after 261.707 seconds, with only the two allowed main-baseline ContourTests.cs whitespace errors at
148:30 and 150:55, tracked by #1485. The historical generic workspace-loading warning also appeared. No changed
file has a formatting diagnostic, and the baseline errors are not changed.

The artifact gate returned 0 after 511.466 seconds. Dashes, prose and file size with --tree, documentation
versions, git diff --check and the capture-manifest verifier each returned 0. The manifest lists every file in
this measurements folder with its current hash. All historical source/package identities and capture checks
remain valid. The updated record's final hash is reverified before its commit.

The final Release solution build after the fixture and oracle-header updates also returned 0, with zero
warnings and errors, in 25.992 seconds through build-slot.
