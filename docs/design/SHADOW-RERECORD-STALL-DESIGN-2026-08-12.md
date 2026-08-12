# Shadow re-record: what the pass actually costs, and what would remove it (2026-08-12)

Issue: [#410](https://github.com/APKiwiOrg/KhaozEngine/issues/410). Instrument: `Scene3D.LastShadowPassDiagnostics`
(17.36.0). Bench: `KhaozEngine.Render.Tests/Gpu/ShadowRerecordBenchGpuTests.cs`.

This doc is the design half of the #410 attack. It weighs the ways to stop the key-light depth pass re-recording
the whole atlas on a scene where nothing a viewer can see has changed, and it recommends one. No code lands from
it: the bench that produced its numbers is the only thing this branch ships, and rendering behaviour is untouched.

Read section 2 before section 3. Two of the four options look obviously right and are not, and both times the
reason is the same coupling: the atlas is a persistent texture the receivers sample through the matrices that
recorded it, so anything that changes what is recorded also constrains what the receiver may be told.

## 1. What is measured

### 1.1 The bench

One scene, built to the shape of the Windows field trace rather than to a round number: 1,525 rigid shadow-caster
instances inside 4,593 drawn instances, chunk-major over a 500 m disc, four cascades at 2048, one skinned caster,
GPU skinning on. It fragments into 404 to 406 rigid spans over the four cascades (96, 96, 95, 119), against the
field trace's 707 to 789 (45, 153, 221, 285). Same order, same shape, one machine's worth of scene.

It renders that scene under the four shadow decisions the diagnostics can name, interleaved round-robin in one
process so no configuration owns the warm-up or the drift, and reduced by median over 200 measured frames each.
The dirty-reason facts are asserted. The milliseconds are printed and gate nothing, because a wall-clock threshold
on a shared machine is a coin flip.

### 1.2 The numbers, local Metal (Apple silicon, Release)

| configuration | shadow mean ms | shadow median ms | frame median ms | spans | draws | us/span |
|---|---|---|---|---|---|---|
| shadows off | 0.000 | 0.000 | 1.255 | 0 | 0 | - |
| skinned caster, sun frozen | 0.300 | 0.232 | 2.429 | 406 | 410 | 0.74 |
| rigid only, sun frozen (skips) | 0.062 | 0.048 | 1.541 | 0 | 0 | - |
| rigid only, daylight sun | 0.235 | 0.204 | 2.305 | 404 | 404 | 0.58 |
| rigid only, 0.001 deg sun | 0.248 | 0.204 | 2.314 | 404 | 404 | 0.61 |
| skinned caster, sun frozen, pipelined | 0.248 | 0.227 | 0.847 | 406 | 410 | 0.61 |

Five things fall out of that table, and they are the inputs to every option below.

**The re-record costs 0.24 ms of CPU encode and about 0.65 ms of GPU, per frame.** Encode above the skip floor is
0.300 - 0.062 = 0.237 ms mean (0.184 median). Whole-frame above shadows-off is 1.173 ms when it re-records and
0.286 ms when it skips, so the re-record's whole-frame premium is 0.887 ms and only a quarter of that is the CPU
recording the pass timings measure. **On this backend the re-record is GPU-dominated.** Any option judged only on
the encode number is being judged on a quarter of its saving.

**A skipped frame is not free: it pays 0.062 ms.** `BuildShadowCasterSpans` runs before the dirty check on every
frame, over every caster, because the signature it builds is what the compare needs. That is the floor. No option
in this doc removes it, and at the field machine's 2,200 candidates it is proportionally larger.

**Movement size does not change the price.** Ruinborne's real daylight rate (0.00333 deg/frame) and a rate
one third of it (0.001 deg/frame) cost 0.235 and 0.248 ms and record 404 draws each. The cascade compare is exact
matrix equality (`ShadowCascadeVpsChanged`), so there is no sun movement small enough to be ignored, and the bench
asserts that: `LightMatrixChanged` is true on all 200 frames of the 0.001 deg configuration.

**One skinned caster forces the other 406 spans.** The skinned caster contributes 4 of the 410 draws and dirties
100 percent of the pass. In the field trace the ratio is 17 to 21 skinned casters, about 80 of about 790 draws:
a tenth of the work forcing the other nine tenths.

**Recording does not block on the GPU here.** The pipelined configuration records the identical frame with the
queue deliberately loaded and encodes in 0.248 ms mean / 0.227 median against the drained twin's 0.300 / 0.232.
No inflation, so on Metal `ShadowDepthMs` means what it says: CPU time.

### 1.3 What the local numbers can and cannot say about the field

They corroborate the field's Metal column and nothing else. 0.74 us/span here against the trace's 1.01 us/span on
a different Mac and a different scene is the same quantity twice, which is the useful part: the bench reproduces
the machine it runs on.

They cannot settle the Windows question, and do not need to. #410's own trace analysis already settled it from
Veldrid's source rather than from a timer: `D3D11CommandList` records into a deferred context created by
`CreateDeferredContext()`, recording into a deferred context never touches the GPU, so there is nothing there to
block on. The 10.95 us per command call measured on the reporting machine was CPU. What this bench adds is the
control: the same pass shape on a healthy backend costs 0.6 us per span, so the D3D11 number was 40x a per-call
cost rather than a pass that was asking for too much.

**And the field has moved.** After #418's bind batching the same machine records the shadow pass in 0.35 ms and
holds 125 fps flat. So the re-record is a sub-millisecond item on every backend currently measured, and nothing
in section 3 should be sold as the fix for the 10 fps plateau. It is worth doing on its own merits, which are
the 0.9 ms whole-frame premium above and the fact that a stationary scene is paying it for nothing.

## 2. The mechanism, from the code

The decision site is `Scene3D.cs:1948-1995`. Per frame, when the tier is `ShadowMap`:

1. `SetShadowReceiverTail()` fits every cascade from the CURRENT camera and light, writes `_cascadeCpuVps` /
   `_cascadeDepthVps`, and uploads the receiver's matrices. Unconditional, before any dirty check.
2. `BuildShadowCasterSpans` builds this frame's caster draw list and the signature. Unconditional.
3. Three compares plus two flags feed `ShadowDepthPassDirty` (`Scene3D.ShadowCasters.cs:197`), which is a plain
   `||` of: no previous atlas, any skinned caster, resolution changed, light matrix changed, caster data changed.
4. If dirty, `BuildCascadeCasterSpans` splits the list per cascade and `RenderShadowDepthPass` clears the WHOLE
   atlas and redraws everything.

Four properties of that code decide what is possible.

**The skinned bit has no pose compare.** `anySkinnedCaster: skinnedCasterCount > 0`. Bone palettes are not
hashed, so presence is the trigger, deliberately and documented as such.

**The light compare is exact.** `ShadowCascadeVpsChanged` is `a[i] != b[i]` over `Matrix4x4`. There is no
tolerance anywhere in the path.

**The fit already quantizes translation and not rotation.** `ShadowMapMath.BuildLightViewProj` snaps the focus to
texel increments in light-view space, precisely so that a camera sliding by less than a texel does not move the
frustum. The light DIRECTION gets no equivalent treatment, so a sun rotation of any size rebuilds the view basis
and moves every matrix entry. **Option B below is that missing half, not a new idea.**

**The atlas is one texture and the pass clears all of it.** `ShadowMapRenderer` allocates
`resolution * cascadeCount` wide by `resolution` high, R32Float (64 MiB at 2048 x 4), places each cascade in a
column via a baked clip transform plus a scissor, and `BeginShadowPass` clears the whole thing. There is no way
to redraw part of the atlas today, and no way to erase a stale silhouette short of a clear.

**The receiver samples through the matrices the atlas was recorded with, and today that is guaranteed by
accident.** The receiver tail is rebuilt from the current fit every frame including skipped ones. That is safe
only because the pass skips exclusively when the fit compares bit-identical. Loosen the compare and the guarantee
breaks: the receiver would sample an atlas recorded with the old matrix through the new one, which shifts every
shadow by the un-recorded delta. **This is the trap in option B, and it is why the fix is to not re-fit rather
than to fit and skip.**

## 3. The options

### 3.1 Option A: split the rigid and skinned halves

Keep rigid depth from the previous frame, re-record only the skinned casters.

**What it would save.** By the bench's ratio, the skinned casters are 4 of 410 draws, so on a scene where only
they are dirty the re-record's 0.237 ms CPU and 0.65 ms GPU collapse to roughly a hundredth of that. On the field
trace's mix (about 80 skinned draws of 790) it is a tenth. This is the largest saving in the doc and it is the
only option that helps a scene whose characters are genuinely animating, which is the normal MMO frame.

**Why it is not a small change.** A persistent single atlas cannot hold last frame's rigid depth AND this frame's
skinned depth without either a clear (which loses the rigid half) or a way to erase last frame's skinned
silhouettes (which does not exist). Redrawing the skinned casters over an un-cleared atlas leaves the previous
pose's depth in place, and a depth atlas where a stale silhouette survives is exactly the moving-caster ghosting
the issue names as unacceptable.

The clean form is two atlases: a persistent rigid one re-recorded on the rigid dirty conditions, and a
skinned-only one cleared and re-recorded every frame, with the receiver taking the nearer of the two samples.
That costs a second R32Float atlas (64 MiB at the default tier, doubling shadow memory) and doubles the receiver's
PCF taps, which is per-pixel GPU work on every shadowed fragment of every frame, paid whether or not anything is
animating. The bench's whole-frame numbers say the pass's GPU cost is already the larger half of the re-record,
so a change that doubles receiver taps to save caster draws has to be measured, not assumed.

**Blast radius.** `ShadowMapRenderer` (a second target, its own pipelines and scissor set, a second sampler
binding), `ShaderSources.Lighting.cs` (`pcfCascade` samples two atlases and combines), every shadow golden on
three backends, and the `ShadowPassDiagnostics` surface (rendered/skipped becomes per-half). Large.

**Verdict: not first.** The saving is real and it is the only one that survives animation, but it is a rendering
architecture change with a memory and a per-pixel cost, and it should follow the two cheap options rather than
precede them.

### 3.2 Option A-lite: hash the bone palettes, so presence stops being the trigger

The cheap fraction of A. Keep one atlas, keep the all-or-nothing re-record, and replace
`anySkinnedCaster: skinnedCasterCount > 0` with a compare of this frame's composed palettes against the last
rendered pass's.

**What it saves.** Exactly the bench's first row: a stationary scene holding an idle skinned caster stops
re-recording, dropping 0.237 ms of encode and 0.65 ms of GPU per frame to the 0.062 ms skip floor. It saves
nothing at all once a character animates, which in Ruinborne is most of the time, because an idle animation moves
the palette every frame.

**Ghosting risk: none.** A changed pose re-records everything exactly as today. The change can only make the
dirty test more precise, never less, and the existing GPU test
(`ShadowPassDiagnosticsGpuTests.Stationary_scene_with_a_skinned_caster_still_renders_every_frame`) is the
before-and-after: it is written to fail deliberately when this lands.

**Cost of the compare.** The palettes are already contiguous in `_boneMatrices`, so it is a memcmp against a kept
copy, sized bone count times 64 bytes per caster. At the field's 21 casters and 128 bones that is 172 KB compared
per frame, which is real and needs measuring against the 0.237 ms it is trying to save. A cheaper form is a hash
folded during the pack that already walks those matrices.

**Blast radius.** `Scene3D.cs` (one flag), a kept palette buffer beside the existing `_lastShadowCasterRuns`
swap-buffer pattern, one diagnostics doc-comment correction, one GPU test inverted. Small.

### 3.3 Option B: do not re-fit a cascade for sub-texel light movement

The missing half of the texel snap that already exists for translation. Freeze a cascade's fitted matrix until
the light has rotated enough to move the recorded shadow by more than a texel, and only then re-fit it.

**What it saves.** The whole re-record on a scene that is stationary under a moving sun: 0.235 ms encode plus
about 0.65 ms of GPU, down to the 0.062 ms floor, for as long as the accumulated rotation stays under the
threshold. The bench's fourth and fifth rows are the evidence that the saving is available: at 0.001 deg/frame the
pass re-records 200 frames out of 200, and no viewer could tell those 200 frames apart.

**Sizing the threshold, per cascade.** The quantity a viewer sees is how far a shadow moves on the ground. For a
caster h above its receiver and a light rotation of `dTheta`, the shadow's foot moves by `h * dTheta` (small
angle), and the cascade's own quantum is `TexelWorldSize(r, res) = 2r/res`. So the condition for one cascade is
`h_max * dTheta < 2r/res`, evaluated with that cascade's own radius. Cascade 0 is the tightest fit and therefore
sets the smallest threshold, and cascade 3's radius is roughly an order of magnitude larger, so it can hold still
roughly an order of magnitude longer. **The threshold is per cascade or it is wrong**, which is what makes
option C the natural companion rather than an independent idea.

**The trap, and the reason this is "do not re-fit" rather than "fit and skip".** The receiver tail is rebuilt
from the current fit every frame (section 2). Fitting normally and then declining to re-record would leave the
receiver sampling an atlas recorded with the previous matrix through the current one, and a fraction of a texel of
mismatch is exactly the acne and edge swim the texel snap exists to prevent. Freezing the fit itself keeps the
atlas, the receiver tail, and the per-cascade cull (`ShadowCascadeCull.FromLightViewProj` reads the same fit) in
agreement by construction, and the light-matrix compare then keeps working unchanged: a frozen fit compares equal,
so `LightMatrixChanged` goes false and the existing skip does the rest.

**Ghosting risk: none of the kind the issue names.** A moving caster still trips `CasterDataChanged` and
re-records. What this trades is shadow-update LATENCY for the sun: the shadow direction lags the light direction
by up to the threshold, and the correction lands in one step when the threshold is crossed. Sized by the rule
above, that step is one texel, which is the same discontinuity the translation snap already ships and nobody has
reported. Sized carelessly, it is a visible jump, so the threshold constant is the whole risk and it belongs in
`ShadowSettings` with a documented derivation rather than as a magic number in the fit.

**Blast radius.** `ShadowMapMath.BuildLightViewProj` gains a per-cascade "previous fit" input or, better, the
freeze lives in `Scene3D`'s fit loop (`Scene3D.cs:1718-1730`) where the previous matrices are already kept, so the
pure math function stays pure and headless-testable. Plus one `ShadowSettings` knob, headless tests on the
threshold, and a bench row. Small to medium, and no golden moves as long as the threshold defaults conservatively.

### 3.4 Option C: dirty per cascade instead of per pass

Today `dirty` is one bool for the whole atlas. The natural extension of B: track it per cascade, re-record only
the columns that moved.

**What it saves.** Two independent fractions. Under B, the far cascades hold still an order of magnitude longer
than cascade 0, so most re-fit frames would touch one column instead of four. And under a moving CASTER, only the
cascades that caster reaches need redrawing, which the per-cascade cull already computes: the bench's spans split
96 / 96 / 95 / 119, so a cascade-0-only re-record is roughly a quarter of the pass.

**What it costs structurally.** Three things have to become per cascade rather than per pass: the atlas clear
(`BeginShadowPass` clears the whole texture, and the per-column scissor already exists so a scissored clear is
plausible), the kept reference state (`_lastCascadeCpuVps` already is per cascade, but `_lastShadowCasterRuns` is
one signature for the whole list and would need splitting or re-deriving per cascade), and the diagnostics
(`Rendered` / `Skipped` become per column, which is a public API change to `ShadowPassDiagnostics`).

**Ghosting risk: real and specific.** A caster that moves out of cascade 2 while cascade 2 is not re-recorded
leaves its shadow behind in that column. The per-cascade dirty test therefore has to be driven by the caster's
OLD and NEW cascade masks, not just its new one, or the vacated column keeps a ghost. That is the one place in
this doc where a plausible implementation is silently wrong.

**Blast radius.** `Scene3D.ShadowCasters.cs`, `Scene3D.ShadowCascadeCull.cs`, `ShadowMapRenderer`,
`ShadowPassDiagnostics` (public), plus goldens if the clear behaviour changes. Medium.

### 3.5 Option D: caster-count-scaled budgets

Bound the per-frame re-record work: with N casters above some count, re-record cascade `k mod 4` this frame and
let the others go stale, or re-record a fixed slice of the caster list per frame.

**Verdict: declined as a first move, with the reason written down.** It is option C plus a deliberate staleness
policy, so it cannot be built before C exists, and its ghosting risk is not incidental but designed in: a moving
caster in a deferred cascade IS a stale shadow, and the issue's acceptance criterion forbids exactly that. It also
solves a problem the measurements do not show: the pass cost scales with span count, and no backend currently
measured is anywhere near a budget that would need amortizing. Revisit if a field capture ever shows a scene where
the re-record is unavoidable AND expensive on a healthy backend.

### 3.6 Two smaller things the code reading turned up

**The signature build is unconditional.** `BuildShadowCasterSpans` walks every caster and builds two lists before
the dirty check, every frame, including frames that will skip. That is the measured 0.062 ms floor. A running hash
folded into the walk the model pass already does would remove the second walk, but it changes what
`ShadowCastersChanged` can prove (a hash collides, a list compare does not), so it is a deliberate trade rather
than a free win. Not recommended now, recorded so the floor is not mistaken for irreducible.

**A frame already known dirty still pays the compare.** When a skinned caster is present the pass is dirty
regardless, yet the light and caster compares run anyway. Short-circuiting them is a few microseconds and would
blind the diagnostics to why the frame was dirty, which is the instrument #410 exists to have. Declined: the
instrument is worth more than the microseconds.

## 4. Recommendation

**Take B first, then A-lite, then C. Leave A-full and D unbuilt until a field capture asks for them.**

B is the largest saving per unit of risk in the doc. It removes the entire re-record from a stationary scene under
a moving sun, which is the exact case the field trace spent 160 seconds sitting in, and it is not a new mechanism
but the rotation half of a quantization the fit already applies to translation. Its one real hazard is the
receiver-tail coupling, and freezing the fit rather than skipping the record disposes of it by construction.

A-lite is cheap, has no ghosting risk in any form, and deletes an unconditional truth ("any skinned caster
re-records everything") that is wrong in principle even where it is cheap in practice. It should ship with B
because together they cover both stationary regimes the bench measures, and because the existing GPU test is
already written to be inverted by it.

C follows because B's threshold is per cascade anyway, so the per-cascade machinery is half-built by then. It
carries the one genuinely subtle correctness trap in the doc (the vacated-cascade ghost), which is the reason it
goes third rather than second.

A-full stays open. It is the only option that helps an animating scene, and if a field capture ever shows the
shadow pass mattering while characters move, it is the answer. It is not the answer to a stationary 10 fps
plateau, and it should not be started on the strength of this doc's numbers.

None of this should be represented as closing #410. The plateau that opened that issue was a D3D11 per-call tax
and #418's batching took it from 38 us per span to something that holds 125 fps. This work is the separate,
smaller, real thing sitting underneath it: a stationary scene re-recording 400 spans and 1,500 casters every
frame for no visible reason, on every backend.

## 5. What the Windows F3 capture must show to confirm it

Ruinborne forwards `ShadowPassDiagnostics` to F3 telemetry, so the confirmation is four readings on the reporting
machine, same scene, same stationary pose, before and after.

1. **The reason bits change, and in the right direction.** Today every sample reads
   `AnySkinnedCaster=1, LightMatrixChanged=1`. After B plus A-lite, a stationary AFK window must read
   `LightMatrixChanged=0` on most frames with an occasional 1 at the re-fit cadence, and `AnySkinnedCaster=0`
   whenever the character is genuinely still. A capture where `LightMatrixChanged` is still 1 every frame means
   the threshold is smaller than Ruinborne's per-frame sun step and the change bought nothing.
2. **The re-fit cadence matches the threshold.** Count the frames between `LightMatrixChanged=1` samples. It must
   equal the texel threshold divided by Ruinborne's per-frame sun rotation, within a frame or two. If it does not,
   something else is re-fitting the cascades and the epsilon is not the thing being measured.
3. **`shadowMs` falls to the skip floor on the non-re-fit frames**, and the floor scales with candidate count, not
   with span count. On that machine at about 2,200 candidates expect a floor above this bench's 0.062 ms.
   `TotalDrawCalls` and `TotalRigidSpanCount` must read 0 on those frames, which is what proves the atlas was
   reused rather than merely recorded quickly.
4. **No ghost.** A capture with the player running past a moving prop must still show `CasterDataChanged=1` and a
   rendered pass on every frame the prop moves. The visual check is the one thing telemetry cannot do: a
   screenshot of a walking character's shadow at the re-fit boundary, confirming the shadow edge steps by at most
   one texel and does not smear.

Reading 4 is the acceptance criterion from #410's own body ("without reintroducing moving-caster shadow
ghosting"), and it is the one that decides whether C is safe to build on top.

## 6. Status

Design only. The bench is committed and passing on local Metal. Nothing in section 3 is implemented, no version
was bumped, and no rendering behaviour changed on this branch.
