# Per-plane water look

Design for [#370](https://github.com/APKiwiOrg/KhaozEngine/issues/370). Consumer origin is
[Ruinborne#293](https://github.com/APKiwiOrg/Ruinborne/issues/293).

## The gap

`WaterPlane` is the per-frame WHERE-to-draw request and `WaterSettings` on `Post` is the look.
`Scene3D.DrawWater` has always accepted several planes, and the geometry half of that works: separate
lakes and ponds queue one plane each and each draws at its own surface height. The look half does not.
`WaterRenderer.Draw` takes ONE `WaterSettings`, so every queued plane in a frame shares one wave
source, one sea state, one bathymetry binding and one foam configuration.

The consequence is that a game cannot have a calm inland lake and a rough sea at the same time. Queue
a lake plane next to an FftOcean sea and the lake gets the ocean's FFT swell, its whitecaps and its
17.3.0 breaking surf. It reads as a patch of sea that happens to be inland.

## What the architecture already gives us

The important finding, and the one that decided the shape: almost none of this needs new machinery.

`WaterRenderer.Draw` already packs the uniform buffer **once per plane**, not once per frame:

```csharp
for (int i = 0; i < planes.Length; i++)
{
    var u = PackUbo(clipVp, viewProj, lightDirection, lightColor, cameraPos, settings, sky, timeSeconds,
        oceanMaps, renderOrigin, shore, planes[i].SurfaceY);
    cl.UpdateBuffer(_ubo!, (uint)i * SlotBytes, in u);
}
```

Each plane already owns a 768-byte dynamic-offset slot in the shared UBO, and `PackUbo` already takes
`settings` as an argument and `planes[i].SurfaceY` as a per-plane value (17.3.0 threaded the surface
height through so the surf band could measure crest height above *this* plane's still water). Handing
a different settings object to that call is the whole feature.

Two more facts make the expensive-looking parts free:

- **The FFT is a uniform branch, not a pipeline.** `FftParams.x = ocean.Active ? 1f : 0f`, and every
  FFT branch in both shader stages is gated on it. Packing an inactive `OceanMaps` for one plane makes
  that plane procedural, with no pipeline switch and no second shader.
- **Bathymetry rides the same gate.** `shoreLive = shore.Active && ocean.Active`, so a plane whose
  effective wave source is not FftOcean loses shoaling and breaking surf automatically. The lake gets
  "no surf" for free rather than needing its own flag.

So the per-plane look costs **zero new UBO bytes** (the payload stays 672, the slot stays 768), zero
new GPU resources, and zero new pipelines. That is why this ships as a minor rather than as a program
with its own buffer layout.

## The cut

A `WaterSettings` field is overridable per plane when overriding it is a pure change to what `PackUbo`
writes into that plane's slot. It stays scene-wide when it drives a GPU resource that is produced once
per frame, or when it selects the pass's geometry or pipeline.

**Per plane**, 33 fields in seven groups:

- Wave source: `WaveSource`
- Swell: `SwellAmplitude`, `SwellWavelength`, `SwellDirectionDegrees`, `SwellSpreadDegrees`,
  `SwellSteepness`, `SwellSpeed`, `SwellSeed`, `SwellComponents`
- Ripple and detail: `WaveScale`, `WaveSpeed`, `NormalStrength`, `WaveWarpStrength`, `RippleComponents`,
  `RippleLacunarity`, `RippleGain`, `RippleSeed`, `DetailFadeDistance`, `DistantDetailScale`,
  `VarianceToRoughness`
- Body colour: `DeepColor`, `ShallowColor`, `AbsorptionPerMetre`, `ShallowDepth`, `Opacity`
- Foam: `FoamColor`, `FoamStrength`, `FoamCrestCoverage`, `FoamShoreWidth`, `FoamPatternScale`
- Shore: `ShoreFadeDistance`
- Depth response strength: `ShoalingStrength`, `SurfStrength`

**Scene-wide, and why:**

| Stays scene-wide | Why |
|---|---|
| `SeaState` | It is the FFT bake key. See the next section. |
| `Bathymetry` | One `WaterBathymetryMap` texture, uploaded once per frame on a revision change. A second field is a second texture and a second binding. |
| `GridMode`, `ClipmapCellSize`, `ClipmapRingCells`, `ClipmapLevels`, `ClipmapGeomorphBand`, `GridFocusBias` | These select the pass's pipeline, index buffer and vertex layout, chosen once before the draw loop. Per-plane grid mode is a geometry change, not a look change. |
| `HorizonColor`, `SkyReflectionStrength`, `SkyReflectionSunStrength`, `GlintStrength`, `GlintRoughness`, `GlintDistantRoughness`, `GlintExponent` | Reflection and glint read the one sky and the one sun. #370 puts them out of scope explicitly. They are cheap UBO scalars, so this is a scope decision and not a structural one: a later release can move the blend weights per-plane without touching anything here. |
| `SurfBreakerIndex`, `SurfBandWidth`, `SurfCrestBias`, `SurfTrailWidth`, `SurfAmplitudeCollapse`, `ShoalingDepthScale` | Shape knobs for *how* surf breaks. A plane that wants no surf sets `SurfStrength = 0`, which is the whole per-body need. Making the shape per-body as well is surface with no caller. |
| `FootprintSamples`, `ClipmapBandLimitSamples` | Sample counts. Quality, not look. |

## Why a per-plane sea state is refused rather than deferred

#370 lists `SeaState` as a candidate and notes that refusing is acceptable. It is refused, and the
reason is worth writing down because the naive implementation looks like it would work.

There is one `OceanFftProducer`, one `_h0`/`_work`/`_foam` storage set and one cascade map array.
`Update` derives a `Bake` key from the sea state's spectrum-shaping fields and rebakes when it differs
from `_baked`. Calling `Update` twice per frame with two different sea states does not produce two
oceans. It produces one ocean that rebakes on **every** call for **both** states, because `_baked` is a
single field and each call finds the other's key: a CPU spectrum bake plus a buffer upload every frame,
and a full `ReleaseFftResources()` and texture rebuild whenever the resolution or cascade count differ.
It also corrupts foam, which is a persistent per-texel accumulator whose contract is that one
invocation owns each texel.

So per-body sea states are not a knob that was skipped. They are a second producer instance, a second
map array, a second binding and a second compute stall, which is a program of its own and belongs with
[#275](https://github.com/APKiwiOrg/KhaozEngine/issues/275) (authored water volumes) rather than here.

What a plane CAN do is opt out of the shared ocean entirely, to `Procedural`. That is the case every
consumer asking for this actually has: one sea, and inland bodies that should not be sea.

## Shape

`WaterLook` is a sealed class in `KhaozEngine.Render3D`, every field nullable, `null` meaning "inherit
the scene". `WaterPlane` gains a `Look` property, supplied through a trailing optional constructor
parameter so every existing call site compiles unchanged.

```csharp
scene.DrawWater(new WaterPlane(0f, 9.7f, 0f, 104f, 104f, new WaterLook
{
    WaveSource = WaterWaveSource.Procedural,
    SwellAmplitude = 0.04f,
    FoamStrength = 0f,
    SurfStrength = 0f,
}));
```

**Partial override, not group replacement.** The alternative was for `WaterLook` to carry whole groups
(a complete swell block, a complete foam block) that replace the scene's. It was rejected because the
common case is one or two fields differing from a scene look the consumer has already tuned, and group
replacement forces them to restate twenty values to change one, then keep both copies in sync forever.
Nullable per field also makes every future addition to the overridable set purely additive.

**Resolution is a scratch copy, not an inline coalesce.** The renderer keeps one reusable
`WaterSettings` scratch. Per plane:

```csharp
WaterSettings effective = plane.Look is null ? settings : plane.Look.ResolveInto(_effective, settings);
```

`ResolveInto` copies the scene settings field-wise into the scratch (`WaterSettings.CopyFrom`, in a
`WaterSettings.Copy.cs` partial to keep the main file well under the size cap) and then writes the
non-null overrides over the top. `SeaState` and `Bathymetry` are copied **by reference** on purpose:
the scratch points at the same scene-wide objects, so there is no way for a look to fork them by
accident.

The rejected alternative was to read `look?.Foo ?? settings.Foo` per field inside `PackUbo`. It reads
smaller, but it has two real costs. It puts the overridable field list inside `PackUbo`, where omitting
one silently means "this knob is not overridable" with nothing to catch it. And it forks the no-look
path: today's single-plane consumers would go through 33 null-coalesces instead of the same code they
run now. With the scratch copy, `plane.Look is null` passes the caller's own `settings` object straight
through to an unchanged `PackUbo`, so **byte-identity for consumers that do not opt in is structural
rather than something a golden has to catch.**

## The one behavioural fix this forces

`OceanFftProducer.Update` gates its own activity on `settings.WaveSource != WaterWaveSource.FftOcean`.
That is wrong once the wave source can be per plane: a scene whose default is `Procedural` with one
plane overriding to `FftOcean` would find the producer inactive, pack `FftParams.x = 0`, and silently
render procedural water. Silent, and exactly the failure mode that is hardest to spot.

So `Draw` computes the effective wave source over the queued planes first, passes "any plane wants the
ocean" into `Update`, and then packs each plane's slot with either the live `OceanMaps` or `default`
according to that plane's own effective source. One ocean either way, driven by demand rather than by
the scene default.

## Testing

- **Byte-identity, headless.** `PackUbo` with a null look and `PackUbo` with the scene settings produce
  the identical `WaterUbo`. Structural, but assert it so a later refactor cannot quietly fork the path.
- **Override and inherit, headless, field by field.** For every overridable field: setting it changes
  the packed slot, and leaving it null leaves the packed slot equal to the scene's. This is the test
  that keeps the resolver honest as fields are added.
- **Scene-wide fields are not forkable.** A look cannot change `SeaState` or `Bathymetry` identity.
- **UBO layout unchanged.** The existing `UboLayoutTests` water assertions (`PayloadBytes == 2*64 +
  34*16`, slot alignment, GLSL block parity) must pass untouched. If any of them needs editing, the
  implementation has gone wrong.
- **Demand-driven ocean.** A scene defaulting to `Procedural` with one `FftOcean` plane activates the
  producer. A scene defaulting to `FftOcean` with every plane overridden to `Procedural` does not.
- **`scene3d_water` golden passes unbaked.** It queues one plane with no look. If it needs a rebake,
  the byte-identity guarantee is broken.
- **A new two-plane golden** (name containing `Golden` so `cross-platform-gpu.yml` picks it up on all
  three backends), rough FFT sea beside a still plane, which is the thing #370 asked for and the only
  test that can show it actually looks different.

## Deferred

- Per-body sea states, bathymetry fields and grid modes: [#275](https://github.com/APKiwiOrg/KhaozEngine/issues/275).
- Per-plane reflection and glint weights: cheap, out of #370's scope, no caller yet.
- Nothing here gives gameplay a second water body. Submersion, the scatter guard and nav water blocking
  all still read one document water level. That is #275's half, and a consumer wiring a lake today does
  it in its own medium provider.

## Later extension: per-plane sun response (#372)

The original 33-field cut kept glint scene-wide as a scope choice. The look now has 37 fields after
adding `GlintStrength`, `GlintRoughness`, `GlintDistantRoughness` and `GlintExponent`. The sun direction
and colour remain scene-wide. Only the existing per-plane uniform scalars change, with no resource
or pipeline changes. Reflection weights remain outside the look.

`OceanPresets.ApplyToLook(kind, WaterLook)` can therefore apply the complete existing weather bundle. It
leaves `WaveSource` unchanged so a preset never silently switches a plane between FFT and procedural
water. It also retains unrelated shore and appearance overrides. The original scope table above
records the initial design, the package README is the current API reference.
