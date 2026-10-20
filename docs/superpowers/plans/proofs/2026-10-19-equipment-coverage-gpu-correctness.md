# Equipment coverage GPU correctness, Part C

## Task 17 scene evidence and reviewed goldens

Scene capture copies actual selected words in the frame command list beside the aggregate result readback.
The shared fixture submits once and drains explicitly at its assertion boundary. Test callbacks freeze each
preparation identity and reservation before submission, record the physical buffers and draw windows observed
at the main, full/visible outline, cascade and point consumers, and verify the submitted current buffer becomes
the retained previous buffer. The completed keyed result is read through `Scene3D.TryReadCoverageOutcome`.
Actual source and final words retain independent pinned-reference comparisons. Temporal signatures are assigned
from returned bytes. Admission refusals and no-compute fallbacks retain separate metadata without a GPU result.
Stale frame, actor, preparation, offset and buffer associations have negative controls on real captured data.
Expected-covered callers require final selection, a completed Covered/None outcome and zero refusal words.
The actual recession-refused surface must fail that positive assertion while retaining independent source-word checks.
Cold reservation tests distinguish a successful reservation from a later sibling failure. The committed buffer
stays live, is consumed on retry and is disposed at teardown. A failure inside the pulling owner's binding
reservation separately proves disposal of the uncommitted buffer and partial binding before a successful retry.

The six local Metal golden comparisons retain the original references and tolerance. Candidate images, raw
float grids and exact delta/hash metadata are emitted without baking. Full-resolution original images are
paired externally for exact review. Local scene proof does not accept the other twelve backend comparisons,
final hosted synchronization or a refresh.

The [initial hosted comparison](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39347407621) at
`22ccad5140e85429f9e6d7a198d4717e568828d0` passed all eighteen expected native comparisons, six per backend,
with zero skips. Its synchronization validation passed 573 Render tests and 740 total tests with zero actual
validation messages. The [owning-backend bake](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39348598745)
then passed all six native cases on each backend with zero skips. Root authenticated all eighteen baked grid
hashes and all eighteen baked PNG hashes. Each backend has exactly three reviewed changed grids and three
unchanged grids. The nine approved grid bytes imported from that bake are:

| Backend | View | Approved grid SHA-256 |
| --- | --- | --- |
| metal-native | rear | `d625e30c061a184a399029050a1575dfdffd636709a05cbf2d7a246f92ed71e1` |
| metal-native | grazing-left | `af3a3343692b5a25ca3e1d1ed62ebde155fb827b2aeb4e10b2c4b06779b799f2` |
| metal-native | grazing-right | `72dabd74539b794963815a9d2107110b09c77628bcc2e827b6a690436666334b` |
| direct3d11-native | rear | `d625e30c061a184a399029050a1575dfdffd636709a05cbf2d7a246f92ed71e1` |
| direct3d11-native | grazing-left | `af3a3343692b5a25ca3e1d1ed62ebde155fb827b2aeb4e10b2c4b06779b799f2` |
| direct3d11-native | grazing-right | `72dabd74539b794963815a9d2107110b09c77628bcc2e827b6a690436666334b` |
| vulkan-native | rear | `c0cd15b3518ec973c5d20627c3a4a10fc2e9e88241ac31b5a542697b830e8f8f` |
| vulkan-native | grazing-left | `af3a3343692b5a25ca3e1d1ed62ebde155fb827b2aeb4e10b2c4b06779b799f2` |
| vulkan-native | grazing-right | `72dabd74539b794963815a9d2107110b09c77628bcc2e827b6a690436666334b` |

Root's review accepts the source-supported intended 25% clearance reserve and the resulting local
normal/tangent shading changes for these rear and grazing views. The original approved bake has no vertex
readback, so original-bake vertex provenance remains limited. Front, high and low-backlit retain their
unchanged grids and images. This import changes no tolerance or frozen contract.

The initial comparison used normal caches, and neither it nor the owning-backend bake is the final cold-cache
gate. Final all-backend Coverage and synchronization verification remains pending. This record makes no
final acceptance, performance, integration, release or adoption claim.

## Task 16 allocation and batch boundaries

`CoverageComputeBatchGpuTests` owns three executing native facts. The warmed changed-pose fact
reserves two actors, fills all three outcome slots and warms both submitted final generations before
measuring two distinct poses with a GPU recession refusal and recovery. Temporal history is active.
The test checks actual submitted buffer rotation, history reuse, every selected vertex word and
frame, view, actor and preparation identities from native headers and completed outcomes.

Measured coverage intervals are public equipment and host staging with draw submission, frame
reservation, packing and compute dispatch inside resolution, pulled surface resolution, outcome-copy
recording and completed outcome enumeration. Each requires zero managed bytes. Additional counters tightly
bracket the complete `Scene.RenderInternal` and shared `GpuFrameSubmission.Submit(commands)` calls.
Both totals require zero managed bytes in the warmed two-actor and full-ring facts. The render total
includes graphics payload packing and upload. The submission total includes the native submission and
submitted history callbacks. Warmed wait-stack capture is disabled and explicitly asserted.
Command-list opening and closing, test assertions, assertion drains, reference construction, payload
capture, word readback, comparison and evidence writing stay outside those two complete-call intervals.
The full-ring fact warms completed enumeration before filling the ring again. Its fourth submission
continues drawing and history while dropping only that frame's outcome copy.
A first, previously unwarmed completed-enumeration observation allocated 88 bytes. Its exact internal
source remains unattributed. The bounded warm-up exercises that missing path once before measurement.
There is no production workaround, allocation subtraction or repeated-until-green test.

The headless allocation facts retain their zero assertions. Captured upload words and actor memberships
are checked in a separate encoding frame. The measured uncaptured route shares the submission owner,
readback registration and Submitted history callback with the captured route. Recording observations
prove command encoding. Native facts supply the selected words and completed outcomes.

The observed 200-byte reservation allocation came from interface enumeration of the skinned draw list.
Replacing only that traversal with its existing count and indexer removed it. Restoring the traversal
made both native measured frames fail at Prepare=200 while all other coverage intervals stayed zero.
Changed-actor recorder isolation separately removed Resolution=5696. The recorder's payload copies and
fresh containers belonged to test instrumentation. No production resolution fix or allocation allowance
was introduced.

`Thirty_two_mixed_actors_are_independent` submits one correctness batch with reversed actor and material
order, one or two commands, reversed command inputs, seam and refinement variations, and independent
covered and refused actors. It compares all 16 words per selected vertex and every outcome identity.
Its allocation and resource-creation observations describe cold batch configuration. Two cold waits
come from `Scene3D.EnsureSize` and `RenderResources.DisposeTargets` while replacing the initial MRT
configuration. Both exact call stacks remain in the native packet. Outcome-copy recording and completed
enumeration wait zero times. The separate warmed two-actor and full-ring facts require zero production
waits and zero resource creation. Cold complete-render and complete-submission totals are observations
only. The cold render total explicitly includes test-owned wait-stack capture, which remains enabled to
retain both call stacks. No captured bytes are subtracted. This batch supplies no warm 32-actor allocation
or timing claim.

Native packets are `coverage-batch-warm.<backend>.json`, `coverage-batch-mixed32.<backend>.json` and
`coverage-batch-full-ring.<backend>.json`, emitted only through `KE_COVERAGE_EVIDENCE_DIR`.
The mixed packet retains independent expected words, actual device words, native headers, decoded
outcomes and cold wait stacks. The frozen 72 constructions and 144-row manifest remain unchanged.
CPU 2 ms for 32 actors and native GPU 15 percent overhead remain unmeasured.

## Commit and native gate

- Accepted Task 6 starting commit `4357d7dc3d29a7df6e2abb38d6a67eb2922c5430`.
- Implementation and tests commit `d0ce71e90eb9786819eb4e2a532b38c5422426c6`.
- Native backend MetalNative, device Apple M2 Max.
- Required gate passed 105/105, including seven executing GPU facts, with zero failures or skips.
- Size/layout/earlier-phase regression passed 64/64, including ten executing GPU facts, with zero failures or skips.
- The unchanged 144-row reference output manifest SHA-256 is `62f84ad4d131de458754d7b4ef69bbaaaf0dc38ed004ebe4d2f0695cbde66b5f`.

The local build and tests ran on the exact source tree committed above. The checked-in evidence
commit changes documentation only. No scene integration, geometry certificate, timing, performance
budget claim or load proof is part of this gate.

| Backend/device | Device word gate | Native compiler/pipeline gate | Synchronization validation |
| --- | --- | --- | --- |
| MetalNative, Apple M2 Max | Passed, 72 frozen mode-0 cases and 72 separate skin-through-select cases | Passed, every phase thread ceiling at least 64 | Native execution passed, Vulkan validation does not apply |
| Direct3D11 WARP | Passed in hosted run 39210816991 on 713da9182, 280 of 280 with none skipped | Passed, FXC cs_5_0 compiled every phase and executed natively | Not applicable |
| Vulkan lavapipe | Passed in hosted run 39210816991, 280 of 280 | Passed, native execution | Passed, 513 tests under synchronization validation with no VUID or hazard lines |
| Metal Apple Paravirtual | Passed in hosted run 39210816991, 280 of 280 | Passed, every phase pipeline created at 64 threads and executed natively | Not applicable |

All four device rows now pass, so the hard gate before Tasks 8 to 17 is closed. On every
backend the hosted run executed Every_production_fixture_matches_every_reference_word and
Complete_skin_through_select_matches_the_same_uploaded_input_words. Local HLSL and MSL
cross-compilation and SPIRV-Tools validation did not stand in for these native rows. The Direct3D11
test step took 11 minutes 11 seconds, which issue 1503 tracks as a cold-cache compile concern.

## Task 8 CPU envelope and origin admission

CoverageActorBounds is host code, outside Binary32Kernel. It caches each bone's influenced
source-position box, the interval of exact sums of flushed vertex weights, per-bone/per-slot
weight ranges, the nonnegative flag and reachability of the rounded total's 1e-8f identity branch.
Only cold preparation visits source vertices. Frame preparation is O(bones) plus fixed interval work.

For nonnegative weights, each exact weighted position is T times a convex combination of points
in H. H joins the transformed bone boxes and T is enclosed by the cached weight-total interval.
This uses the joined form, with no normalization or independent slot totals. Signed hosts instead
evaluate each weighted matrix coefficient through the four left-associated slots and then the
three-term point dot, applying Round32Envelope at every multiply and add. The identity source box
is joined whenever the rounded weight total can take the identity branch.

Let u=2^-24 and eta=2^-126. For every finite operation F(RN32(x)), its error is at most
u*abs(x)+eta. This includes the midpoint that rounds to the inclusively flushed smallest normal.
The nonnegative path bounds the absolute expanded product sum P by the largest per-bone absolute
point-product sum times the maximum total. A path through the four-weight coefficient blend and
point dot has at most eight rounded operations. With S the source box's sum of absolute coordinate
maxima, the additive enclosure is gamma8*P + (7*eta*(S+1)+6*eta)/(1-8*u).
Seven coefficient flushes per row propagate through the three source coordinates and translation.
Six point-dot flushes remain. All relative amplification is bounded by the denominator. Absolute
coefficient and dot bounds also check potential intermediate overflow before using this finite
error model. All sums, products, divisions, square roots and endpoint conversions round outward.

Round32Envelope applies the monotone RN32-then-F map to both interval endpoints. Its endpoints
retain signed zero and the inclusive 0x00800000 threshold. A binary64 outward interval alone is
never treated as a binary32 rounding enclosure. Refinement expands each skin axis by
gamma6*max(abs(min),abs(max))+6*eta. The fixed positive interpolation weights sum to one, and their
edge/interior graphs fit this six-operation relative and flush allowance. There is no frame-time
recipe loop. Tests use separate integer RN32 skinning, edge and integer-barycentric interpolation,
including shear, unnormalized and signed weights, identity, subnormals and near-limit magnitudes.

ExpandWorld and OriginError share one conformed local envelope. It starts with the refined box
and the maximum command recession times outward column lengths of the interval inverse model,
including RN32-then-F of the resulting local endpoints. It also joins every command's actual
rounded conformation graph. Commands do not accumulate because their regions are disjoint.
The Recess field is produced with Derive's binary64 actor inverse and direction application,
rounded once to binary32, then passed through F at its device read. Each local move encloses
F(RN32(before - F(RN32(scalar * Recess)))) with outward interval operations.

The scalar ceiling includes both the nominal Conform check and the corrected full-target check.
The nominal check admits F(RN32(full*ZScale)) <= cap, so
full <= (cap+eta)/((1-u)*ZScale). ZScale itself is enclosed through its pinned square/add/root
graph. For a corrected total t, let d be the flushed Recess, L the flushed actor linear map,
P_i the refined coordinate magnitude and b_i=(u*P_i+3*eta)/(1-3*u). The rounded local delta is
-t*d plus coordinate error at most gamma3*t*abs(d_i)+b_i. Its rounded linear transform has
coordinate error at most t*A_j+B_j, where
A_j=gamma8*sum_i(abs(d_i*L_ij)) and
B_j=(1+gamma5)*sum_i(b_i*abs(L_ij))+5*eta.
Reversing the full-target distance graph gives an upper bound N on the norm of its rounded
world delta. With q=(cap+eta)/(1-u), N=sqrt((q*q+5*eta)/(1-u)^3).
The triangle inequality gives t <= (N+norm(B))/(norm(d*L)-norm(A)). The denominator uses its
outward lower endpoint and must be positive. The larger of the nominal and corrected ceilings
feeds the local move enclosure. A transition's rounded applied scalar lies between zero and t,
so the same enclosure contains its recomputed move and the exact zero/one copies. Invalid
directions, scalar ceilings or local endpoints refuse. No source or recipe loop runs per frame.

World bounds retain the common scalar when transforming the recession direction. The exact
image of before-t*d is enclosed by worldBefore-[0,tMax]*(d*model). The local multiply/subtract
rounding adds a symmetric coordinate allowance of
gamma2*tMax*abs(d_i)+(u*P_i+2*eta)/(1-2*u), transformed through the model as well. These command
boxes are joined with the nominal world sphere expansion, never accumulated. Keeping the scalar
correlated avoids inflating the world box through independent inverse/shear coordinate extrema.
The final model-dot error is evaluated over the larger conformed local box and includes copied-field
flush residuals. An ideal scalar world budget alone is never the drawing envelope.
Origin admission uses the same conformed local box.
It adds the Euclidean origin-subtraction/upload residual to sqrt(3) times
(gamma7*maximumAbsoluteProductSum+7*eta). Copied-coefficient flush residuals are included as well.
Nonfinite intervals and potential dot overflow refuse. Allows uses error <= (double)clearance*.25d.
Every sorted command is checked and the first exceeded margin owns UnsupportedTransform and its IDs.

Task 9 and the draw/history tasks consume this one world box for culling, caster classification,
the coverage sphere and rejection rectangles. CoverageSurfaceBounds.FromWorld reanchors it with
outward subtraction, without GPU readback. This guard bounds model-coordinate magnitude only.
It does not reinstate the removed render-pass certificate or certify raster, depth or temporal math.
Uncovered actors retain the heuristic tracked by issue 1492.

## Numerical and publication proof

The frozen-pose test calls the unchanged 144-row reference-manifest test first. It uploads each
frozen Posed segment, starts RecordPhases at ValidateSource and records through Select. Every
field in vertex order is compared as its uint word, including zero signs. It then hashes the
actual Current canonical payload and compares it with that case's unchanged mode-0 manifest row.
All 72 constructions succeed, including the six B39 certification negatives listed below.
Certification is not performed by the GPU chain.

The independent full path uploads the same source attributes, integer skin indices and composed
palette words used by CoverageReferenceSkinning.TrySkin. Its reference then calls TryConstruct on
that recomputed pose. Production Record executes Skin through Select. All 72 output/failure pairs
match. This path never compares its newly skinned pose with a manifest for a different frozen pose.

Construction negatives compare the complete CoverageFailure, including original command ID,
equipment low/high words, count and limit, for codes 3 to 8. Additional executing tests cover
invalid vertices before faces, triangle-order sign disagreement before a later invalid triangle,
full no-motion and signed-zero record copies, duplicate shading corners, tangent W=-1, retained
outcome identity, and an early private candidate followed by a later failed gather. A refusal
publishes the complete source payload and zero canonical candidate records. Peers continue their
same-phase work. The final Current header equals the Results header.

## C8 basis split and equivalence argument

The unsplit GatherAttributes module measured 40,412 exhaustively inlined SPIR-V instructions,
with 82,612 HLSL bytes and 89,926 MSL bytes. Its structural bound was 46,566. The unsplit
ValidateFaces structural bound was 46,470. Both fail the 30,000 instruction gate. No cap changed.

PrepareNormalBasis and FinishNormalBasis split the complete pinned TryBasis graph. They use
Work header pair 42/43, one checked 46-word record per face. Each face invocation initializes its
own code and state. State 0 has no changed face, state 1 publishes prepared inputs, state 2
publishes the twelve complete vectors, and state 3 publishes only a failure code. Finish reads
payloads only in state 1. Gather reads complete vectors only in state 2. Dispatch barriers
separate all producer and consumer phases. Words 38 to 41 remain untouched.

The basis-word substitution is exact for these reasons.

1. Before and TransitionAfter are immutable throughout normal validation and gathering. TryBasis
   depends only on those vertex positions and the face's three fixed indices. Command state,
   shading rows, authored directions and gather sums do not enter the basis computation.
2. The two producers preserve U, V, S, T, Cross, both strict Unit graphs, alignment, both
   determinants, both scale roots, thresholds and the six XYZ inverse-vector divisions in the
   pinned order. All divisions and roots go through covScalarJob and one exact-worker pump per
   physical phase. Serialized words have already passed F, so storing and reading them applies
   only the idempotent F(F(x))=F(x). There is no additional rounding.
3. Therefore each stored basis vector word equals every repeated reference TryBasis evaluation
   on the same immutable inputs. Consuming that word changes neither a subsequent operation's
   operands nor the normal/tangent scalar graph. Flush tracing is not a GPU output or guard.
4. A moved corner implies its face is changed. ValidateFaces therefore consumes that face's
   completed basis status before gathering is allowed. Preparation records no public event or
   atomic refusal. Triangle-order reduction combines each face's local code and orientation
   sign, preserving the reference's first face failure even when a later producer also fails.
5. Gather still reads every stored corner without deduplication. Each direction uses the pinned
   transform, strict Unit, sequential component Adds and final Unit. Normal rows are shading
   rows, tangent rows are vertex rows, and tangent W remains the Before word. Early candidate
   records remain private until the final reduction and actor-wide Select.

Every physical numerical kernel has one exact-worker call site. The size audit checks the fully
expanded call graph, rolled loops and constant vector/matrix/private-array access. All 47 catalog
modules pass. No existing module hash or size row changed.

## Phase sizes and native pipeline ceilings

Caps are 30,000 exhaustively inlined instructions and 100,000 bytes for each text target. All
threadExecutionWidth values are 32. CorrectFaces retains its 776-byte MSL headroom.

| Phase | Inlined SPIR-V | HLSL bytes | MSL bytes | Metal thread ceiling |
| --- | ---: | ---: | ---: | ---: |
| Reset | 559 | 3010 | 3622 | 1024 |
| Skin | 18256 | 61095 | 67607 | 768 |
| ReduceSkin | 306 | 1857 | 2289 | 1024 |
| ValidateSource | 448 | 2934 | 3785 | 1024 |
| ReduceSource | 307 | 1857 | 2289 | 1024 |
| ValidateAliases | 865 | 3752 | 4929 | 1024 |
| ReduceAliases | 306 | 1857 | 2289 | 1024 |
| InterpolateGroups | 10403 | 53515 | 59298 | 1024 |
| ReduceGroups | 305 | 1857 | 2289 | 1024 |
| ValidateAttributes | 19538 | 64547 | 71343 | 1024 |
| ReduceAttributes | 306 | 1857 | 2289 | 1024 |
| FillBefore | 355 | 2121 | 2691 | 1024 |
| ConformGroups | 22238 | 86707 | 94814 | 384 |
| ConformBoundary | 13519 | 63309 | 70720 | 448 |
| ConformResume | 7151 | 18203 | 22304 | 1024 |
| CopyPointAliases | 618 | 3184 | 4226 | 1024 |
| ReduceConform | 597 | 3819 | 4426 | 1024 |
| CorrectFaces | 23393 | 91211 | 99224 | 384 |
| CorrectFacesOverlay | 20280 | 87142 | 95185 | 384 |
| CorrectFacesFinish | 10457 | 53891 | 58980 | 1024 |
| ReduceFaces | 614 | 4032 | 4639 | 1024 |
| GatherCorrections | 2179 | 8225 | 11541 | 1024 |
| CheckFullTargets | 17077 | 62395 | 69322 | 1024 |
| ReduceFullTargets | 597 | 3819 | 4426 | 1024 |
| CopyCorrectedAliases | 561 | 2910 | 3961 | 1024 |
| TransitionGroups | 1995 | 6506 | 9616 | 1024 |
| StoreTransition | 920 | 4786 | 6218 | 1024 |
| ValidateVertices | 715 | 3607 | 4600 | 1024 |
| ReduceVertices | 305 | 1857 | 2289 | 1024 |
| PrepareNormalBasis | 24940 | 67095 | 73789 | 640 |
| FinishNormalBasis | 14393 | 54671 | 60402 | 832 |
| ValidateFaces | 21385 | 64745 | 71898 | 512 |
| ReduceFacesOrientation | 465 | 3541 | 4097 | 1024 |
| GatherAttributes | 23020 | 66060 | 72979 | 640 |
| ReduceAttributesFinal | 305 | 1858 | 2290 | 1024 |
| Select | 426 | 2654 | 3171 | 1024 |

## Shader hashes

These hashes are the precise compiler artifacts recorded in shader-corpus/corpus.txt and
coverage-sizes.csv at the implementation commit above. Text targets are LF-normalized.

| Phase | SPIR-V SHA-256 | HLSL SHA-256 | MSL SHA-256 |
| --- | --- | --- | --- |
| Reset | 00b5ac37c99d8df63ea4c5ec7e1cb113cfe2ca39c0084b8b91cb3301416be299 | 9debc0e662c87f619573d2c8a6165e917314ffd22fe778a24901b4afd9f32c8d | 715529f9d8ee6ed2f347911d2525ef963d37cf47e3fe1a4f6771ab1f3ba38948 |
| Skin | 88b2a71fec9fd136a182e6e2df6acc52c3a41699d0ab759357019d5579f14aec | 965203b7235cb1da54e2cde377503845ac8efdeeab1fbeafe771f3f5b2c1d112 | f4e7c9f80f061fa6714dc353da53affd2dde3670c7698119b3dc6bfc18925335 |
| ReduceSkin | 18082bcb2e13429b89184fd5dbbbba87d9f5978314c96f6abd2018cec651af03 | 834325ebc052c0e9fd8528acbfda8c26a1f4589385837558b8161f8bb2f1fab2 | e8dbb1eeb87c9a73eb66d6b8a9443b2d35912a58d450cecddf8a18c4ca058e18 |
| ValidateSource | 09e8d8e4d36cf25c73852b831f289c8a785902f662375301d5ba62b7524963b6 | cc485fa000f0c0be1aa96b90786511029a27bb4ff062d3566652031369d4940a | f1e90c995e18c6a8d59d3d3eba295d512efd7d25d03959d717060e7c2a865384 |
| ReduceSource | 015f7f79f4d29a713f337d2fba55fe812cd79cdb140c3850dd09bed0e3a9756d | c21905f4c6e259530492e19cfc3bd1c97fec9b053082cc2b7daf1b5d23d69a52 | a0b6b5f786b16ccd3827312be1ba02f6ee1f0ea21f105446f4340fad3b6a40ba |
| ValidateAliases | deed0222b8a4d8f82ae65e372618ec58494b3babf207b71e51d7c7eadf9234d3 | 5837de6a330e96e93496375832b151a3046e72a1fd085d851b092d6b02114c8e | 51171d5a2594aab89068295a60e0f0b1c74f438dea02adc4867e5525690f2f11 |
| ReduceAliases | 9763945150c17657052d828cc803d351b89c2e253770341974402c161b12d06c | 972378fe510a66ca8a5a9535781e50c40c801adcd2c1a21e26e143a2f4b5c9e2 | f0e1b5ca9af0cf64a8764e6cabd5e9bc211582d9b60b851f249b337fd8bc807b |
| InterpolateGroups | ea582182abc9d0c780f74464a153cc3991fea441c0006e4fb28d392616fb855b | 73ed19f171871234a637c214b9c0d8eec44570ba8c64a95176228cb81aad4c02 | 2562d370c5164a131550f3a8f0c3ce978660047e5ad93e86cbde03d4dab63181 |
| ReduceGroups | 5ceaf652a2b1f588d2919655c3a4ae4b3aa661900cd07c1c71b36243797d2cf3 | 283d14187262dfe05178192cfb33871ff5080a74cef54d6f9e745f2ccdcafe60 | 2fbd96c49d1da9771bd0722707cd116c1deb8eb8f4602307004fbadadba862cd |
| ValidateAttributes | 2b9e58e78c99602a1581a2c5ed917d195e083d3ef74c2d0d49d42a40d0ffe027 | a638a4bf258c36294beeb482d5e5309c952af4ebd41cc6a83046a0de79e38415 | d7fcabf027466dc87d4dcbf8f489ea78fd39fda438ee221d35e6ff3b91fe3bcc |
| ReduceAttributes | c910a0b2ba1b581ec2b7aee5dfd979811b1ee67c41be2f70eb1975b2e2117e54 | 775fdc942e7542241453a53b47c5df39784caed757adea1d0090c506fb95be0a | edaf6abc4d42eb1087092004b03019136ff3e78df2fe4d8cda22b48f8e15cffc |
| FillBefore | c75d62f4263a89bd130861dfa1ceeeaf38e2c532eb1b742244817ccb2824696d | 8fb794acff18e25a1c42cfc613165353d053a67fe810fd88d517107a89b30033 | aa5731ce140f5bc08bbea9712cff08bce83e2a506a4ac71a6a9cd3cf6942a451 |
| ConformGroups | f0c1b8be8acd252f2657c294f7c05cc7161da7d9d86cf506e13d963291c2162b | 7cf5561aaa16481dcc00d87de631084d4dc5f01d4ecf78e83721c2c99df43e8b | 314cf6122ae83fd80f6ed93daf38d581e8fbcf9556f5a943513214f93e1be45e |
| ConformBoundary | fae3329600c79e2acb756d7d08e22eddddc3b5a54eca385a5f6a12658a3e1dab | 25218ab406a05a5850ccb690bbd97fd4aef3ae7ebf5c82d224497757b6121041 | 9951078edbfa97204949a21917089ddd3fd6f9fbddaa088babe646b78e0e02f0 |
| ConformResume | dff3939df714eac8f8ae52794cc1720509392a96b0d4001a3649aade3926a14f | a380a35ea8a8ba53824e47ba360ba7858aab580af98ac022d14335591a37a79e | 62e3b0cdac8dd8235d21c5073f76b4dc6f4fe8ab73b1bf3c8847eecee8cf6282 |
| CopyPointAliases | 6889c5d3fc1c084f766789d496cc49123c42bb0dddbf355e03d0654e84b39251 | 7053c3fa6cf6da28c425d63fbe9e323aad0eb04b3ccf23ad39fa01d10324d32a | 6f4f5daa31c6924152aee6e651dc68c0a0ff44b32739bde9b662de6aa51e95b3 |
| ReduceConform | ded9246696967aa601164f7c51c0689d12e5645a29bbc2fa8f0e736ef41eef52 | 00facaef3b7915f0c9945ac1768f384346ae3b92f46a7ad4b88a1b6d1cba3e18 | dc99e6d4199c649e46ff3a44ff5192ad780a6a4d7eaf1f398c2de7953fdd0aee |
| CorrectFaces | 0e74d19215ff5f16bf13e0f5e7decda509440a3d48fe25535f2298a738af0974 | ecf72f9ad565c647671bba14a021abcba559681e7eda34f3eab35401c752a779 | dcc8f0f0d893052266017ba0ef2a910a55282985b4956b97afa0ed0b0ac36a76 |
| CorrectFacesOverlay | 54249e618f180c1452a7948aad9521952875d579c7862a927c7b9c849f2b8668 | 82935b74e4f1f2856947c2891e9059f9811e199f2c4cce5853bbef87f7618435 | 1de3e4a6acb9b974c8e37d322e0669d95bd055a28da3429b25b2786dad1af942 |
| CorrectFacesFinish | db23822f6de0847a9f506f26cde33960d151f63fc5678105db2a4df2e39f5586 | 5c2e95c2d313a62da52c096e584182148dc8880def6b8fc29270b32cc9594e45 | 57c4108cdc7e9e0ff8dbcd2b933f5416bac646c3d91ea08cdcad5307ab12a8ad |
| ReduceFaces | 4661c2237b8c4508b934355b13f4c28dcf340bd05e60ad475d6d51e96adbd6f6 | c306fc761baf017281fb159ba5588ea3e1f5212209392b0117d18c01aca65e32 | ad0e41eb13cb0bf44ceda24eb5ddcd24d68345351cd9e1dd4bfe7111cba9d563 |
| GatherCorrections | aea7876fbdb3864817314a9cb3a26a82f3e7f229b83cd45b9574685d67fb52f8 | 05ea742bcea5ec92ab75c8218a7aedea742a64c5cb5d908c10242ac2651ee509 | 63e7bb9cbf2b10ec0d53621f2c8b1f32281357060f548ddb7d247d3058804b04 |
| CheckFullTargets | 6cc43791e6968b9fded323bd13a554d3bce0e176efcfc36ecaa41e40267a9c49 | 971c727dea99f24ab43f88458b68c2750c8928e2dcd554a3bbf8e6d7eb886680 | 6aa54eeba9bb873a40cb43dbcbf65791adc4109e4185556f8a08d68086b6fde8 |
| ReduceFullTargets | 0632d68ac454736775310ec784a8ea0aaa47afa95f8d7ee400877cf8cf388f7d | 002fc06b1c2550016eb424fe5614332094fe140dbd792a29daa8fb1cf542d71a | 5fa88fdf2d3895b3adf168a4ec937f190599073561a07202cb5dfbbadc27e0c4 |
| CopyCorrectedAliases | 58f439888846e2a50ee11ece5256f29c925141a6b40ae7c3846f8deb7c0a1312 | 502737b966c00af07e2e3de79f48c20d7b69eb9d2f7000619bebab061b778fd6 | 03dd0dfcde22df244b3fe68fb05c56d83e96aa22d13d5f55e16529ab87352f06 |
| TransitionGroups | 41e1331ef0c6f1cf43d007669f6e2f9122145cbdcc17b17b7a1d73e7e6116a42 | fb4c206ed3f87615f8a44282c093379329798d15b5c3205915f795c2cd3fedb5 | 041a1d7715785b022188f17f8ee84f633f3193e802939b5b853685a7356b0e56 |
| StoreTransition | dbfd13252069c171945005746935a592fd7a928266bf621616c9058e0739879e | 85e46f6b0b02dc3634577b44593521603652e4c973af410c4d00d5109dca9abd | 166715aebcded45e6e7eebfaa8ed08db81cfaa24c34cbbbcaa16fe2751609597 |
| ValidateVertices | 576dfb7f02a963f842a6a53ad359b84b35493323d233d0743b75e3a37f0ee400 | 50324495ec1aec8e89075613cedd5ad29881cb2a007748aa16535184af8fddcc | f67e8c43b52de76ebd3942429f6316d1a085a1ebb659b65bf79629c7b7821a70 |
| ReduceVertices | ae6ccec75fe176855841239e8f3fac57d862ac57e66355364094e41df05a4f83 | 865ac34ab506716704bbec5d961ed14d1b6b8339f3dbbba31d72f413045829aa | 2b30ad3375ebe66f1d9c11168b82718477f2b78d7fe076df19fe03fa0b3a77ad |
| PrepareNormalBasis | 4f4aad6735a73b6a134604e87fa9b1cae3a2bdd0e519f8f2fdc97afa272d608f | df26000f82a0eb8c188622950fbc51d64261c493359c377f9242c30f14f603f8 | 7f83fe4132fac89abb463899a0641d81949b623c9300dc8c8e6352a55f6784d3 |
| FinishNormalBasis | 6f4161873af4d4f055dbdd0940a8e734848d43ab62403adbc062654beef7994b | c6c3507dc2ef0b437f044b11ba954e65266782f97055cdf65d6634e2aca620d2 | a3cb8a59870e5919edac6a9a885488bb7b175b270b1b4d33031f19f55b897f39 |
| ValidateFaces | 9d1471241b07edff7b7f7dcc93a3b4c565cf30fe5c21be7d76eb64728d3ca43f | 9fdc6af01296170a6cf10ace2edaf4d01eba129b93ba7d7364a1613526dbd59a | 275c82aa0b1e55f1085e73415e1dc60fb94b6e67bc655dc81a093584e9d40783 |
| ReduceFacesOrientation | 797c4f8fdbfc3ab801980e9a8623da06bfe604298b8ddd76698181bc3d33e623 | 60bfaef4a66930f2eca2590389757e0f5a92fb37af8ec4335fd251861c945578 | 2d31d74ce1a7851575081e2d2882c2f07f0102b47b5c49c4453d825e5c5456cb |
| GatherAttributes | 3efa1b107bfccf3614c5f354ab832695ffad5def21ea933fea2192d32d0e9b30 | 7ad9e6fc8db2a0b77daf1a9ef0d183aa72722cf0fc4319c4cfe6bb76068b8b17 | c2bdceb17904231b9d6adc65d4c8fc6cadf5a6a3c146a3494495edec531f9362 |
| ReduceAttributesFinal | 0fc022aef11e38ca10d5e2ffd7cfd67954d543ca3b3b0a492e1783ebf3a516e4 | 3a396374f8c0c8e1f1a5b21e1754608ad59ca04b735bde4895cce49698ac6e71 | 839cf090f56f6f43749d0a13075e6a1c732b09c8bc171d7080bfa296f8b8317a |
| Select | 3ce270f9eac9b5d75aa6f8b533af865e28cdc70326a84cf179bf54007ec1d389 | 39e83c306b45ca534a39d8e0a0c92598824ef03640bfc17cd08923f877e019fd | 0ac08c0e5b3dc9788119a6d8b8a2c07ab472f03dc38517c9be861073af77a81e |

## All 72 frozen device comparisons

Every row below is from executing native Metal Current-buffer readback, compared with both the
reference words and the unchanged mode-0 hash. Every differing-word count is zero.

| Case ID | Native mode-0 SHA-256 | Vertex words |
| --- | --- | ---: |
| cost-limit-l2-bent-w0 | a5005519d39b177d36d9aa25492b30839b7dafb23742253b156f24c40b41124e | 19584 |
| cost-limit-l2-bent-w05 | 5c7b7fbcdffdb2fdb0815d14375e293acfde98e3520016428b0d5d7bd3fda845 | 19584 |
| cost-limit-l2-bent-w1 | d623cd48525b99c21736875cb2bd8ee328647adca793d400c418448f0311a335 | 19584 |
| cost-limit-l2-lifted-w0 | b5789fc43672bfde45443b8e1cf8a93d0111047ff164cdf502cbf75aeded86a0 | 19584 |
| cost-limit-l2-lifted-w05 | 74d979c3611819c1616f7a4618ddf032dd9a46a11fac949a5cbc4c85e22c7e03 | 19584 |
| cost-limit-l2-lifted-w1 | 4104f7183967733e67ba34a2a8ccb8acbe1aaf2006fe0368fb6e78e47b9f5015 | 19584 |
| cost-limit-l3-bent-w0 | 99f0688714ab4d0c813f03ca268cad941b177f780eb6c12cc34e4544645f6257 | 22848 |
| cost-limit-l3-bent-w05 | 04de61d3d401f2c9902d5c2057179b9ac2898817445ed62a5ec14b3b47c3ccb4 | 22848 |
| cost-limit-l3-bent-w1 | 493d240674051da48a1bc11ec12a4f72683fe2d5fa912161c2b86a7d0e3887ea | 22848 |
| cost-limit-l3-lifted-w0 | 598452919ad12cb8a7364d22cfcacab6b7d525185321c379b622db4ba050222b | 22848 |
| cost-limit-l3-lifted-w05 | 47dd12d933eaffc6675559224742e7447af96dc505c4f01295012394614553f4 | 22848 |
| cost-limit-l3-lifted-w1 | a6be35cc05eeb3c92b3b40a9b89f5530a74e475c4fed72525343d768908f5be8 | 22848 |
| cost-limit-l4-bent-w0 | ba0024e202b6aa83db374278b8eabc2ff4e54baf9403c64457d8d35a724e243e | 27264 |
| cost-limit-l4-bent-w05 | 2f9f98813eeca43b9286dbdbfe80737f679a5f4fab4802d5bb87df32fcbf158d | 27264 |
| cost-limit-l4-bent-w1 | def0b8aab14b1670ba05cd5c480cb61caadeadc2e5318cc9b6dd5edbdb0604af | 27264 |
| cost-limit-l4-lifted-w0 | a5bf9a6a6a80e599347321a0ca9a4924db02c1d165ef34a94e27774a1cca4f68 | 27264 |
| cost-limit-l4-lifted-w05 | 76192a294a0c8863a56e452e7601d399dc823555c068fe5a1e68553e66c17b9b | 27264 |
| cost-limit-l4-lifted-w1 | 88a254eaa04f7232085d49f59ed5ab0eb01bb3dcd2b2e347923df0111443d952 | 27264 |
| cost-typical-l2-bent-w0 | 011c1ba146ed8d4e55a1a11a11fd82b4d0459a4fb7f3a7b4e5c56ee1df261768 | 20128 |
| cost-typical-l2-bent-w05 | 4fbda6369f468c3ae98d4a9c7303dc2088c2c3a28c3eca7a446796bdf533e80a | 20128 |
| cost-typical-l2-bent-w1 | 503d6d72817ca748798b145e0265f97b8c8544d18259c638580a4d06947ac73d | 20128 |
| cost-typical-l2-lifted-w0 | cdb1f138b7457e3fedd1d62c2fdb1f82861783aa9b1b2a107728004604276635 | 20128 |
| cost-typical-l2-lifted-w05 | e11864fb6a7155781d9d819307d495956644a1312ea6f5d0c7e88d7575075eea | 20128 |
| cost-typical-l2-lifted-w1 | ec69ec4217ed8511b76fd8211d8e34cc0b4ca2c4d9b7194c7eb24be01e46de96 | 20128 |
| cost-typical-l3-bent-w0 | 950fbd93e29d08cb176ac72123a774d370f05818efc9f9a32bd6524a37f12f56 | 24320 |
| cost-typical-l3-bent-w05 | c3b630f387a42b63fcd8adf96f7adf5c5ce6304828a281057a257583bef0f727 | 24320 |
| cost-typical-l3-bent-w1 | 2aedca9b4b0a3e5736576f404e822694de96f3d82e8d5c4fd07feaa4ca90681e | 24320 |
| cost-typical-l3-lifted-w0 | 818334620803d45edd134b94d7d83e02b53fff77497e60c3ddc1e506c232e89b | 24320 |
| cost-typical-l3-lifted-w05 | 0a50607174dc7fbf74fdba8aa36681c890d61c59e4096b4bd45a17a17884395f | 24320 |
| cost-typical-l3-lifted-w1 | 6d78bcaa8c81496eba91d4d7c3179474affcee274015ca52186abbc7c84bf8a0 | 24320 |
| cost-typical-l4-bent-w0 | 21e9ded6f7400c2c728bee9a87e89bb21109c96ac0b0c1bc6b133803bdb53790 | 30048 |
| cost-typical-l4-bent-w05 | a6af296c0d1dc7454e9a50a0a6df8b5d12a98721b17ba96a280936d278485ce1 | 30048 |
| cost-typical-l4-bent-w1 | b0f352a4a3346daab1230f8e1883f7c52b89d8b9176caf77002976d2bf1f5ef3 | 30048 |
| cost-typical-l4-lifted-w0 | f69630a8623637a57aadcbfd1c6d25c16c4c865dec039f3fa0cd06831b5f091e | 30048 |
| cost-typical-l4-lifted-w05 | 1d1dbe48651f9042d6529614f282b08fb9dede4413677ed804beefc427765a8d | 30048 |
| cost-typical-l4-lifted-w1 | 54fd220be95262ebd37dfa39651065d9358c4855c790c45982b165914e95abe1 | 30048 |
| curved-challenging-seams-l2-w0 | 1714464931fea3bc29b993f0848b6dbbe539b51c3aba67e1abbeb69b730b4e21 | 1856 |
| curved-challenging-seams-l2-w05 | be95f80f50ba2ed5f9d90d1e5e3c7bfd83e14b8523cb7985c6f9cf1270aa9e50 | 1856 |
| curved-challenging-seams-l2-w1 | 129e23d29d8370951dd02553e31788853d5792c6a4059b9d87f5768dc631fe9b | 1856 |
| curved-challenging-seams-l3-w0 | 38e14319d74915a01bb37741b036a503eaa3a75ae22f6b80f814e30b73830a60 | 3328 |
| curved-challenging-seams-l3-w05 | bbdd1917e1bd80e9f338a0483fdfa9719c8a0f75b483574cf5bb96cb30576c22 | 3328 |
| curved-challenging-seams-l3-w1 | eb6acdf6a618bd011a53de55e1b4bf46d3124afa882752bd80d060e90a216b8f | 3328 |
| curved-challenging-seams-l4-w0 | 7cb48b3273e59ab195e29344047a8c51adca06f957a108fe2baa34ce4b4b8d32 | 5312 |
| curved-challenging-seams-l4-w05 | fa089fb44c5280b20a8b47b08b9398891b189e96adfe7a3b79698e1e63789e08 | 5312 |
| curved-challenging-seams-l4-w1 | 84c623667dff3e5706e189a964d951f4f616796b098794b87a39234aed59de01 | 5312 |
| curved-challenging-welded-l2-w0 | 0e8bd5751139dc42d0ca0c367f3962e46939b41749a2b2295c5f3c6e0bf36792 | 1680 |
| curved-challenging-welded-l2-w05 | 05b67968fcc77620b36c00019d9886d3464f17aa07e2461ce2e4a51ba68d35fc | 1680 |
| curved-challenging-welded-l2-w1 | 7ba8e55c7eec86b5052208e323b9297730054732f9ba6a8eddf9f68b7cedec80 | 1680 |
| curved-challenging-welded-l3-w0 | 3601e45a2bc15d79b15738ba4002c21c84bd93a50a14669ae95dcdcaf7cc95cc | 3088 |
| curved-challenging-welded-l3-w05 | d3dd646aedcb1fee51688307c0a27a3a9d37294da161268e66573592d1d8ecbe | 3088 |
| curved-challenging-welded-l3-w1 | 608710262e231805e36a97e42ac3a23a2e354c8ffa9fb6e70783cfd52e3678d0 | 3088 |
| curved-challenging-welded-l4-w0 | 42b2b001c09db2601791352da4527c5213f4b0592afeb965079dceba729db20d | 5008 |
| curved-challenging-welded-l4-w05 | 05848b96582bbec8421c9727164a9ed14e427eb8592c2c99bd54691478398711 | 5008 |
| curved-challenging-welded-l4-w1 | d834906404e69fe8defbd299d8726acf2924c02b500f9a0ea95391df74b45950 | 5008 |
| curved-ordinary-seams-l2-w0 | bbfd17eaf3df44f8ea74855c120aef2fcf7f0c2385358b4023f2359da63ce00d | 1856 |
| curved-ordinary-seams-l2-w05 | 0dcecf68332f5ba2b2e641478a2fbb841069bc34608c0e40b49cb4c38ac105eb | 1856 |
| curved-ordinary-seams-l2-w1 | 55cc63fa0a3c4261fa37d7447d25781bfbd6c565afdf8ef644a1fbdd6004a042 | 1856 |
| curved-ordinary-seams-l3-w0 | 79606ddd4e306605d2c872222a9624318a678ade4979ba80b5d351513d1cacdf | 3328 |
| curved-ordinary-seams-l3-w05 | ef4fe2e3b0f0653643e219fce5d435b4c3eacdfbcd1f2710f40b6b10f1592371 | 3328 |
| curved-ordinary-seams-l3-w1 | 0772ff5cf19239a5db6fee4723a880761b82e44b582cb1c0a707fa855ce2ed83 | 3328 |
| curved-ordinary-seams-l4-w0 | b21ab37265ffd1de9da6d9096e23f6c3f20679f558b9dd9b700aecc164e18bda | 5312 |
| curved-ordinary-seams-l4-w05 | 5ff4bc4bb7419246b1b38e6740027bded67b6f34c74b65ebda5da59268478503 | 5312 |
| curved-ordinary-seams-l4-w1 | 78142d8b9ba9e0abdeb396ceb9c422ed82eff79a1d2436e8b45e5d059b3f434b | 5312 |
| curved-ordinary-welded-l2-w0 | b7b6e804bd51142793e4e7b00b1dc696ddd0f58f3ce84936fff623a576a6c2e5 | 1680 |
| curved-ordinary-welded-l2-w05 | 604734033cdc7a4e10fc7c98fdd36ab3315826585f5a3b8be7825e43d51b16fa | 1680 |
| curved-ordinary-welded-l2-w1 | d99d26d1e5ca00eee7c8206ec1ef12b9c5d218c553657ee73d15557d04651041 | 1680 |
| curved-ordinary-welded-l3-w0 | daf6f5ff6b1133ffb6588398f061e460741b947476452f919a19e7995ef4b2fb | 3088 |
| curved-ordinary-welded-l3-w05 | 68625b7fc7223ef0b2fdc8298a4891028854080e4836b723549da8cebf46fa12 | 3088 |
| curved-ordinary-welded-l3-w1 | 3157370345587582fb51bbace18eb8d7580b8639c26429627b5b6c57bcf11cb2 | 3088 |
| curved-ordinary-welded-l4-w0 | 18337d2f53a8b9a4ee69e9f0b4ee5e0cbbbe4ec82aee8c8c740c8c85a3110f53 | 5008 |
| curved-ordinary-welded-l4-w05 | 03d611e00ddada41ddc5dba0efdddbd89e6fe863b33f9c6fa6edd7744f3f8a0a | 5008 |
| curved-ordinary-welded-l4-w1 | 4b909fb49aac6bae31a4ba8cba507baa29c60a43188b38a9bedc19787c6570d3 | 5008 |

The six certification-negative successful constructions are

- curved-challenging-seams-l2-w1
- curved-challenging-seams-l3-w1
- curved-challenging-seams-l4-w1
- curved-challenging-welded-l2-w1
- curved-challenging-welded-l3-w1
- curved-challenging-welded-l4-w1

## Reproduction and retained evidence

Run from the implementation worktree, one build-slot invocation at a time.

```bash
build-slot --label c07-build -- dotnet build KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release
build-slot --label c07-tests -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~CoverageComputeEquivalenceGpuTests|FullyQualifiedName~CoverageComputeSelectGpuTests|FullyQualifiedName~CoverageReferenceOutputTests|FullyQualifiedName~CoverageOracleTests' --logger 'console;verbosity=normal'
```

The plan workspace retains task-7-red.log, task-7-final-header.log, task-7-regression.log,
task-7-sizes.log, task-7-native-evidence.txt and task-7-report.md. Native TRX files are
/tmp/ke-c07-final/task-7-final.trx and /tmp/ke-c07-regression/task-7-regression.trx.
The exact size manifest is checked in. Hosted run 39210816991 holds the TRX, shader artifacts and
Vulkan synchronization logs for the three hosted device rows.
