# R3-lite withdrawal and outside-cost controls

**Superseded.** The withdrawal below was reverted the same day. Its cost premise measured the mask's overhead
without its benefit, and the T4 Direct3D 11 confidence difference did not depend on the mask. The plan's Task 7f
2026-10-21 correction records the restoration and the final NVIDIA Direct3D 11 comparison rule. The
outside-cost controls described here remain.

The 2026-10-21 ruling withdraws the pass-one current-neighbour mask. T4 Direct3D 11 entry identity passed at
v21.7.1 and 7f.2 (`f8c4c402f`), then failed after 7f.2. Production confidence differed from frozen D by up to
32 half steps. The mask measured within 0.1 ms on all three decomposition legs, so it has no demonstrated
benefit that justifies the compiler coupling across programs.

Prepare and fused entry no longer compute that mask. PreparedSurface.w is zero again. Pass two reads current
motion and depth as at 7f.2, with 59 fast-path fetches or 43 under the range skip. R1, R2 with the binade rule,
R4, the LF generator, TrySelect and the allocation fix remain. Mask-only facts and the inline mask probe are
removed. The shared tolerance-edge depth inputs stay in the A/B oracle. Frozen D texts, full-program pins and
DXBC fixture provenance pins are unchanged.

Entry identity compares colour, confidence, locks, R8 and depth exactly on every backend. Frozen-D A/B remains
exact on IEEE-preserving backends. Only hardware NVIDIA Direct3D 11 permits up to two representable half-float
steps per colour and confidence channel. Stored lock bits, R8 and stored depth remain exact. Every NVIDIA
frame prints per-channel counts and maximum steps. D's paired fused/split comparison is diagnostic only and
never sets or expands that allowance. This replaces the measured-envelope rule from `e639ef2d6`.

## Outside-cost variants

Both scenes (boxes and moving thin field) remain at 2560x1440, Native and Quality, fused and split. Each variant
owns a synchronized scene and history and takes part in the same fifteen rotating rounds. Nothing compiles
inside a measured block. Off is the existing drift control. No variant or timing test was executed for this change.

| Variant | Injection before the first resolve |
| --- | --- |
| V0 shipped | Normal scene and renderer, without a fragment rewrite. |
| V1 check removed | `WithoutCheck` replaces only the check expression with false. |
| V5 visibility infrastructure off | The scene test seam requests the existing MRT layout without visibility. The renderer and split omit the three visibility bindings, history allocation, uniform updates and history copies. `WithoutVisibility` removes the shader check, helper and visibility declarations. |
| V4 V5 without precise | The V5 seams plus `WithoutVisibilityOrPrecise`, which removes exactly the two precise markings in the shared lock-release function. Device-free compilation checks that its NoContraction decorations disappear. |
| V3 frozen D | The existing full-program frozen-D rewrite, with the shipped visibility infrastructure retained. |

The output prints V0 minus V1 (check), V1 minus V5 (visibility infrastructure), V5 minus V4 (precise), and
V0 minus V3. V2 is retired with R3-lite. Wiring checks inspect real scene state, resource sets and recorded
texture copies. The default production path remains enabled and uses the original shader bindings.

## Verification record

Release solution build passed with zero warnings and errors. The documented shader writers passed 9/9 with no
skips. All scratch logs and TRX files are under `/tmp/grimhollow-taa-unmask`.

Exactly eleven fragment programs changed in each HLSL, MSL and SPIR-V table. The corpus changed their 33
fragment-emission rows. No vertex, reflected layout, program inventory or historical corpus row changed.

- TemporalPrepare
- TemporalResolve and TemporalResolveHalf
- TemporalAccumulate and TemporalAccumulateHalf
- TemporalAccumulateAtDisplaySize and TemporalAccumulateAtDisplaySizeHalf
- TemporalDebugView and TemporalDebugViewHalf
- TemporalCountProbe and TemporalCountProbeHalf

TemporalPrepare returned exactly to its 7f.2 hashes in all three backend tables and all corpus rows. The prepare,
fused resolve and shared accumulation source files also match 7f.2, apart from comments outside the GLSL. The
fast-path generator restores only the current-side block and retains the later binade rule and LF generation.

TemporalPrepare fragment hashes, each identical to 7f.2

| Table | SHA-256 |
| --- | --- |
| hlsl-hashes | `a4674786f948f1e165cedcdc120538c551c9ec2583a20bec1da4bd17a2430be0` |
| msl-hashes | `15aeb5d87d1d283f49d437e92fe8a9a92e4071336a77bf9dbb5523fb75a779fe` |
| spirv-hashes | `5f1e1dc41810830ecd36a0dc4c3aedd5d0e57bf7f459643b058cf13259015e57` |

The device-free selection executed 243 cases, 236 passed and 7 failed, zero skipped. Every failure is
`TemporalVisibilityDxbcRealProgramTests.EachFixtureWasCompiledFromTheCurrentGlsl`. The stale listings are
DecisionHelper.d and DecisionHelper.shipped, and the shipped TemporalResolveFrag, TemporalResolveHalfFrag,
TemporalAccumulateFrag, TemporalAccumulateHalfFrag, TemporalAccumulateAtDisplaySizeFrag and
TemporalAccumulateAtDisplaySizeHalfFrag. The six full-program D pins still match. All frozen-D fixture pins,
compiled counts, fast path, DXBC reader and comparison/mutation checks, binade, shader tables, corpus, resolve,
wiring and outside-cost rewrite checks pass.

These eight listings require a Windows FXC dump. No listing or pin was manufactured or repinned. The fixture
fact remains red with old/current hashes and recapture instructions. The proposed T4 correctness filter does not
recapture DXBC, so a genuine Windows dump remains a separate pre-integration requirement.

The broad Metal gate uses the corrected TAA_CORRECTNESS filter (Perf and Bench excluded), plus
MotionTargetLifecycleGpuTests. The device-free TemporalVisibilityDxbcRealProgramTests class above is excluded
from the GPU process, not skipped or hidden from verification. Its failing source pins are already recorded
separately. No timing test may appear in discovery.

The single broad Metal process passed 1656/1656, zero failed and zero skipped, on Apple M2 Max in 2.1952
minutes. It includes 12 frozen-D A/B cases, 8 exact entry-identity cases, 6 equivalence cases, 4 visibility-free
control cases and both MotionTargetLifecycleGpuTests cases. Every A/B frame set printed byte-identical colour,
state, R8 and depth. Equivalence stayed exact. Both V5 and V4 rendered on Metal through fused and split at
Quality and Native without visibility targets or history allocation.

MotionFrameAllocationTests passed 5/5, zero skipped. The steady temporal-frame allocation assertions remain
zero across GPU/CPU skinning and resolve/motion-only paths. Full-solution `dotnet format --verify-no-changes --no-restore` returned zero. Both formatter commands emitted
workspace-load warning summaries. The Release compiler emitted zero warnings. Whole-tree dashes, prose and
file-size checks, documentation versions and `git diff --check` all returned zero. No size baseline changed.

Verification artifacts, SHA-256

| Artifact under /tmp/grimhollow-taa-unmask | SHA-256 |
| --- | --- |

The fallback seam behavioral RED failed on both entries because an R8 MRT was still allocated. The generator
writer passed in that run. An earlier test setup compile failed for a missing Internal namespace import, fixed
before the behavioral RED. No timing fixture ran.

| build.log | `3860763133609c02d512e66ad9aa4ad374c168ef4c4e65caa5199d611e9c64b8` |
| writers.trx | `350033705b40ab76e5b9ad5fa6b260139d5455ec3f124586ee4761e38a309aad` |
| cpu.trx | `fc162689421520515f7d205ecdd17a9949d1c9d8370b6685572770bcbf9b1cc0` |
| metal.trx | `a00aa547bdeabd5047515b0c0db334af5890a2aa62693f33263b84cc37053d24` |
| allocation.trx | `b3e6f0adbc75d62c49ff378163d788d9833c35127ed8dbbbd20e7d7c71b6981e` |
| format-verify.log | `239c224f01960886368326d9ef5ebdcce071dcd0996d157bef6a963fc1eaa49c` |
| guards.json | `79c9ac080f4c380bfbc11e404db25ffbd37381b7bab46e401b9c08a328e00cad` |

No timing, perf or bench test ran, no workflow was dispatched, and there was no merge, tag or release. The
outside-cost numbers, NVIDIA D3D11 post-withdrawal behavior, current FXC listings and exact entry identity on
other unrun backends remain unverified. The two T4 requests require fresh owner approval.
