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
GPU skinning on. It fragments into 404 to 406 rigid spans over the four cascades, against the field trace's 707 to
789. Same order of magnitude, and explicitly NOT the same distribution: the bench's per-cascade split is flat
(96, 96, 95, 119, a 1.25x spread) while the field's is a monotonic ramp (45, 153, 221, 285, a 6.3x spread) with
most of its spans in the far cascades. Anything below that reasons from WHICH cascade the spans sit in has to be
read against the field's ramp, not the bench's.

It renders that scene under the four shadow decisions the diagnostics can name, interleaved round-robin in one
process so no configuration owns the warm-up or the drift, over 200 measured frames each. The dirty-reason facts
are asserted. The milliseconds are printed and gate nothing, because a wall-clock threshold on a shared machine is
a coin flip.

The bench sits in the `AllocSensitive` collection, so it never records while the rest of the assembly runs beside
it. That is not hygiene theatre: measured unserialized, the same encode read 1.392 ms against 0.227 ms serialized,
six times over, which is larger than every difference this doc reasons about. Numbers quoted from a run that was
not serialized are not comparable to these.

### 1.2 The numbers, local Metal (Apple silicon, Release, serialized run)

| configuration | shadow mean ms | shadow median ms | frame median ms | spans | draws | us/span |
|---|---|---|---|---|---|---|
| shadows off | 0.000 | 0.000 | 1.145 | 0 | 0 | - |
| skinned caster, sun frozen | 0.227 | 0.226 | 2.162 | 406 | 410 | 0.56 |
| rigid only, sun frozen (skips) | 0.042 | 0.042 | 1.388 | 0 | 0 | - |
| rigid only, daylight sun | 0.198 | 0.191 | 1.949 | 405 | 405 | 0.49 |
| rigid only, 0.001 deg sun | 0.189 | 0.188 | 1.948 | 404 | 404 | 0.47 |
| skinned caster, sun frozen, pipelined | 0.206 | 0.201 | 0.734 | 406 | 410 | 0.51 |

The two shadow columns are the pass timer's mean and its median over the same 200 frames. The frame column is a
median only, and there is no frame mean. Every subtraction below therefore stays inside one reduction, mean from
mean or median from median, because mixing them invents a difference the size of the gap between them.

Six things fall out of that table, and they are the inputs to every option below.

**The re-record costs about 0.15 ms of CPU encode and about 0.41 ms of everything else, per frame.** Derive both
from the RIGID-ONLY pair, which is the only pair whose two rows draw the same geometry: daylight sun against sun
frozen. The whole-frame premium is 1.949 - 1.388 = 0.561 ms (medians). The encode premium is 0.191 - 0.042 =
0.149 ms (medians), or 0.198 - 0.042 = 0.156 ms (means). The remainder, about 0.41 ms, is GPU execution plus
whatever the frame pays around the pass timer. **On this backend the re-record is dominated by the part the pass
timer cannot see.** Any option judged only on the encode number is being judged on about a quarter of its saving.

The skinned-versus-skip pair is the tempting one to use and it is contaminated: 2.162 - 1.388 = 0.774 ms, but the
skinned configuration also draws the limb in the MAIN pass and uploads its bone palette, neither of which the skip
configuration pays. That 0.774 is the re-record plus an unrelated per-frame cost, so it is not the premium.

**A skipped frame is not free: it pays 0.042 ms.** `BuildShadowCasterSpans` runs before the dirty check on every
frame, over every caster, because the signature it builds is what the compare needs. That is the floor. No option
in this doc removes it, and at the field machine's 2,200 candidates it is proportionally larger.

**Movement size does not change the price.** Ruinborne's real daylight rate (0.00333 deg/frame) and a rate one
third of it (0.001 deg/frame) cost 0.198 and 0.189 ms mean and record 405 and 404 draws. The cascade compare is
exact matrix equality (`ShadowCascadeVpsChanged`), so there is no sun movement small enough to be ignored, and the
bench asserts that: `LightMatrixChanged` is true on all 200 frames of the 0.001 deg configuration.

**One skinned caster forces the other 406 spans.** The skinned caster contributes 4 of the 410 draws and dirties
100 percent of the pass. In the field trace the ratio is 17 to 21 skinned casters, about 80 of about 790 draws:
a tenth of the work forcing the other nine tenths.

**Recording does not block on the GPU here.** The pipelined configuration records the identical frame with the
queue deliberately loaded and encodes in 0.206 ms mean / 0.201 median against the drained twin's 0.227 / 0.226.
No inflation, so on Metal `ShadowDepthMs` means what it says: CPU time.

**Corollary: every headline number above is from the DRAINED regime, which is the pessimistic side.** Draining
empties the queue before each measured frame and gives up whatever overlap a loaded queue buys, and it costs about
9 percent on the encode (0.227 against 0.206 mean). A real frame runs pipelined, so read the encode figures as an
upper bound. The frame-median column runs the other way and must not be compared across the two: the pipelined
row's 0.734 ms excludes GPU execution altogether, because nothing idles the device inside its measured window.

### 1.3 What the local numbers can and cannot say about the field

They corroborate the field's Metal column and nothing else. 0.49 to 0.56 us/span here against the trace's
1.01 us/span on a different Mac and a different scene is the same quantity twice, which is the useful part: the
bench reproduces the machine it runs on.

They cannot settle the Windows question, and do not need to. #410's own trace analysis already settled it from
Veldrid's source rather than from a timer: `D3D11CommandList` records into a deferred context created by
`CreateDeferredContext()`, recording into a deferred context never touches the GPU, so there is nothing there to
block on. The 10.95 us per command call measured on the reporting machine was CPU. What this bench adds is the
control: the same pass shape on a healthy backend costs 0.5 us per span, so one D3D11 command call was costing more
than twenty times a whole span's worth of work here, a per-call tax rather than a pass asking for too much.

**And the field has moved.** After the 4.9.101 fork bump the same machine records the shadow pass in 0.35 ms and
holds 125 fps flat. So the re-record is a sub-millisecond item on every backend currently measured, and nothing
in section 3 should be sold as the fix for the 10 fps plateau. It is worth doing on its own merits, which are
the 0.56 ms whole-frame premium above and the fact that a stationary scene is paying it for nothing.

## 2. The mechanism, from the code

The decision site is `Scene3D.cs:1948-1995`. Per frame, when the tier is `ShadowMap`:

0. The fit already happened, and not here. `ComputeShadowCascades()` (`Scene3D.cs:1696-1731`) fits every cascade
   from the CURRENT camera and light and writes `_cascadeCpuVps`. It is called at `Scene3D.cs:1846`, before the
   CPU skin pass and OUTSIDE the `ShadowDepthMs` window, so none of section 1's numbers include it.
1. `SetShadowReceiverTail()` (`Scene3D.cs:1744`) READS `_cascadeCpuVps`, GPU-clip-corrects each cascade into
   `_cascadeReceiverVps`, bakes the atlas-column transform into `_cascadeDepthVps`, and hands the receiver its
   matrices. Unconditional, before any dirty check. It fits nothing itself.
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
frustum. The light DIRECTION gets no equivalent treatment in the code as it stands, so a sun rotation of any size
rebuilds the view basis and moves every matrix entry. That is true of the current code and false as history:
13.1.0 shipped a direction quantizer for this exact dirty-skip and 14.0.0 removed it, which 3.3's prior-art
paragraph covers. **Option B below is that missing half, not a new idea.**

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
they are dirty the re-record's 0.15 ms of encode and 0.41 ms of everything else collapse to roughly a hundredth of
that. On the field trace's mix (about 80 skinned draws of 790) it is a tenth. This is the largest saving in the doc
and it is the only option that helps a scene whose characters are genuinely animating, the normal MMO frame.

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

### 3.2 Option A-lite: compare the skinned casters, so presence stops being the trigger

The cheap fraction of A. Keep one atlas, keep the all-or-nothing re-record, and replace
`anySkinnedCaster: skinnedCasterCount > 0` with a compare of this frame's skinned casters against the last
rendered pass's. The obvious form of that compare is the bone palettes, and the obvious form is not enough: see
the ghosting paragraph below for what it has to cover.

**What it saves.** Exactly the bench's skinned row: a stationary scene holding an idle skinned caster stops
re-recording, dropping the re-record's encode and the roughly 0.41 ms around it to the 0.042 ms skip floor. The
skinned row's own encode premium is 0.226 - 0.042 = 0.184 ms (medians), a little above the rigid-only pair's
0.149 ms because the pass also draws and packs the limb. It saves nothing at all once a character animates, which
in Ruinborne is most of the time, because an idle animation moves the palette every frame.

**Ghosting risk: real, and it is what sets the SCOPE of the compare.** Hashing the palettes ALONE is wrong, and
wrong in exactly the way #410 forbids. The palette is bones and nothing else: `ComposeBonesIntoSlot`
(`Scene3D.cs:2826-2842`) folds each joint against its inverse bind and writes no world term, and the depth pack
takes the two separately (`ShadowMapRenderer.PackSkinnedShadowSlot`, `Rendering/ShadowMapRenderer.cs:424-430`,
`model` and `bones` as distinct arguments). The skinned caster's world matrix rides `GpuSkinnedDraw`
(`Scene3D.cs:1899-1901`), and no other compare sees it either: `BuildShadowCasterSpans`
(`Scene3D.ShadowCasters.cs:157-182`) walks `_runs`, the RIGID instanced runs, so `CasterDataChanged` is blind to a
skinned caster entirely. A caster whose animation is paused while its world matrix translates would therefore
compare palette-equal, the pass would skip, and its silhouette would stay in the atlas where it used to be. Those
are not exotic cases: a character riding a moving platform, a network position correction on a still pose, an
LOD-frozen animation at distance. All three ghost.

So the compare covers three things or it is unsafe: the composed palettes, EACH skinned draw's world matrix, and
the skinned caster identity set (one caster leaving as another arrives changes neither of the first two for the
casters that remain). Scoped that way it can only make the dirty test more precise, never less, and the existing
GPU test (`ShadowPassDiagnosticsGpuTests.Stationary_scene_with_a_skinned_caster_still_renders_every_frame`) is the
before-and-after: it is written to fail deliberately when this lands.

**Cost of the compare.** The palettes are already contiguous in `_boneMatrices`, so it is a memcmp against a kept
copy, sized bone count times 64 bytes per caster. At the field's 21 casters and 128 bones that is 172 KB compared
per frame, which is real and needs measuring against the 0.184 ms it is trying to save. The world matrices add
64 bytes per caster on top, which does not move that number. A cheaper form is a hash folded during the pack that
already walks those matrices.

**Blast radius.** `Scene3D.cs` (one flag), a kept palette-plus-world buffer beside the existing
`_lastShadowCasterRuns` swap-buffer pattern, one diagnostics doc-comment correction, one GPU test inverted. Small,
but only once the scope above is understood: the version that hashes bones alone is smaller and ships a ghost.

### 3.3 Option B: do not re-fit a cascade for sub-texel light movement

The missing half of the texel snap that already exists for translation. Hold the light direction the fit is given
until the sun has rotated enough to move the recorded shadow by more than a texel, and only then let the new
direction through. The camera half of the fit keeps running every frame, which is load-bearing and is the last
paragraph of this section.

**Prior art in this repo: 13.1.0's quantizer, removed in 14.0.0.** `ShadowSettings.ShadowLightQuantizeDegrees`
plus the pure `ShadowMapMath.QuantizeDirection` snapped the key light onto an angular lattice (azimuth and
elevation) before the fit, for the same reason this option exists: hold the fitted matrices bit-identical between
steps so the atlas dirty-skip engages under a rotating sun. 14.0.0 removed both as breaking, because no consumer
fleet-wide ever enabled the knob (it defaulted to `0`) and its `ShadowStepBlendSeconds` companion ghosted a caster
that moved mid-fade. Three material differences make this option a different bet rather than a repeat of that one.
It defaults ON, so adoption is not something a consumer has to opt into and then remember. It HOLDS the last
adopted direction rather than snapping to a lattice, so a scene with a static sun fits from a direction
bit-identical to its live one and its rendered bytes do not move, which a lattice snap cannot promise: a lattice
pulls a stationary sun onto the nearest cell and changes the image on adoption. And it ships no step-blend
companion at all, so the ghosting source that killed the earlier family is absent by construction, with
`CasterDataChanged` still firing under a held sun.

**What it saves.** The whole re-record on a scene that is stationary under a moving sun: 0.198 ms encode plus the
roughly 0.41 ms around it, down to the 0.042 ms floor, for as long as the accumulated rotation stays under the
threshold. The bench's fourth and fifth rows are the evidence that the saving is available: at 0.001 deg/frame the
pass re-records 200 frames out of 200, and no viewer could tell those 200 frames apart.

**Sizing the threshold, per cascade.** The quantity a viewer sees is how far a shadow moves on the ground, and
that depends on the sun's ELEVATION as much as on the rotation. A caster h tall at sun elevation `e` throws its
foot `h * cot(e)` from its base. An azimuth step `dPhi` sweeps that foot by `h * cot(e) * dPhi`. An elevation step
`dE` slides it by `h * dE / sin^2(e)`. Those two are not comparable as written, because `dPhi` is AZIMUTH while
`dE` is great-circle: an azimuth step of `dPhi` is only `cos(e) * dPhi` of great-circle travel. Per great-circle
radian the pair is `h / sin(e)` for azimuth and `h / sin^2(e)` for elevation, so both exceed the naive
`h * dTheta` at every elevation and the elevation term is the larger by `1 / sin(e) >= 1`. At this bench's
35 degree sun the two factors are 1.74x and 3.04x `h * dTheta`. At 5 degrees, which Ruinborne's 30 minute day
passes through twice a cycle, they are 11x and 131x. A threshold derived from `h * dTheta` alone would be 131x too
loose at dawn and dusk, which is precisely when a slow low sun makes the freeze most attractive.

Since the elevation term is the larger at every elevation, one condition covers any rotation direction, and it is
an EXACT bound rather than merely a conservative one: the two displacements are perpendicular on the ground
(elevation slides the foot along the azimuth, azimuth sweeps it across), so a rotation splitting `dTheta` between
them drifts `h * sqrt((dE/sin^2 e)^2 + (dPhiGc/sin e)^2)`, which is at most `h * dTheta / sin^2(e)` and reaches it
on a pure elevation change. Against a cascade's own quantum `TexelWorldSize(r, res) = 2r/res`:

```
h_max * dTheta / sin^2(e) < 2r/res
```

evaluated with that cascade's own radius. Cascade 0 is the tightest fit and therefore sets the smallest threshold,
and cascade 3's radius is roughly an order of magnitude larger, so it can hold still roughly an order of magnitude
longer. **The threshold is per cascade or it is wrong**, which is what makes option C the natural companion rather
than an independent idea. It also has to be evaluated against the CURRENT elevation rather than baked from a
representative one: the `1/sin^2(e)` factor is 3.04 at the bench's 35 degrees and 131 at a 5 degree dusk, so a
constant derived at one elevation is 43x wrong at the other.

**The trap, and the reason this is "do not re-fit" rather than "fit and skip".** The receiver tail is rebuilt
from the current fit every frame (section 2). Fitting normally and then declining to re-record would leave the
receiver sampling an atlas recorded with the previous matrix through the current one, and a fraction of a texel of
mismatch is exactly the acne and edge swim the texel snap exists to prevent. Freezing the fit's light input keeps
the atlas, the receiver tail, and the per-cascade cull (`ShadowCascadeCull.FromLightViewProj` reads the same fit)
in agreement by construction, and the light-matrix compare then keeps working unchanged: a fit re-derived from a
held direction and an unmoved camera compares equal, so `LightMatrixChanged` goes false and the existing skip does
the rest.

**And what gets frozen is the light DIRECTION INPUT, never the fitted output matrix.** The fit is a function of
the camera as well as the light: `ComputeShadowCascades` re-derives each cascade's bounding sphere from this
frame's frustum corners every frame (section 2, step 0). Freeze the output matrix and a camera that moves leaves
the cascade sphere sitting where the camera used to be, so the frustum walks out of the atlas and shadows stretch
or vanish at the far edge, which is a far worse artifact than the one being avoided. Freeze the direction and
re-fit from the current camera every frame, and a camera that moves more than the existing texel snap absorbs
simply changes the fitted matrix, trips `LightMatrixChanged` and re-records, which is correct: the cascade moved,
so the atlas has to. The saving is the stationary-camera case, which is the case section 4 is about.

**Ghosting risk: none of the kind the issue names.** A moving caster still trips `CasterDataChanged` and
re-records. What this trades is shadow-update LATENCY for the sun: the shadow direction lags the light direction
by up to the threshold, and the correction lands in one step when the threshold is crossed. Sized by the rule
above, that step is one texel, which is the same discontinuity the translation snap already ships and nobody has
reported. Sized carelessly, it is a visible jump, so the threshold constant is the whole risk and it belongs in
`ShadowSettings` with a documented derivation rather than as a magic number in the fit.

**Blast radius.** `ShadowMapMath.BuildLightViewProj` gains a per-cascade "previous fit" input or, better, the
freeze lives in `ComputeShadowCascades`'s fit loop (`Scene3D.cs:1721-1729`), where the frozen direction can be
substituted for `lightDir` before `FitCascade` and the previous matrices are already kept, so the pure math
function stays pure and headless-testable. Plus one `ShadowSettings` knob, headless tests on the threshold, and a
bench row. Small to medium, and no golden moves as long as the threshold defaults conservatively.

#### Shipped in 17.36.1, with corrections in flight

Built as proposed, at `Scene3D.cs:1719` substituting `HeldLightDirection` for the direct
`Vector3.Normalize(Post.LightDirection)`. The hold's state and its two hazard write-ups live in the new
`Scene3D.ShadowLightHold.cs` partial, and the arithmetic in `Internal/ShadowLightHold.cs`, which is pure and
pinned by `ShadowLightHoldTests`. The end-to-end behaviour is `ShadowLightHoldGpuTests` and the bench.
No golden moved, which is the prediction this section made: a scene with a fixed light adopts on its first frame
and thereafter holds a direction bit-identical to its live one, so the fit is byte-for-byte what it was.

Five things came out differently from the proposal above, all of them worth carrying forward into option C.

**Two knobs, not one.** The rule needs `h_max`, and there is nowhere honest to get it from. The engine does have
every rigid caster's world bounding sphere in hand (`_shadowCasterSpheres`), so an auto-derived bound looked
available, and it is wrong: a merged HLOD cluster's sphere is the radius of a whole chunk of terrain-scale
geometry rather than the height of anything standing on it, so deriving `h_max` from it collapses the threshold to
nothing in exactly the streamed outdoor scene this feature exists for. So `ShadowLightHoldTexels` (default `1`,
`0` disables) is the drift budget and `ShadowLightHoldCasterHeight` (default `12`, the tall tree this section
sizes against) is `h_max`. Both ride the FLAT-GROUND reading of `h * cot(e)`, so the one-texel bound is sub-texel
by construction only there: a receiver grazed more shallowly than the sun's elevation (a cliff face, a wall) takes
`(ray length) * dTheta / sin(grazing angle)` of drift instead, which can pass a texel on the same rotation. It
stays bounded and small, and `ShadowLightHoldCasterHeight` is where a game full of steep receivers buys the margin
back.

**The threshold has a resolvability floor the section did not anticipate.** `Post.LightDirection` is a `Vector3`
of floats, so a unit direction only carries about `1e-7` radians of angular resolution. Below `1e-5` radians the
comparison is two rounding errors, so `ShouldAdopt` re-fits unconditionally there. Where that engages is not a
fixed sun angle: solving `budget * (2r/res) * sin^2(e) / h = 1e-5` for the elevation gives a standdown scaling as
`1/sqrt(r)` with cascade 0's CAMERA-derived fitted radius, so it is about 2.6 degrees on this bench's wide framing
(`r` around 60 m) and about 5.8 degrees at the 12 m radius `ShadowLightHoldTests` fits. It costs nothing real
wherever it lands, because this section's own arithmetic had already made the hold worthless there: at the
standdown the threshold IS the floor, `1e-5` radians or about `5.7e-4` degrees, and Ruinborne's `0.00333` degrees
per frame is some six times that, so it crosses every single frame anyway. What it buys is that dusk degrades
deterministically rather than holding or releasing on noise.

**The elevation read is the LOWER of the held and live directions, not the current one.** This section says
"evaluated against the CURRENT elevation". The drift per radian is worst at the smallest elevation the interval
passes through, so a sun RISING out of a dusk would be sized by the generous end of its own interval. Taking the
minimum is one `MathF.Min` and is strictly conservative.

**The compare is the total angle between held and live, not an accumulated arc length.** A sun that wanders and
returns has not moved its shadow, and arc length would claim it had. The angle is computed from the chord
(`2*asin(|a-b|/2)`) because `acos(dot)` loses most of its significant digits at these angles, which is a PRECISION
argument and not a liveness one. At a single 0.001 degree step the dot product is `1 - 1.5e-10`, which a float
cannot represent as distinct from 1, so that step does read as exactly zero. It would not hang the hold: the
compare is against the ACCUMULATED angle between held and live, never a per-frame step, and `1 - cos(theta)`
clears a float's last place near 1 at about `3.4e-4` radians (0.020 degrees), well under the 0.095 degree
threshold measured below. `acos` would therefore still release, with a few percent of jitter on WHERE, from the
quantization plus the dot's own rounding. The chord has no such loss (subtracting two nearby floats is exact) and
carries the angle down to the resolvability floor, which is why it is the one that ships.

**The threshold is per PASS, sized on the tightest active cascade.** This section says "the threshold is per
cascade or it is wrong", and that is right about the threshold and not yet actionable about the dirty test, which
is still one bool for the whole atlas. Taking the minimum radius over the active cascades satisfies every cascade
at once and is the conservative reading of the same rule. The saving option C adds is exactly the far cascades'
order of magnitude of extra hold, and the per-cascade radius walk this fix already does (`MinCascadeRadius`) is
half of its machinery.

**Measured.** The bench's two sun-moving rows fall from 200 rendered frames out of 200 to 0, landing on the skip
row's numbers exactly (0.035 ms mean, 1.11 ms frame median against 0.198 and 1.949 before). The cadence cannot be
read off those rows, because each interleaved block's measured window opens four frames after a warmup that always
re-adopts, so a threshold wider than the window reads as a flat 200 skips whatever its real width is. The new
`The_hold_releases_on_a_cadence_the_threshold_predicts` sweeps 400 continuous frames per rate instead: one re-fit
every 28.6 frames at the daylight rate and every 100 at a third of it, which is the same 0.095 to 0.1 degree
threshold read two ways, so 96.5 and 99 percent of frames skip. **Ruinborne's real daylight rate lands well below
the threshold**, which section 3.3 left open. Note that this cadence scales with cascade 0's fitted radius, which
on this bench's wide 60 x 20 x 60 framing is around 60 m: a tighter game camera fits a smaller cascade 0 and holds
proportionally less, so section 5's field capture remains the confirmation and #410 stays open until it lands.

### 3.4 Option C: dirty per cascade instead of per pass

Today `dirty` is one bool for the whole atlas. The natural extension of B: track it per cascade, re-record only
the columns that moved.

**What it saves.** Two independent fractions. Under B, the far cascades hold still an order of magnitude longer
than cascade 0, so most re-fit frames would touch one column instead of four. And under a moving CASTER, only the
cascades that caster reaches need redrawing, which the per-cascade cull already computes.

How much that second fraction is worth depends entirely on the span distribution, and the bench's is not the
field's (section 1.1). On the bench's flat 96 / 96 / 95 / 119 a cascade-0-only re-record is roughly a quarter of
the pass. On the field trace's ramp (45, 153, 221, 285) the same re-record is 45 of 704 spans, **6.4 percent**.
The field number is the one to plan against: it is nearly four times better than the bench's, and it is the shape
a streamed world with distant HLOD actually produces.

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
the dirty check, every frame, including frames that will skip. That is the measured 0.042 ms floor. A running hash
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
a moving sun, which is the case the field trace spent about 71 of its 160.91 seconds sitting in (t=0 to 30.25 and
t=120 to 160.9, the two stationary phases either side of the moving one), and it is not a new mechanism but the
rotation half of a quantization the fit already applies to translation. Its two real hazards are the receiver-tail
coupling and the camera term in the fit, and freezing the light direction rather than the fitted matrix disposes
of both by construction.

A-lite is cheap and deletes an unconditional truth ("any skinned caster re-records everything") that is wrong in
principle even where it is cheap in practice. It should ship with B because together they cover both stationary
regimes the bench measures, and because the existing GPU test is already written to be inverted by it. It is not
the risk-free option it looks like: scoped to the palettes alone it ships a ghost (section 3.2), and it is only
safe once the world matrices and the caster identity set are in the compare too.

C follows because B's threshold is per cascade anyway, so the per-cascade machinery is half-built by then. It
carries the one genuinely subtle correctness trap in the doc (the vacated-cascade ghost), which is the reason it
goes third rather than second.

A-full stays open. It is the only option that helps an animating scene, and if a field capture ever shows the
shadow pass mattering while characters move, it is the answer. It is not the answer to a stationary 10 fps
plateau, and it should not be started on the strength of this doc's numbers.

None of this should be represented as closing #410. The plateau that opened that issue was a D3D11 per-call tax,
and the 4.9.101 fork bump took it from 38 us per span to something that holds 125 fps. Credit that bump, not one
issue in it: #418's bind batching is the mechanism that matches the observed collapse in the shadow encode, but
#415's immediate-context fixes rode the same bump, so the attribution is a mechanism-match argued in the #410
thread rather than an isolated A/B. This work is the separate, smaller, real thing sitting underneath it: a
stationary scene re-recording 400 spans and 1,500 casters every frame for no visible reason, on every backend.

## 5. What the Windows F3 capture must show to confirm it

Ruinborne forwards `ShadowPassDiagnostics` to F3 telemetry, so the confirmation is four readings on the reporting
machine, same scene, same stationary pose, before and after.

1. **The reason bits change, and in the right direction.** Today every sample reads
   `AnySkinnedCaster=1, LightMatrixChanged=1`. After B plus A-lite, a stationary AFK window must read
   `LightMatrixChanged=0` on most frames with an occasional 1 at the re-fit cadence, and `AnySkinnedCaster=0`
   whenever the character is genuinely still in BOTH senses A-lite has to compare, pose and world position. A
   capture where `LightMatrixChanged` is still 1 every frame means the threshold is smaller than Ruinborne's
   per-frame sun step and the change bought nothing.
2. **The re-fit cadence matches the threshold.** Count the frames between `LightMatrixChanged=1` samples. It must
   equal the texel threshold divided by Ruinborne's per-frame sun rotation, within a frame or two. If it does not,
   something else is re-fitting the cascades and the epsilon is not the thing being measured.
3. **`shadowMs` falls to the skip floor on the non-re-fit frames**, and the floor scales with candidate count, not
   with span count. On that machine at about 2,200 candidates expect a floor above this bench's 0.042 ms.
   `TotalDrawCalls` and `TotalRigidSpanCount` must read 0 on those frames, which is what proves the atlas was
   reused rather than merely recorded quickly.
4. **No ghost.** A capture with the player running past a moving prop must still show `CasterDataChanged=1` and a
   rendered pass on every frame the prop moves. The skinned half needs its own reading, because `CasterDataChanged`
   is rigid-only (section 3.2): a character carried along on a still pose, which is what a moving platform or a
   position correction produces, must also render every frame, and that is the reading that proves A-lite's
   compare took in the world matrices and not only the palettes. The visual check is the one thing telemetry
   cannot do: a screenshot of a walking character's shadow at the re-fit boundary, confirming the shadow edge
   steps by at most one texel and does not smear.

Reading 4 is the acceptance criterion from #410's own body ("without reintroducing moving-caster shadow
ghosting"), and it is the one that decides whether C is safe to build on top.

## 6. Status

**Option B shipped in 17.36.1** (section 3.3's "shipped, corrections in flight" note has what changed on the way
in). A-lite, C, A-full and D are unbuilt, in that order of preference, and section 4's reasoning for the order is
unchanged by B landing: if anything B strengthens the case for C, since the per-cascade radius walk C needs now
exists.

Section 1.2's table was the pre-B state, and it is kept as written because it is the BEFORE half of B's evidence.
Its two sun-moving rows now read 0.035 ms mean and 0 draw calls on 200 skipped frames. Every number in it came
from one serialized full run of `KhaozEngine.Render.Tests` with `KE_GPU_TESTS=1`, and the after numbers from an
isolated Release run of the same bench, so the absolute milliseconds shift a little between the two while the
comparisons within each run hold.

#410 stays OPEN. Section 5 is the acceptance, and it needs a Windows F3 capture on the reporting machine that no
local bench can stand in for.
