# Per-body water depth for shoreline and submerged-object appearance

Date: 2026-10-24

Status: Approved by the owner on 2026-10-24. The implementation plan and subagent-driven execution
were also approved on 2026-10-24. Engine Tasks 1 through 7 are authorized, with consumer Task 8 owned
by its Grimhollow coordinator. Release authorization is separate and requires approval of the exact tag.
Implemented, staged for 21.14.0. Water optics checks passed on hosted Metal, D3D11 and Vulkan (run 40155111807).
Consumer Task 8 and native R8b remain open under https://github.com/APKiwiOrg/KhaozEngine/issues/1300.
The native adapter contract is recorded in the [R8b handoff](WATER-OPTICS-R8B-HANDOFF-2026-10-24.md).

Implementation plan: [per-body water optics](../superpowers/plans/2026-10-24-per-body-water-optics.md).

Execution amendment: [triangle-backed optical beds](WATER-OPTICS-TRIANGLE-AMENDMENT-2026-10-24.md)
supersedes this document's bilinear-only TileWorld reconstruction after the required saddle gate failed.
The original bilinear resource remains supported. The amendment governs exact mesh-source optics,
coverage and source-generation publication without changing terrain geometry or bank feathering.

Related work: [#1300](https://github.com/APKiwiOrg/KhaozEngine/issues/1300), the native world-authoring
R8b optical extension, and the Grimhollow duck consumer proof.

## Intent and success

A swimming creature must read as intersecting water, while the bank keeps a natural soft transition.
Moving a submerged object must not create a new shallow-water patch or shoreline in an otherwise
unchanged body of water. The same contract must distinguish water bodies at different elevations over
the same XZ coordinates.

The immediate consumer is a rigged duck with tucked paddling feet, occasional wing movement and short
surface ripples. Its geometry is already submerged by the intended amount. The required change belongs
to shared water rendering, rather than another creature offset or a special duck shader.

Acceptance requires both numerical and rendered evidence. Correct depths alone do not establish a
convincing water look. The controlled consumer capture remains a release-adoption gate.

## Evidence and current behavior

The current water fragment shader reconstructs the first opaque scene position and subtracts its Y from
the displaced water surface Y. That vertical distance drives body colour, ordinary shore foam and the
shore alpha feather. The first opaque surface can be a creature, bridge or other prop as well as terrain.

The consumer proof places the duck belly about 0.103 m below a flat authored surface. The river's
0.20 m shore feather nevertheless reduces water alpha over the submerged duck. A matched diagnostic
changing only that feather to 0.02 m increases coverage over the lowest belly and foot pixels, but the
hovering impression remains and the bank becomes a hard angular cut. No product material change was
applied. The repeatable private consumer proof is preserved at
[855a63dd5](https://github.com/APKiwiOrg/Grimhollow/commit/855a63dd52c17672f85eb1f162940ece3b706724).

`WaterBathymetry` already supplies a scene-wide depth texture for FFT shoaling and breaking surf. Its
coastal-strip coverage convention, deep-water sentinel, half-precision storage and single reference
level are not an optical input contract. Neither the shipped TileWorld renderer nor the inspected R8a
native renderer supplies independent optical bed data.

## Decision

Introduce an immutable, explicitly bound optical depth resource. It represents a bounded patch of one
water body's bed relative to a stated reference surface. Several draw requests may share that resource.
It is separate from `WaterLook` and from the existing global FFT bathymetry resource.

Scores below are engineering judgments on a 1 to 10 scale, with higher being better.

| Approach | Correctness | Stacked-water support | Simplicity | Total |
| --- | ---: | ---: | ---: | ---: |
| Per-body optical depth resource | 9 | 9 | 4 | 22 |
| Reuse the global FFT bathymetry field | 4 | 2 | 8 | 14 |

The per-body resource requires preparation and lifetime work, but supplies the missing identity and
coverage contract. Reusing the global field is less work initially, but cannot distinguish overlapping
vertical bodies and inherits unsuitable absence and precision semantics. The material-only adjustment
was tested and rejected as the consumer fix.

## Scope

This change covers the Render3D optical depth resource, water shading that uses it, the TileWorld
producer, and public synthetic plus private consumer acceptance evidence.

It preserves the native renderer's original-domain clipping, anchors and prepared-resource ownership.
The new resource must fit that extension point. Implementation against the unfinished R8a branch is
not part of this change. Native adapter adoption is coordinated with R8b after its prerequisite lands.

FFT wave simulation, wave refraction, cave lighting, camera policy and the existing region-level surface
height issue remain separate concerns. Scene-colour refraction and per-channel attenuation of the
underlying opaque image are not silently added by this design. The existing alpha compositor remains
the initial rendering path, subject to the visual gate below.

This is a contribution to #1300. It does not close that broader issue merely because the duck passes.

## Public concepts and responsibilities

The implementation plan will name the concrete overloads against the current scene and TileWorld
interfaces. The following concepts and responsibilities are binding for that plan.

| Concept | Responsibility |
| --- | --- |
| `WaterOpticsField` | Immutable CPU sample payload, rectangular dimensions and finite signed rest depths. Creation validates and owns its sample bytes. |
| `WaterOpticsHandle` | Scene-owned, generation-checked GPU resource identity. It cannot be used in another scene or after release. |
| `WaterOpticsBinding` | Associates a field with its local XZ mapping, reference surface level and coordinate frame for one water submission. |
| `WaterPlane` optical binding | Optional additive submission input. Existing calls with no binding retain the legacy scene-depth behavior. |
| TileWorld optical cache | Produces, prepares, shares, invalidates and releases fields from authored height data. It owns no shader or optical formula. |

The look remains reusable visual material configuration. A river and a lake may share a look while
binding different bed resources. Field identity must not be inferred from the look, a global scene slot,
draw order, or whichever water plane was submitted last.

CPU payload construction and shader math remain usable in headless tests. Scene and recording adapters
must express preparation and release explicitly. Unsupported rendering adapters report a capability
refusal before drawing. They must not claim to have accepted a field and then silently draw the old look.

## Sample meaning, mapping and precision

Each sample is the signed vertical distance from the binding's reference still-water surface to the
authored bed. Positive values are water depth. Zero and negative values are known bank or land samples,
not missing data. A field may cover land around the drawn water to preserve shore interpolation.

The field is a rectangular row-major lattice with independent width and height, both at least two.
Dimensions, multiplication and payload length are checked. Non-finite samples, degenerate extents and
unsupported device dimensions are refused. The producer must resolve missing source data before
constructing a field. NaN, infinity and a large positive number are not absence sentinels.

Use an R32Float sampled texture for the initial resource. Bilinear interpolation is explicit through
four bounded texel fetches, so the contract does not depend on float32 linear-filter support on every
backend. The CPU reference uses the same lattice and edge convention. Tests cover exact vertices,
interior interpolation and the final sample row and column.

Mapping is expressed in bounded local coordinates. The binding carries the corresponding frame or
anchor through the existing render-origin conversion. Large absolute world coordinates must not be
packed into texture UV arithmetic. Rebasing changes the mapping, not the resource's sample values.

Two vertically stacked bodies use independent bindings and reference levels even when their XZ bounds
match. Equal immutable sample payloads may be shared, but the renderer still resolves the mapping and
reference level from each submission.

Changing a water surface level does not silently change the field's reference level. That level remains
fixed until the producer rebuilds the field generation or deliberately re-anchors the complete body.

For a sampled rest depth `dRest`, reference level `yReference` and displaced fragment height `yWater`,
the bed depth is `max(0, dRest + yWater - yReference)`. The existing reconstructed opaque point remains
a separate input. Its vertical distance is not renamed or treated as an optical ray length.

## Coverage and refusal

An opted-in water request must have complete field coverage for its drawable domain, including the
certified displacement envelope and the interpolation support it can sample. Validation happens before
the request is published or queued. It is not a per-fragment guess about an unavailable bed.

A missing binding is the explicit legacy mode. An invalid, stale, wrong-scene or insufficiently covered
binding is an error, not permission to switch some pixels to scene depth. A request that opted in must
never turn a coverage hole into deep water or into a shoreline.

For a currently flat rectangular plane, the coverage check can use its complete rectangle. Native
bounded surfaces retain their original-domain checks and certified padding. An internal cell edge is
never a shoreline and must not introduce an alpha fade.

If an adapter cannot prepare the requested generation because authored data is missing, it reports the
missing dependency and keeps the generation unpublished. A previously valid generation may remain
visible only while its original geometry and source dependencies remain valid. New geometry must not
be paired with an old field to conceal a preparation failure.

## Shader behavior

Keep independent, clearly named bed and opaque-scene depth values.

- With a valid binding, bed depth controls shallow/deep water colour and its palette alpha, ordinary
  shoreline foam, and `ShoreFadeDistance`.
- The opaque scene point continues to participate in existing depth-tested object visibility. Above-water
  geometry remains an occluder. A submerged object is not used as a substitute bed.
- With no binding, retain the existing depth source and appearance. The unused feature must not change
  frame output or golden images for legacy callers, and performs no optical field preparation or uploads.
- FFT crest foam, existing shoaling and breaking surf retain their existing inputs. The new resource
  does not reinterpret or overwrite `PixelPostProcessSettings.Water.Bathymetry`.

The current renderer grades the water palette and then alpha-blends it over the lit scene. It does not
apply RGB absorption directly to the opaque scene colour. This design does not claim otherwise.
The early visual gate must establish that separating the bed input is sufficient for the requested
immersion. If it is not, stop before rollout and revise the design with the additional compositing
contract needed. Do not compensate by moving the duck, clipping its submerged geometry, tightening
banks, or blessing a failed image as a new golden.

Preserve raw NDC depth reconstruction, inverse projection, Vulkan Y handling, temporal jitter and
render-origin rebasing. New uniforms and resources require updated layout and binding tests. Preserve
contiguous D3D11 interpolant and vertex signatures. The current UBO size must be read from code and
layout tests during implementation, not copied from older documentation.

## Resource preparation and lifetime

Preparing a field uploads its immutable data once and returns a generation-checked handle. Drawing
only resolves that handle, packs its mapping and binds the prepared texture. It performs no sampling
of authored world data, texture allocation, upload or managed allocation.

Replacement creates and validates a new resource before publishing the new binding. Release follows
the renderer's existing in-flight retirement rules. A retired field cannot be overwritten or disposed
while a submitted frame may still read it. Device loss and recreation invalidate old handles and
reprepare the retained CPU payload through the same ownership path.

Account resident bytes as checked sample count times four, plus the resource's actual retained overhead.
Expose preparation/upload counts through the existing diagnostic style so tests can distinguish reuse
from repeated uploads. Stationary scenes and moving creatures cause no optical field uploads.

Cache ownership is bounded by resident water resources. Unloading a region or disposing its view releases
its references. Sharing must not leak a field or free it while another plane still owns it. A colour-only
look change does not rebuild bed samples. A changed displacement envelope must revalidate coverage.

## TileWorld producer

Build optical samples from the same authored plane-height semantics used by rendered terrain, including
the documented higher-plane fallback. Confirm source availability rather than treating an unloaded
region's default height as authored bed data.

Share a prepared field among water rectangles with the same resident region, plane, reference surface
level and height dependencies. Do not allocate a separate full grid for every row-run rectangle.
Different surface levels have distinct bindings. Sampling density follows the authored height lattice
and its interpolation, rather than the low-frequency FFT bathymetry resolution assumption.

The cache key includes the geometry generation and every height dependency used for boundary samples.
If neighbouring regions contribute samples, their revisions participate in invalidation. Adjacent fields
must reconstruct the same bed on their common boundary. Tests pin this under edits, unload and reload.

Preparation belongs to region preparation or flush, before ordinary frame submission. Extend the current
water cache coherently rather than putting resource creation in `DrawWaterPlanes`. Pruning and view
disposal release the corresponding fields. Legacy direct `Scene3D.DrawWater` consumers remain supported.

## Native water and R8a coordination

The inspected R8a plan reserves #1300 optics for R8b. Its canonical bounded-water resources, identity,
anchors, original-domain clipping and dependency digests remain authoritative.

The Render3D field contract must be reviewed against those seams before implementation is accepted.
A native producer selects the bed from the water body's own space and wet interval, rather than a
scene-wide XZ query or the aggregate `DeepestBedY`. It must charge retained data and carry its source
digest through preparation, admission and retirement.

No unpublished R8a types become dependencies of main's Render3D or TileWorld implementation. The
native adapter handoff records the exact new resource and coverage contract for its owning R8b work.
That handoff remains open until the native path actually prepares and submits the binding. This change
does not claim native optical acceptance from compilation-only MapDoc water tests.

## Verification and acceptance gates

First prove the shader behavior on a small public synthetic fixture with a real skinned depth-writing
object, a submerged rigid ledge and an authored sloping bank. Keep the old and new paths selectable
in the fixture only. Match all other rendering state. This establishes whether the proposed input
separation addresses the appearance before expanding adapters and public documentation.

Headless contracts cover signed samples, interpolation, precision over representative 2 m, 30 m and
100 m depths, invalid data, reference-level changes, stacked bindings, frame-origin rebasing and
coverage refusal. Lifecycle tests cover replacement, stale handles, scene ownership, reuse, release,
device recreation and no uploads or managed allocation during unchanged draws.

TileWorld tests cover higher planes, missing neighbour data, height edits, multiple surface levels,
shared row-run fields, unload, reload and boundary continuity.

GPU evidence must include these independent cases:

1. A sloping bank with a soft transition under the intended 0.20 m material feather.
2. A skinned object crossing a water surface, with the upper part correctly occluding water and the
   submerged part visibly affected by water. Moving it does not move the bed-derived colour or shore mask.
3. A submerged ledge, plus 2 m, 30 m and 100 m bed depths without false pale or stepped bands.
4. Two bounded bodies at the same XZ and different levels, including an enclosed synthetic space.
5. Adjacent field patches, camera-origin rebasing, temporal jitter, and the supported MSAA resolve path.
6. A legacy no-binding scene whose output remains unchanged, plus the existing FFT shoaling/surf cases.

Do not demand byte-identical bank pixels between different depth definitions on a slope. Require the
authored bank's continuous soft edge, stable world-space depth and no new hard cut, and inspect the
matched captures. Numerical tests alone cannot certify that appearance.

Run the appropriate headless tests and the full Release build and suite required by contributor rules.
Shader/resource changes require the branch's complete Metal, Direct3D 11 and Vulkan GPU dispatch before
integration. Bake changed goldens on the matching hosted backend and inspect every changed evidence
image. Never accept a self-baked local golden as proof. These are normal verification runs, not stress
runs. No stress or repeated-until-green run is authorized by this spec.

The private consumer gate repeats the existing duck and bank captures with Native TAA. The approved
duck shape, motion, waterline, camera and material stay fixed. The duck must read as immersed and the
bank must remain acceptable. Private game assets and screenshots stay out of the public engine repo.

## Delivery and completion

Write an implementation plan only after the owner approves this spec. The plan begins with the small
optical proof and then covers resource ownership, the TileWorld producer, backend verification and
consumer evidence. Preserve the failed controls and their metrics.

Update the Render3D and TileWorld package references, consumer documentation and pipeline descriptions
for the shipped contract. Keep this document as rationale. Sweep Markdown for the previous claim that
all water depth resources are scene-wide. Follow the engine's version and pack rules when code lands.

A private pre-merge package proof may validate integration without changing a game's shipping pin.
Grimhollow adopts only a released engine version and performs its required package refresh, navigation
rebake and final verification. Release authorization remains separate. A game-rooted session does not
create the engine tag.

Completion requires the accepted optical images, cross-backend evidence, resource/lifetime tests,
documented native handoff and the released consumer adoption. The earlier unrelated Grimhollow
playtest-handshake failure remains a separately tracked verification issue, not a reason to weaken this
feature's tests or to claim its game integration is green.
