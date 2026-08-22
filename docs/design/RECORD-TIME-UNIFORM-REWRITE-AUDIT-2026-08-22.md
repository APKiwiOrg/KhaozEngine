# Record-time uniform rewrite audit (2026-08-22)

Issue: [#483](https://github.com/APKiwiOrg/KhaozEngine/issues/483). Written against engine `17.39.0`.

Sweep every record-time `IGpuCommandList.UpdateBuffer` to a UNIFORM buffer in the engine and answer one
question per site: can a frame write the same byte range twice with a draw recorded in between? That shape
renders differently on the engine's own native backends than on the Veldrid ones, silently.

## 1. What the hazard actually is, measured

On `Direct3D11Native`, `VulkanNative` and `MetalNative` a record-time `UpdateBuffer` to a uniform buffer is a
memcpy into that frame's own ring segment (`D3D11UniformRing.Write`, `VulkanUniformRing.Write`,
`MetalUniformRing.Write`). It records no command, so it is not ordered against the draws in the same list. One
segment holds one copy of those bytes for the whole frame, so the LAST write decides every byte and a draw
recorded between two writes reads the SECOND one.

That was a claim about three implementations, taken from their own doc comments, and #483 exists precisely
because the same claim was made about a fourth thing (Veldrid's Direct3D 11 leg) where it is false. So it is
measured rather than believed: `KhaozEngine.Render.Tests/Gpu/RecordTimeUniformRewriteGpuTests.cs` writes A,
draws into one target, rewrites the same sixteen bytes with B, draws into a second target and reads one texel
out of each.

| backend | first draw | second draw | reading |
|---|---|---|---|
| `MetalNative` | blue | blue | COLLAPSED: both draws read the last write |
| `Metal` (Veldrid) | red | blue | ORDERED: each draw read the value written before it |

The control matters as much as the assertion. It says the two families really do disagree, so a hazard is a
divergence between backends rather than a bug that would show up everywhere, which is why no committed golden
could have caught the one found below: the golden families are baked on the incumbents.

Four conditions have to hold at once for a site to be a hazard:

1. the buffer is a UNIFORM buffer (the only usage any of the three rings backs, see `MetalBufferPolicy.IsRingBacked`
   and its two siblings), so a vertex, index or instance stream re-streamed between draws is not one,
2. a DRAW or dispatch is recorded between the two writes,
3. one of those intervening draws BOUND a window of that buffer, and
4. the two writes disagree on a byte inside that window. Rewriting bytes nothing already recorded can read is a
   no-op, which is what makes the engine's whole-mirror-per-slot upload pattern safe.

Conditions 3 and 4 are one idea and are applied together, over the bound window rather than over the whole
overlapping range. Section 4 has the reason, which is that the looser form calls the engine's own sanctioned
pattern a collapse.

## 2. The site table

Every uniform buffer created in `KhaozEngine.Render2D` and `KhaozEngine.Render3D`, which between them are the
only packages that create one at all (`Gui`, `Particles.Render3D`, `Terrain`/`TileWorld.Render3D`, `MapEditor`
and the samples draw through those two and declare no layout and no uniform buffer of their own).

| buffer | range written | writes per frame | draw between | verdict |
|---|---|---|---|---|
| `ModelRenderer._ubo` (1008 B) | whole, offset 0 | 1 (`SetFrameUniforms`, `Scene3D.cs:1844`) | no | safe: once per frame |
| `ModelRenderer` splat combined UBO, per material | whole, offset 0 | 1 per material (`DrawSplatRuns`) | no | safe: one buffer per material, each written once ahead of its draws |
| `ModelRenderer` tile-ground combined UBO, per material | whole, offset 0 | 1 per material (`DrawTileGroundRuns`) | no | safe: same shape as splat |
| `ModelRenderer._skinnedMainUbo` (8 x 9472 B) | whole, offset 0 | 1 (`UploadSkinnedMainSlots`) | no | safe: per-draw slots, one upload after every slot is packed |
| `ShadowMapRenderer._lightUbo` (4 x 256 B) | whole, offset 0 | 1 (`BeginDepthPass`) | no | safe: per-cascade slots + dynamic offset |
| `ShadowMapRenderer._skinnedUbo` (8 x 8448 B) | whole, offset 0 | 1 (`UploadSkinnedShadowSlots`) | no | safe: per (cascade, caster) slots + dynamic offset |
| `WaterRenderer._ubo` (4 x 768 B) | whole, offset 0 | 1 (`UploadSlots`) | no | safe: per-plane slots + dynamic offset, packed then uploaded (17.20.0) |
| `OverlayMeshRenderer._ubo` (8 x 256 B) | whole, offset 0 | 1 (`Flush`) | no | safe: per-draw slots + dynamic offset (17.18.0) |
| `OceanFftProducer._ubo` (80 B) | whole, offset 0 | 1 per command list | no | safe: one block serves both dispatches, and the priming pass carries its own on its own drained list |
| `PixelPostProcess` x10 (palette, edge, final, fxaa, bright, blurH, blurV, composite, tone, apply) | whole, offset 0 | at most 1 each | no | safe: every write is in `PrepareUniforms`, which runs once before the first `SetFramebuffer` |
| `SkyRenderer._ubo` | whole, offset 0 | 1 | no | safe: `Draw` runs once, and the sky/starfield switch takes at most one arm |
| `StarfieldRenderer._ubo` | whole, offset 0 | 1 | no | safe: as sky |
| `BeamRenderer._ubo` (80 B) | whole, offset 0 | 1 (`SetFrameUniforms`, before `Draw`) | no | safe |
| `TrailRenderer._ubo` (64 B) | whole, offset 0 | 1 (`SetFrameUniforms`) | no | safe: two draws follow it (additive then alpha) and neither writes |
| `TexturedBillboardRenderer._ubo` (64 B) | whole, offset 0 | 1 (`SetViewProj`, before the run loop) | no | safe |
| `DepthLineRenderer._ubo` (64 B) | whole, offset 0 | 1 | no | safe |
| `DistortionRenderer._frameUbo` | whole, offset 0 | 1 | no | safe |
| `ParticleRenderer._frameUbo` (192 B) | whole, offset 0 | 1 | no | safe |
| `TransitionRenderer._solidBuf` / `._crossBuf` (16 B) | whole, offset 0 | at most 1 (one arm of an if) | no | safe |
| `OverlayRenderer<T>._ubo` (64 B) as `LineRenderer` | whole, offset 0 | 1 | no | safe |
| `OverlayRenderer<T>._ubo` (64 B) as `FillRenderer` | whole, offset 0 | 1 | no | safe |
| `OverlayRenderer<T>._ubo` (64 B) as `BillboardRenderer` | whole, offset 0 | 2 (additive then alpha) | YES | safe by condition 4: both writes are `GpuClip.Correct(vp, caps)` from the same frame's `vp`, so the second write is byte-identical to the first |
| `SpriteBatch._vpUbo` (8 x 256 B) | whole, offset 0 | 1 per `Begin` | YES | safe by condition 4: a slot's mirror value never changes once its `Begin` claimed it, so every re-upload restates the bytes already recorded against (this is the pattern #408 proved) |
| `GroundDecalRenderer._frameUbo` (80 B, pre-17.39.0) | whole, offset 0 | 2 (blob-shadow pass, main pass) | YES | **HAZARD**, see section 3 |

Device-level `IGpuDevice.UpdateBuffer` sites are out of scope here and stay so: that call reaches every ring
segment and leaves a pending patch on any the GPU has not finished with, so a value written once persists for the
buffer's life. The neighbouring case of one landing mid-command-stream is #415, closed. The shipped device-level
sites are all load-time or bake-time (`ModelRenderer.cs:495`, `ModelRenderer.TileGround.cs:77`,
`OceanFftProducer.cs:446` and `:492`, `WaterRenderer.cs:303`, `Scene3D.cs:546`, `:566`, `:572`, `:781`) or write
vertex buffers (`SpriteBatch.cs:688`, `:716`), none of them ring-backed.

## 3. The one hazard, and why the fix is slots

`Scene3D` runs `GroundDecalRenderer.Draw` twice in one frame and the two runs disagree about one lane:

- `Scene3D.cs:1903`, the blob-shadow pass. Runs early, before the skinned draws, after a depth-only resolve. It
  must NOT reject dynamic-tagged pixels, because the normal target the reject reads is not yet valid there.
- `Scene3D.cs:2020`, the main decal pass. Runs after the depth+normal resolve. It MUST reject, so a ground decal
  never paints onto a character (#235).

Both wrote the same 80 bytes at offset 0, with the blob pass's draws recorded between the two writes and with
`TimeQ.w` differing. On the natives the second write decided both, so the blob pass ran with the reject on
against an invalid normal target. On the incumbents it was correct, which is why the goldens are silent.

**The fix.** The buffer is two 256-byte slots. Each pass packs its own slot into a CPU mirror, uploads the mirror
WHOLE, and binds its slot with a dynamic offset. The pass is an explicit `FramePass` enum rather than the old
`bool rejectDynamicGeometry`, so the slot and the reject are one decision and cannot drift.

**Alternatives weighed.**

- *Move the reject into the per-instance attribute stream.* Also removes the rewrite, and is arguably where the
  flag belongs, since it is a property of the decal set rather than of the frame. Declined: it changes a shader,
  the `DecalInstance` struct and the bytes every golden reads, for a correctness fix that does not need any of
  that. The slot version is pixel-identical on every backend that was already ordered.
- *Two `GroundDecalRenderer` instances, one per pass.* Doubles four pipelines and a shader set for two passes
  that differ in one float.
- *Reorder so both writes precede all decal draws.* Impossible: the two passes are separated by the whole
  skinned and sky half of the frame by design.

**Why the whole-mirror upload does not reintroduce the problem.** Each pass's upload restates the other pass's
slot with the bytes it already holds, and its own slot is read by no draw recorded before it, which is
conditions 3 and 4 and is the same argument `SpriteBatch` has carried
since #408. Keeping it whole is what keeps the write off Veldrid's blocking partial-uniform-write staging route
on Direct3D 11.

**Golden classification.** Zero movement expected or observed on the incumbent families: the same 80 bytes are
read, from a different offset in a bigger buffer. On the natives this is a CORRECTION, in a configuration
(Blob shadows plus queued ground decals in one frame) that no committed golden covers. Nothing was rebaked.

## 4. The guard, which is the durable half

A written table nobody re-derives is worth less than a check that fails. Four pieces, all in
`KhaozEngine.Render.Tests`:

- `RecordingGpuCommandList` stamps each recorded upload with how many draws and dispatches preceded it, and
  each draw with the uniform WINDOWS the resource sets bound at that moment cover (its `Reads.cs` half). The
  first turns a pair of uploads into an ordering fact. The second is what says whether any draw could have
  read the bytes the second upload changed.
- `UniformWindowIndex` + `UniformBufferTrackingGpuDevice` supply the two facts the handles do not carry:
  whether a buffer was created with the uniform bit, and which byte range of it each resource set binds
  (including whether that range is rebased per draw by a dynamic offset). Neither can be read back off an
  `IGpuBuffer` or an `IGpuResourceSet`, so they are remembered at the factory.
- `UniformRewriteAudit.Scan` applies the four conditions of section 1 to one frame and returns the offending
  pairs, each naming the buffer, the bytes that differ, the window a draw in between bound, and how many
  draws sit between the two writes.

### The window rule, and why comparing the whole overlap was wrong

The first cut of this guard compared the WHOLE overlapping range of the two writes. That is a false positive
on shipped, correct code. The sanctioned engine pattern is to pack your own slot of a CPU mirror and upload
the mirror WHOLE, so two passes' uploads DO differ, in the slot the other pass owns. `GroundDecalRenderer`
after section 3's fix has exactly that shape, and so does `SpriteBatch.ViewProj.cs:86`. The old rule only
stayed green because the guard's frame was bit-identical from frame to frame: advance
`Scene3D.EffectTimeSeconds` the way any host does and it goes red on the fix, reporting

```
a 512-byte uniform buffer had [0, 512) written twice with 7 draw(s) recorded between the two writes, and the bytes differ
```

where the differing bytes are in slot 1, `[256, 512)`, which no draw recorded before the second write ever
binds.

So the rule is stated over the bytes a draw could actually have observed:

> Two uploads to one uniform buffer are a hazard when a draw or dispatch recorded BETWEEN them bound a window
> of that buffer, and the two uploads disagree on a byte inside THAT WINDOW. A rewrite whose differing bytes
> fall outside every window bound in between is not a hazard.

A window is the `GpuBufferRange` the set binds, plus the per-draw dynamic offset when the layout element is
`Dynamic`. A draw is "between" two uploads when its ordinal is at least the count of draws recorded before
the first and below the count recorded before the second. The index over-reports in two directions on
purpose, since over-reporting can only turn a safe rewrite into a reported hazard and never hide one: a set
stays bound at its slot until that slot is bound again (so a stale set a later pipeline does not read still
counts), and a set built against a layout the index never saw contributes every uniform buffer it binds at
full extent (and bumps `UnresolvedResourceSets`, which the guard asserts is zero).

`UniformRewriteAuditTests` pins both verdicts with no device at all, which is what makes them safe to trust:
one recording of the two-pass whole-mirror shape, run twice, differing only in which slot the second pass
packs. Packing its own slot is empty. Packing the FIRST pass's slot is one hazard naming the window
`[0, 80)`.

### The frames

`UniformRewriteGuardGpuTests` renders TWO deliberately greedy configurations through the real renderers, with
every queue that gates a pass filled, and asserts the finding list is empty for each. It checks its own
inputs first (uniform buffers were recognised, draws were recorded, more than ten uniform uploads happened,
bound windows were recorded, and at least one buffer that the frame REWROTE was also bound by a draw) so an
empty answer cannot come from an empty frame or from a window rule with nothing to compare against.

The first configuration is blob shadows over a sky, which is the pair section 3's hazard lived in. The second
is the shadow-map tier over a starfield with GPU skinning on, which is the only way to reach the cascade light
UBO, both skinned per-draw slot buffers and the starfield UBO at all. Reverting the section-3 fix turns the
guard red with

```
a 512-byte uniform buffer had [0, 80) written twice with 8 draw(s) recorded between the two writes, the bytes
differ, and a draw in between bound the window [0, 80) of it
```

so it catches the hazard it was written for rather than only watching for a hypothetical one.

**What it does NOT reach**, measured rather than glossed. Across both configurations the guard writes 28 of
the 30 uniform buffers a `Scene3D` creates (the test prints both numbers). The two it never writes are
`TransitionRenderer._solidBuf` and `._crossBuf`, 16 bytes each, because no `Scene3D.ScreenTransition` is
assigned and each is one arm of an if. Outside that count entirely is everything a Render3D scene never
creates: all of `KhaozEngine.Render2D`, `SpriteBatch._vpUbo` included, which carries the same whole-mirror
per-slot shape and is covered by section 2's table and by the device-free audit tests rather than by a frame.
A NEW pass added later is outside the guard until its queue is added to `QueueEverything`. That is the
standing maintenance cost, and it is the reason section 2's table is kept as well as the guard.

### The one row that looks at the render

`BlobDecalGhostHoleGoldenTests` is the picture. Everything above is a timeline, and a timeline cannot say the
corrected frame looks right. It renders a character standing in a blob, then a second frame with the
character gone, and asserts the floor the character HID is blob-covered rather than punched out.

Three things about it were established by probing rather than reasoning, and each one is why the obvious
version of this test cannot fail:

- **MSAA has to be on.** Without it `NormalTex` IS the model pass's own attachment, written THIS frame, so
  the blob pass reads current tags and the reject never fires whatever the ring did.
- **The FIRST frame is not the case to assert.** An unresolved normal target holds whatever the driver left
  in it, and on Metal that reads opaque, so a frame-one collapse discards nothing. The damage that survives a
  probe is the ghost hole on the frame AFTER a character moved, not a missing blob on frame one.
- **The sample point has to be floor BEHIND the character, not under it.** A character's own footprint is a
  small part of its silhouette, and the interior of an open mesh shows floor straight through, so the hole
  from a squat open cylinder is a thin ring that a centred sample sits inside and misses entirely.

With the section-3 fix reverted on `MetalNative` it reads bare floor (210) where blob-covered floor beside it
reads 165, and it is green on the Veldrid incumbent either way, which is the divergence section 1 measures,
seen in a picture.

## 5. What consumers were told

`docs/USING-KHAOZENGINE.md` already stated the rule for `Direct3D11Native` and `VulkanNative` ("a uniform write
lands when you make it, not when the list is submitted"). The Metal section carried the ring's creation refusal
but not its ordering rule, so 17.39.0 adds the same paragraph there. Nothing about the rule changed: it is now
stated for all three of the backends it holds on, and measured on the one that can be measured locally.
