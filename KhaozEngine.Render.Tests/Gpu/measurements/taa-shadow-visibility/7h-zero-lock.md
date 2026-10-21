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
The final artifact guards are the pre-commit gate. They run dashes, prose and file size with --tree, doc versions,
git diff --check and the capture manifest verifier through build-slot. The commit report records their exit code.
No manual validation is needed for the captured compiler failure. Integration remains held.
