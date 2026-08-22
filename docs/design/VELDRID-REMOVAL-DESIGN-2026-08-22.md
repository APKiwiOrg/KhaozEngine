# The Veldrid removal: shader toolchain, owner golden families, and the delete

Design for MF1, the closing act named in section 19 of
[METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md](METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md). Phase 4 of the staged
native GPU backend program ([#420](https://github.com/APKiwiOrg/KhaozEngine/issues/420)) built the last native
backend and deliberately removed nothing. This document plans the removal itself.

Written against engine `17.39.0`, with the default flip to the native backends in flight separately. That flip
is step A and this is step B. Everything below assumes the flip has landed and soaked.

---

## 1. Goal, and what this is not

**Goal: no Veldrid anywhere in the graph.** No `Veldrid`, no `Veldrid.SPIRV`, no vendored fork, no fork feed, no
`Newtonsoft.Json` CVE override, no incumbent CI leg, and no shader toolchain the engine does not own the choice
of. `#420`'s written endpoint is "no Veldrid in the graph, three engine-owned backends", and this is the change
that reaches it.

**Non-goals, each of which has been mistaken for part of this work at least once already.**

- **Not the default flip.** Step A moves the three OS defaults onto the native backends and takes its own soak.
  This document starts the morning after.
- **Not removing the four Veldrid `GpuBackendKind` members.** They stay in the enum forever, as named
  exceptions. Section 5.2.
- **Not lifting the one-uniform-buffer invariant ([#604](https://github.com/APKiwiOrg/KhaozEngine/issues/604)),
  not specifying N-concurrent recording ([#613](https://github.com/APKiwiOrg/KhaozEngine/issues/613)), and not
  changing rule 2's drain ([#461](https://github.com/APKiwiOrg/KhaozEngine/issues/461)).** All three are
  *unblocked* by this change and none is *done* by it. Folding any of them in would put a rendering change
  inside the release whose whole job is to prove that removing an unused implementation changed nothing.
  Section 6.
- **Not a seam simplification.** The seam's shape is what made a third backend possible. The argument for
  narrowing it now that every backend is ours is the same argument that would have made phase 3 harder.
  METAL-NATIVE section 19 already refused this and the refusal stands.

---

## 2. The shader toolchain, which is the actual project

Deleting the incumbent backend is 1150 lines of mechanical work. The toolchain is the part with a decision in
it.

### 2.1 What the natives take from Veldrid today

All three native backends reach glslang and SPIRV-Cross through `Veldrid.SPIRV`, from one seat inside
`KhaozEngine.Gpu`, and that seat is deliberate: decisions P2, V-P3 and M-P3 all say a backend package must
declare no Veldrid package of its own, so the edge lives in `KhaozEngine.Gpu` and the backends consume
Veldrid-free signatures across `InternalsVisibleTo`. Four files hold the whole edge.

| File | What it takes from `Veldrid.SPIRV` |
|---|---|
| `KhaozEngine.Gpu/Internal/SpirvFrontEnd.cs` | `SpirvCompilation.CompileGlslToSpirv` under `GlslCompileOptions`. GLSL 450 in, SPIR-V out. |
| `KhaozEngine.Gpu/Internal/SpirvCrossCompile.cs` | `CompileVertexFragment` / `CompileCompute` to `CrossCompileTarget.HLSL` and `.MSL`, plus the `SpirvReflection` it reads back in `Reflect`. |
| `KhaozEngine.Gpu/ShaderValidation.cs` | The same two members across four targets (HLSL, MSL, GLSL, ESSL) with no device in existence. |
| `KhaozEngine.Gpu/Internal/SpirvToolchainVersion.cs` | `typeof(SpirvCompilation).Assembly`, for the shader-cache key ([#610](https://github.com/APKiwiOrg/KhaozEngine/issues/610)). Identity only. |

The Veldrid-free halves survive a swap untouched, and that is phase 3's front-end split paying off exactly as
its header said it would: `SpirvCompileCache`, `SpirvLocalSize`, `ShaderCrossCompileResult`, `MslBindingOrder`
and all three option pins hold their values as Veldrid-free constants and name no Veldrid type.

**What a swap must therefore supply: GLSL to SPIR-V, SPIR-V to MSL and HLSL, reflection, and a settable
resource-binding table.** The last of those is the one
[#462](https://github.com/APKiwiOrg/KhaozEngine/issues/462) says cannot be reached from where we stand:
`libveldrid-spirv` exports three C entry points and not one carries a binding table, so
`add_msl_resource_binding` is a C++ symbol on an object `CrossCompile` never hands back. A P/Invoke shim over
that library gets precisely what the managed wrapper already gets.

### 2.2 The candidates, checked rather than assumed

Everything in this section was read off nuget.org and then run. The packages, their versions and their RIDs:

| Package | Newest version | License | Ships |
|---|---|---|---|
| `Silk.NET.Shaderc` | 2.23.0 | MIT | `netstandard2.0`, `netstandard2.1`, `netcoreapp3.1`, `net5.0` |
| `Silk.NET.Shaderc.Native` | 2.23.0 | Apache-2.0 (google/shaderc) | linux-arm, linux-arm64, linux-x64, osx-arm64, osx-x64, win-arm64, win-x64, win-x86 |
| `Silk.NET.SPIRV.Cross` | 2.23.0 | MIT | same four TFMs |
| `Silk.NET.SPIRV.Cross.Native` | 2.23.0 | Apache-2.0 (KhronosGroup/SPIRV-Cross) | the same eight RIDs |
| `Silk.NET.SPIRV` | 2.23.0 | MIT | the SPIR-V enums (`Decoration`, `ExecutionModel`) both of the above join on |

`2.23.0` is the exact Silk.NET line `Directory.Packages.props` already pins for windowing, input, OpenAL and the
Vulkan binding, and the comment at the top of that file exists to enforce that lockstep. These packages ride it
for free. Their native binaries are dated 2026-01-22.

Set against what they replace: `Veldrid.SPIRV` 1.0.15 was published on **2022-06-03** and ships
`runtimes/{win-x86, win-x64, linux-x64, osx}` plus three mobile build folders. No `linux-arm64`, no `win-arm64`,
and `osx` in the legacy undifferentiated form. That is the package `AGENTS.md` cites as the reason CI is pinned
to x64. **The Silk.NET natives cover every RID the fleet uses and two the incumbent cannot**, so the swap
removes the x64 pin rather than inheriting it.

### 2.3 The measurements

Four GLSL sources were compiled through both toolchains on an Apple M2 Max: the shipped `StarfieldVert` and
`StarfieldFrag` verbatim out of `ShaderSources.Sky.cs`, plus a vertex and fragment pair carrying four vertex
inputs, two uniform buffers across two descriptor sets, a separate texture and a separate sampler, which is the
shape `SpirvCrossCompile.Reflect` has to model.

**Result 1: the binding table is reachable, and it is authored rather than read.**

```text
[vertex] CompilerMslAddResourceBinding(set=1,binding=0 -> buffer(7)) => Success
[vertex] MSL emitted, 797 chars
  | vertex main0_out main0(main0_in in [[stage_in]], constant Camera& _48 [[buffer(0)]],
    constant PerObject& _22 [[buffer(7)]])
```

The deliberately non-default index 7 was honoured and the untabled `Camera` at set 0 kept `[[buffer(0)]]`. That
is `spvc_compiler_msl_add_resource_binding`, the seat #462 says does not exist at the library, reached through
`Silk.NET.SPIRV.Cross`. **This is the finding that closes M-B1**: with the table authored, the native Metal
backend stops parsing indices out of the emitted MSL and stops walking SPIR-V decorations to join them, which is
what 2.2b built and what [#586](https://github.com/APKiwiOrg/KhaozEngine/issues/586) named #462 as the trigger
for.

**Result 2: reflection is fully reachable, with one trap.** Uniform buffers, separate images, separate samplers
and stage inputs all enumerate with their `DescriptorSet`, `Binding` and `Location` decorations, and each stage
input's `Basetype` (`FP32`) plus `VectorSize` (2, 3, 4) gives `GpuVertexElementFormat` directly. Every field
`Reflect` needs is there. The trap is that **enumeration order is not declaration order**: the probe got
`PerObject` before `Camera`, and `Uv, Color, Normal, Position` for inputs declared `Position, Normal, Uv,
Color`. A port that trusts the order produces silently permuted resource layouts, which is the exact class of
defect this area has already produced three times (7.25.0's albedo swap, 7.51.2's normal-and-depth swap, the
splat terrain's second UBO). **The reimplemented reflection sorts by set then binding, and by location, and a
test pins that against a source whose declaration order differs from both.**

**Result 3: the emitted SPIR-V moves, and the option set that matters was undocumented.**

| Source | `Veldrid.SPIRV` 1.0.15 | shaderc 2.23.0, `OptimizationLevel.Zero` | shaderc 2.23.0, `Performance` |
|---|---|---|---|
| `StarfieldVert` | 836 B | 1192 B | **836 B** |
| `StarfieldFrag` | 1400 B | 1988 B | **1392 B** |
| model vertex | 1604 B | 2064 B | **1604 B** |
| model fragment | 672 B | 856 B | **672 B** |

Three of four match to the byte in LENGTH at `Performance` and none matches at `Zero`. The hashes still differ
at `Performance`, as a 2022 glslang against a 2026 one should. What this establishes is the option: **the
incumbent has been compiling at `optimization_level_performance` all along, and `SpirvFrontEndPin.Debug = false`
is what selects it.** The pin's own header says `Debug` "decides whether glslang writes source text and line
tables into the module", which is true and incomplete. Under the new toolchain the two knobs separate, so
`SpirvFrontEndPin` gains an explicit optimisation level and its `Identity` string grows a segment. Without that,
the swap would silently move every module for a reason no pin records.

**Result 4, and the one that fixes the order of work: the two toolchains cannot share a process.** With both
`Veldrid.SPIRV` and `Silk.NET.Shaderc` loaded, the incumbent's compiles corrupt. `StarfieldFrag` failed inside
libveldrid-spirv's own optimiser with

```text
Component index 3596551104 is out of bounds for combined (Vector1 + Vector2) size of 8.
  %29 = OpVectorShuffle %v2float %28 %28 3596551104 3596551104
```

and a second run of the same binary took `SIGABRT` instead. `3596551104` is `0xD65F03C0`, which is the AArch64
encoding of `ret`, so the operand words are being read out of executable memory. Alone, `Veldrid.SPIRV` compiled
all four sources identically across five consecutive runs. Both libraries statically link glslang and
SPIRV-Tools and the second one loaded interposes on the first.

Three consequences, all binding:

1. **There can be no in-process A/B parity test between the outgoing and incoming toolchains.** The obvious
   migration instrument, a test asserting new equals old, is poisoned by its own existence. What replaces it is
   an out-of-process corpus comparison: compile every shipped program under each toolchain in a separate
   process, commit both hash sets, diff them.
2. **The swap is atomic.** `Veldrid.SPIRV` leaves in the same commit the Silk.NET packages arrive in. There is
   no staged cutover and no feature flag, because a flag means both packages are referenced.
3. **The delete comes first and the swap second.** The incumbent `VeldridGpuDevice` calls
   `CompileGlslToSpirv` and `CreateFromSpirv` itself, so `Veldrid.SPIRV` cannot leave while the incumbent
   lives, and the new toolchain cannot arrive while `Veldrid.SPIRV` is loaded. That is not a preference. It is
   the only order the two libraries permit.

### 2.4 The alternative: build the natives ourselves

The other route is a new `APKiwiOrg` repository that builds SPIRV-Cross and shaderc per RID and publishes
nupkgs, vendored into this repo the way `vendor/veldrid` is.

| Criterion | Weight | Silk.NET packages | Own-built natives |
|---|---|---|---|
| Reaches the MSL binding table (#462, M-B1) | 5 | **10** proven by measurement | 10 by construction |
| RID coverage for the fleet | 4 | **10** all eight, two more than needed | 7 a matrix we build and keep green |
| Cost to land | 5 | **9** two package references | 3 a repo, a CI matrix, a release ritual |
| Ongoing carry | 5 | **8** rides the 2.23.0 lockstep already enforced | 2 our bill forever, on three OSes |
| Version control over the toolchain | 3 | 5 whatever Silk.NET ships | **10** exactly the commits we pick |
| Supply chain and license | 3 | **9** MIT wrapper, Apache-2.0 natives, upstream commits named in the nuspec | 8 our build, our provenance ritual, which #672 shows we get wrong |
| Precedent in this repo | 2 | **9** the Silk.NET family is four packages deep already | 4 `vendor/veldrid` exists and is the thing being deleted |
| **Weighted total (max 270)** | | **231** | 154 |

The two places own-built natives win are real and neither is worth 77 points today. Version control matters when
a fix is needed upstream and cannot be waited for, and the engine has no such fix pending. **Recommendation:
`Silk.NET.Shaderc` and `Silk.NET.SPIRV.Cross` at 2.23.0.** The own-built route stays written down here because
the day Silk.NET stops shipping the line is the day it becomes the answer, and rediscovering the option costs
more than a paragraph.

### 2.5 What the swap actually changes in code

- `SpirvFrontEnd.ToSpirv` calls `shaderc_compile_into_spv` with the pinned optimisation level and target env,
  and keeps its whole signature, its cache call and its label semantics.
- `SpirvCrossCompile`'s four emitters call `spvc_context_parse_spirv` plus `spvc_context_create_compiler` at
  `Backend.Msl` or `Backend.Hlsl`, install options built from the existing pins, and compile.
- **`Reflect` is rewritten, and it is the largest single piece of new code in this program.** `SpirvReflection`
  is Veldrid's own reflection pass over SPIRV-Cross, not something bare SPIRV-Cross hands back. The replacement
  enumerates `SPVC_RESOURCE_TYPE_{UNIFORM_BUFFER, STORAGE_BUFFER, SEPARATE_IMAGE, STORAGE_IMAGE,
  SEPARATE_SAMPLERS, STAGE_INPUT}`, reads `DescriptorSet` / `Binding` / `Location`, resolves the stage input
  formats through `spvc_type_get_basetype` and `spvc_type_get_vector_size`, sorts, and fills the same
  `GpuVertexElement` and `GpuResourceLayoutDescription` shapes. The two `ShaderValidationException` messages
  naming an unmodelled `GpuResourceKind` or `GpuVertexElementFormat` are kept verbatim, because they are how
  this failure has always read.
- `SpirvToolchainVersion.Identity` reads the Silk.NET assemblies instead, and the token changes shape. It keys
  a disk cache, so the change is the invalidation.
- The `MSL` binding table replaces `MetalShaderIndexTable`'s parse and the SPIR-V decoration walk behind it,
  which is where the deletion pays for the addition.
- `ShaderValidation`'s four-target sweep loses `GLSL` and `ESSL` or keeps them, and that is a real choice:
  SPIRV-Cross has both backends, so keeping them costs nothing but they validate against no shipped backend.
  Decided at the row, not here.

---

## 3. The golden-family transition

**This is the part that changes what the test suite MEANS, and it has to happen before the incumbent legs come
out rather than with them.**

> **Landed 2026-08-22 in `17.40.0`: steps 1 and 2 below are done** (rows 2 and 3, #685 and #686). Six tokens
> resolve, the three native families exist as byte-identical copies, and `GoldenFamilyCopyGoldenTests` asserts
> the copy over all 120 grids. Step 3's `bake=true` dispatch was deliberately NOT run: while the copy invariant
> stands, committing a native leg's bake output would fork each pair, so the bake confirmation belongs with row
> 4's deletion rather than ahead of it. The CI mechanism is in place either way, and every leg bakes its own
> family on a bake dispatch.

Today `GoldenCompare.GoldenBackendToken` maps seven kinds onto four families, `metal`, `vulkan`, `direct3d11`
and an `opengl` nothing has ever baked. 120 grids are committed, 40 per live family. Each family is OWNED by an
incumbent backend and each native backend is a GUEST in it, which is why a native leg going green today is
evidence: it renders what the incumbent's committed references say, on the same rasterizer.

`BakeRefusal` enforces that. `KE_UPDATE_GOLDENS` is refused on any backend whose token is not its own name, with
a message telling the operator to re-bake on the owner. **Delete the owners and every family becomes references
nothing may re-bake, and the refusal message names a backend that no longer exists.**

**The transition, in the order it must happen.**

1. **Each native kind is promoted to owner of its own family**, `direct3d11-native`, `vulkan-native`,
   `metal-native`, while the incumbent families still exist. Three new families, three old ones, six tokens,
   `BakeRefusal` unchanged and still meaningful.
2. **The new families are baked as byte-identical COPIES of the incumbent families, and that is asserted rather
   than assumed.** The guest legs are green today, which is exactly the statement that each native backend
   already reproduces its host family's grids. So the bake is a copy plus a test that it was one. A self-baked
   golden always passes itself, and this is the one moment in the program where that trap is avoidable for free.
3. **A `workflow_dispatch bake=true` run on all three legs confirms it**, because a golden baked only on Metal
   turns `main` red on the other two.
4. **Then the incumbent legs and their families are deleted**, and the six tokens become three.
5. **`CrossBackendGoldenTests` becomes the only cross-check left**, and the doc says so in as many words rather
   than letting it be discovered.

**The capability-parity tests are retired, and their replacement is weaker.** `NativeVsVeldridCapabilityParityTests`
and its Metal and Vulkan siblings are 1267 lines that compare a native device's reported capabilities against an
incumbent one created beside it. There is no incumbent to compare against afterwards. What is left is the
goldens plus the frozen marginals, and that is a genuinely thinner net. METAL-NATIVE section 19 said this should
be "said so" and this is the saying: after MF1, no test in this repository compares two independent
implementations of the same API. The 120 grids become 120 grids that only their own producer has ever agreed
with, and their value from that day forward is regression detection rather than correctness evidence.

One mechanical trap, load-bearing and easy to lose: the CI matrix selects golden tests with
`--filter FullyQualifiedName~Golden`. A golden test that loses "Golden" from its name during this rework
silently stops running with nothing red.

---

## 4. The deletion inventory

### 4.1 Delete whole

| Path | Lines |
|---|---|
| `KhaozEngine.Gpu/Internal/VeldridGpuDevice.cs` | 507 |
| `KhaozEngine.Gpu/Internal/VeldridMap.cs` | 312 |
| `KhaozEngine.Gpu/Internal/VeldridResources.cs` | 156 |
| `KhaozEngine.Gpu/Internal/VeldridGpuCommandList.cs` | 127 |
| `KhaozEngine.Gpu/Internal/VeldridMetalCommandQueue.cs` | 48 |
| `KhaozEngine.Render.Tests/Gpu/NativeVsVeldridMetalCapabilityParityTests.cs` | 532 |
| `KhaozEngine.Render.Tests/Gpu/NativeVsVeldridCapabilityParityTests.cs` | 397 |
| `KhaozEngine.Render.Tests/Gpu/NativeVsVeldridVulkanCapabilityParityTests.cs` | 338 |
| `KhaozEngine.Render.Tests/Gpu/VeldridMapTests.cs` | 159 |
| `KhaozEngine.Render.Tests/Gpu/VulkanSpirvIncumbentParityTests.cs` | 134 |
| `KhaozEngine.Render.Tests/Gpu/MetalMslIncumbentParityTests.cs` | 132 |
| `KhaozEngine.Render.Tests/Gpu/VeldridLockdownTests.cs` | 99 |

1150 lines of implementation and 1791 of tests. Plus `vendor/veldrid/` entire (three nupkgs and the provenance
README), the `veldrid-fork` package source and its three `packageSourceMapping` patterns in `nuget.config`, and
in `Directory.Packages.props` the `Veldrid`, `Veldrid.SPIRV` and `Newtonsoft.Json` entries. The last of those is
worth naming precisely because phase 2 had to correct three comment sites about it: the CVE override arrives
through `Veldrid -> NativeLibraryLoader -> Microsoft.Extensions.DependencyModel` and NOT through
`Veldrid.SPIRV`, so it goes with the row-1 delete rather than with the toolchain swap.

### 4.2 Edit, not delete

| Path | What changes |
|---|---|
| `KhaozEngine.Gpu/GpuDeviceContext.cs` (736) | The `GraphicsDevice` field, `GraphicsDeviceOptions`, `SwapchainSource`, and the Veldrid arms of `CreateWindowed` and `CreateHeadless`. The adopted-provider path already sits beside them, so this becomes provider-only. |
| `KhaozEngine.Gpu/GpuBackendSelector.cs` (424) | `ToVeldrid` and the `GraphicsDevice.IsBackendSupported` probe. |
| `KhaozEngine.Gpu/GpuBackendProviders.cs` | `IsBuiltIn` is literally the four Veldrid members. Afterwards every kind is provider-backed and `RequiresProvider` is constant true. |
| `KhaozEngine.Gpu/Internal/D3D11ThreadingProbe.cs` (164) | The `BackendInfoD3D11.Device` entry. The raw-pointer entry the native path uses already exists. |
| `KhaozEngine.Gpu/Internal/MetalFrameCapture.cs` (202) | Loses its `VeldridMetalCommandQueue.TryRead` consumer. |
| `KhaozEngine.Gpu/GpuFrameCapture.cs` | `VeldridPathCaptures` goes entirely. |
| `KhaozEngine.Windowing/FrameCap.cs`, `DisplaySettings.cs` | Both gate on `backend == GpuBackendKind.Metal`. The software frame cap is the incumbent Metal backend's alone, so both sites become dead. Gate 5 of the Metal rollout is what settles this, and it must have READ before these are touched. |
| `KhaozEngine.Tests/ArchitectureTests.cs` | `ThirdPartyHomes` loses the `Veldrid`, `Veldrid.SPIRV` and `Newtonsoft.Json` rows and gains the two Silk.NET shader rows. `NativeGpuBackend_DeclaresNoVeldridPackage` becomes vacuous. |
| `KhaozEngine.Render.Tests/GpuPublicApiTests.cs` (393) | Both guards become tautologies, including the IL walk its own comment calls the load-bearing half. |
| The three append-audit test files (1818 lines) | They assert the Veldrid arms exist at thirteen sites each. Reworked, not deleted: the audit is what makes the enum append-only. |

**`OpenListTrackingGpuDevice` stays.** 224 lines, device-free, roughly 50 call sites, and not Veldrid-coupled: it
answers whether anything opens a second command list while one is recording. The fault it detects is the
incumbent's shape, and all three natives note in their own comments that it passes trivially on their leg. That
is an argument for keeping a cheap regression net, not for deleting it. What retires with the incumbent is the
*reason it was urgent*, which is #424's seven-site list, #428's fork guardrail and #429's pre-record phase,
retired together per #424's F8. **#429 is a public API rollback rather than a deletion**, so it is a
consumer-visible decision and gets its own row.

### 4.3 The phantom-slice emulation goes for free

`VeldridArrayLayers` pads a one-layer texture array to two slices, `HasPhantomLayer` remembers it,
`RequireLogicalLayer` refuses an upload aimed at the phantom, and `CopyTexture` walks logical subresources
whenever either side pads. All three sites live inside files being deleted.
[#673](https://github.com/APKiwiOrg/KhaozEngine/issues/673) is the fork change that would have removed the need,
and it never has to happen: it closes as not planned the day row 1 lands, with that as its written reason.

### 4.4 Prose is the long tail

270 `.cs` files mention Veldrid and only 20 import it. The other 250 are comments of the form "the incumbent
does X, we do Y", which compile fine after the delete and become lies. The three native backend packages carry
102 of them, the largest single editing block in the removal, and their `.csproj` `<Description>` fields carry
Veldrid-comparative prose that ships inside the nupkgs.

`docs/DEPENDENCY-SEAMS.md` has 41 mentions across 13 sections, and its densest is "What the backend package may
reference, and the one edge it may NOT", which is decision P2 in full and whose premise dies here.
[#593](https://github.com/APKiwiOrg/KhaozEngine/issues/593) already says that file is stale, for the third time,
and its suggested fix is to stop enumerating and point at the README catalog. That is the right fix and it folds
into this sweep. `docs/CROSS-PLATFORM.md` has 16 across 8 sections, opening with "runs on Veldrid" and carrying a
literal `Veldrid.SPIRV.SpirvCompilation.CompileVertexFragment` example. `docs/USING-KHAOZENGINE.md` has 56
across 22 sections.

**The rule for the sweep: a comment that describes the incumbent is deleted, not rewritten into the past tense.**
The reasoning about why a native backend is shaped as it is stays, and the comparison to a thing that no longer
exists goes. The exception is the four no-Veldrid guards in section 4.2, whose deletion loses the record of why
the backends are structured as they are, so that reasoning moves into `DEPENDENCY-SEAMS.md` before the guards go.

### 4.5 CI

`.github/workflows/cross-platform-gpu.yml` runs five golden legs plus a Vulkan sync-validation job. Three legs
are incumbent (`metal` on `macos-26`, `direct3d11` on `windows-latest` pinned to WARP, `vulkan` on
`ubuntu-latest` pinned to lavapipe) and three are native. All three incumbent legs go.

**The push-versus-schedule tiering becomes moot and should be re-decided rather than inherited.** Today
`fullSuite` is `always` on both Metal legs and `scheduled` on the four Windows and Linux ones, so those four run
golden-only on push and everything on schedule. The comment already notes the tiers survive for feedback speed
rather than cost, since the repo went public and hosted runners are free. With three legs instead of six there
is room to run the full suite everywhere on push, and the reason not to is wall-clock, which is a number to
measure at the row rather than guess at here.

**The `libvulkan` symlink step retires with the Veldrid Vulkan leg and not before**, which is
[#540](https://github.com/APKiwiOrg/KhaozEngine/issues/540) and which the step's own comment already says. It
looks dead on the native leg because Silk.NET resolves through its own native-context search, and it is not: the
capability-parity test creates a Veldrid Vulkan device beside the native one on whichever leg it runs. Since
that test is deleted in the same row, the ordering is satisfied by construction, but the trap is worth carrying
because removing the step early produces a `DllNotFoundException` that reads as a mystery.

---

## 5. Two things that survive the removal on purpose

### 5.1 `Vortice` is freed, and that is a follow-up rather than a row

`Vortice.Direct3D11` is pinned to 2.3.0 *because Veldrid depends on it*, and `Vortice.D3DCompiler` was held to
the same line so the graph keeps one `SharpGen.Runtime`. With Veldrid gone both are free to move. Moving them is
not part of this release, because it changes the DXBC compiler under a backend in the same change that removes
its A/B partner. It is filed and taken afterwards.

### 5.2 The four Veldrid `GpuBackendKind` members stay forever

`Metal = 0`, `Vulkan = 1`, `Direct3D11 = 2`, `OpenGL = 3` are never removed and never renumbered. The enum is
append-only because a consuming game persists the player's chosen backend, and the four become tokens that
resolve to a named exception.

**The consumer evidence says the exception message is the whole feature.** No consumer persists a
`GpuBackendKind` value. Ruinborne persists its own GPU-free `GraphicsBackendSetting` by name, deliberately
decoupled from the engine's enum ordering, and `Sanitized()` resets an undefined value to `Automatic` on every
load. So a stale settings file does not throw and does not corrupt. What breaks is `Ruinborne.Client` at compile
time in one file, `Ui/SettingsMapper.cs`, plus four test files, and a compile break is the loud safe failure.

**The genuinely dangerous move is the opposite one, and it is the one that looks tidy.** Repointing the member
names at the native implementations would silently switch every Windows tester's persisted `"Direct3D11"` onto
the native backend with no rebuild signal and no player notice, because Ruinborne's label keys are API names and
the UI text stays truthful. **The members throw. They do not forward.**

One real regression path to close in the same row: `GpuDeviceContext.PreflightProvider` calls
`GpuBackendProviders.Require(backend)` OUTSIDE the fallback catch, deliberately, because every throw out of
there is a provider bug. A retired kind reaching that path is a hard boot crash where an unreachable preference
self-heals today through `RecoverFallback`. **So the retired members must be rejected at selection, ahead of
`Require`, on the path that reports `GpuBackendSource.FallbackAfterFailure`**, which is what makes Ruinborne
clear the setting and tell the player.

---

## 6. The parity gaps that become load-bearing, with a verdict each

| Issue | Gap | Verdict |
|---|---|---|
| [#597](https://github.com/APKiwiOrg/KhaozEngine/issues/597) | `GpuResourceLayoutElement.Dynamic` documents dynamic structured buffers. Metal-native accepts, Vulkan-native and D3D11-native refuse. | **After, and probably never.** The incumbent is not masking this: it is a seam-doc-versus-native mismatch that removal does not touch, and nothing in-engine declares one. Narrow the doc. On this list only so the doc sweep catches it. |
| [#599](https://github.com/APKiwiOrg/KhaozEngine/issues/599) | A resource-free shader pair reflects one empty layout, which Metal-native refuses, while Veldrid NREs if you declare a layout and bind nothing. | **After, and removal is what makes the good fix reachable.** The only spelling correct on both today is declare-one-layout AND bind-an-empty-set, and that ceremony already bit the engine's own smoke tests. Deleting the incumbent deletes half the constraint and makes trimming trailing empty sets in `Reflect` legal. Since `Reflect` is being rewritten anyway in the toolchain row, **the fix lands there rather than as its own change.** |
| [#602](https://github.com/APKiwiOrg/KhaozEngine/issues/602) | `GpuReadback.ReadBuffer<T>` passes `srcOffsetBytes` unfiltered into `CopyBuffer`. Metal-native throws on a non-multiple-of-4, the other three tolerate. | **Before, and independently.** Removal narrows the divergence from three-versus-one to two-versus-one and resolves nothing. Rounding or refusing at the helper is the only fix that makes the backends agree, it is small, and it is better done while the incumbent is still there to confirm the tolerant behaviour was never load-bearing. |
| [#613](https://github.com/APKiwiOrg/KhaozEngine/issues/613) | `GpuRecording` narrows to one recorder because the incumbent corrupts otherwise. All three natives advertise N-concurrent as an argued design property. | **After, as its own spec.** This is the highest-value thing the removal unblocks. It is explicitly not "delete the register": it needs a deliberate opt-in shape, which is a design, which makes it a roadmap item and not a row here. |
| [#673](https://github.com/APKiwiOrg/KhaozEngine/issues/673) | The incumbent cannot express a one-layer texture array, so it pads a phantom slice. | **Closed as not planned by row 1.** Section 4.3. |
| [#461](https://github.com/APKiwiOrg/KhaozEngine/issues/461) | Rule 2's drain is Vulkan-shaped and the FFT ocean still pays it. Three of three natives honour it natively. | **After.** Removal is the enabling condition and not the change. Note the rule 1 and rule 2 comment in `GpuInterfaces.cs` describes Veldrid's Vulkan behaviour BY NAME and becomes false on the day row 1 lands, so that comment is a row-1 edit even though the rule itself is not. |
| [#604](https://github.com/APKiwiOrg/KhaozEngine/issues/604) | One uniform buffer per pipeline. Measured by MM6 as the incumbent's declaration-order numbering rather than a property of Metal. | **After, and ordered behind the toolchain swap.** #586's follow-up is explicit that #462 is what makes this trivial instead of delicate: with the binding table authored, the numbering question disappears rather than being re-measured. Doing #604 before the swap means re-measuring three unmeasured shapes against a numbering that is about to stop existing. Note it deletes shipped validation code (`MslBindingOrder.CheckPrefix`, `CheckStage`, `ShaderValidation.CheckMslBufferSlots`, `MslBindingOrderGuardTests`) in one change, or new shaders fail validation before reaching a device. |

Only one gap is ordered BEFORE the removal, one is folded INTO it, and the rest wait. That is deliberate: the
release has to be able to claim that deleting an unused implementation changed nothing a player sees, and every
gap folded in weakens that claim.

---

## 7. The order of work

Two releases, and the split is forced by section 2.3's result 4 rather than chosen for comfort. Rows 1 to 7 are
release one and remove `Veldrid`. Rows 8 to 11 are release two and remove `Veldrid.SPIRV`.

| # | Row | Size | Depends on | Gate |
|---|---|---|---|---|
| 0 | **Step A has landed and soaked.** Not a row, a precondition. The three OS defaults are native and the Metal rollout's gate 5 has been READ, because rows 5 and 6 touch code whose fate gate 5 decides. | - | - | Step A's own gates green, gate 5 recorded |
| 1 | **`#602`: round or refuse an unaligned `CopyBuffer` offset at `GpuReadback`.** The one parity gap taken before the removal, while the incumbent is still there to confirm the tolerant behaviour was never load-bearing. | S | 0 | All four backends agree on a non-multiple-of-4 offset, headless |
| 2 | **Promote the three native kinds to owners of their own golden families**, `direct3d11-native`, `vulkan-native`, `metal-native`. Six tokens, `BakeRefusal` unchanged. | M | 0 | Six tokens resolve, `BakeRefusal` still refuses a guest, no test loses "Golden" from its name |
| 3 | **Bake the three new families as byte-identical copies of the incumbent families, and assert the copy.** Then confirm on all three legs via `workflow_dispatch bake=true`. | M | 2 | 120 new grids byte-equal to the 120 old ones, and the three legs green against them |
| 4 | **Delete the incumbent backend**: the five `Veldrid*.cs` files, the seven Veldrid-only test files, `vendor/veldrid`, the `veldrid-fork` feed, and the `Veldrid` plus `Newtonsoft.Json` package entries. `Veldrid.SPIRV` STAYS. | L | 3 | Full suite green on all three native legs, zero warnings, no `Veldrid` package reference outside `Veldrid.SPIRV` |
| 5 | **Retire the four `GpuBackendKind` members to named exceptions**, rejected at selection AHEAD of `Require` so an unreachable preference still self-heals through `RecoverFallback`. Rework the three append-audit test files. | M | 4 | A persisted `Direct3D11` token reports `FallbackAfterFailure` rather than crashing, and the append audit passes at every one of its thirteen sites |
| 6 | **Delete the three incumbent CI legs, the `libvulkan` symlink step and the incumbent-only rungs**, and re-decide the push-versus-schedule tiering on a measured wall-clock rather than inheriting it. | M | 4 | Three legs green on push, and the wall-clock number recorded in `CROSS-PLATFORM.md` |
| 7 | **Retire #424's F8 set together**: the nested-`Begin` site list, #428's fork guardrail, and #429's pre-record phase. `OpenListTrackingGpuDevice` STAYS. **#429 is a public API rollback and needs its own consumer note.** | M | 4 | The seam contract still refuses a nested `Begin`, and the `Render3DSurface`-without-`onPrepare` residual is decided in writing |
| 8 | **The toolchain swap.** `Veldrid.SPIRV` out and `Silk.NET.Shaderc` plus `Silk.NET.SPIRV.Cross` in, atomically, in one commit. `SpirvFrontEndPin` gains an explicit optimisation level. `Reflect` is rewritten with an explicit sort, and #599's trailing-empty-set trim lands with it. | L | 4, 6 | The out-of-process corpus comparison committed for every shipped program, `ShaderValidation` green on every target, and no assembly referencing both toolchains anywhere in the tree |
| 9 | **Rebaseline the byte-equality tests and rebake the goldens.** `VulkanSpirvByteEqualityTests` and `D3D11HlslByteEqualityTests` move by construction. The goldens are tolerance-based grids, so they may well NOT move, and which of those happened is the row's finding rather than its assumption. | M | 8 | Bake on all three legs via `workflow_dispatch bake=true`, and the count of grids that actually moved recorded |
| 10 | **Author the MSL binding table and delete what it replaces**: `MetalShaderIndexTable`'s parse of the emitted MSL and the SPIR-V decoration walk behind it. This is M-B1 and #462's real payoff. | L | 8 | `MetalMslIdJoinSpikeTests` retired or repointed, the 42 shipped programs bound at authored indices, and the Metal leg's full suite green on real hardware |
| 11 | **The doc sweep.** 250 comment sites, 13 `DEPENDENCY-SEAMS.md` sections including P2 whose premise died, `CROSS-PLATFORM.md`, `USING-KHAOZENGINE.md`, every affected package README, the three backend `.csproj` descriptions, and #593 folded in. | L | 10 | `check-doc-versions.sh` green, and a grep for `Veldrid` across all `*.md` and `*.cs` returning only deliberate history |

**#604 and #613 are NOT rows here.** Both are roadmap items that this program unblocks, both are ordered behind
row 10, and both change rendering or the seam contract. Putting either inside a release whose claim is "nothing
changed" would destroy the claim.

---

## 8. Risk register

**R1, and it is the only one that can hurt a person: Ruinborne's Windows testers are the only real players this
fleet has, and native Direct3D 11 has no field hours at the time of writing.** Today one hundred percent of them
run Veldrid D3D11, because `Automatic` resolves through the OS probe to `GpuBackendKind.Direct3D11` and the
native kinds are reachable only through `KE_GRAPHICS_BACKEND`. Step A is what changes that, and step A's soak is
therefore the entire field evidence base for this release. **Mitigation: row 0 is a precondition and not a
courtesy. If step A's soak has not produced Windows field hours on native D3D11, this program does not start.**
The A/B instrument that made every previous phase recoverable is exactly what row 4 destroys, so the last chance
to use it is before row 4 and not after.

**R2: the golden families stop being evidence and start being regression detection.** Section 3. Nothing
mitigates this, it is the cost of the removal, and the mitigation is honesty about it in `CROSS-PLATFORM.md`
rather than a test that pretends otherwise.

**R3: the toolchain swap moves the emitted bytes and might move pixels.** The goldens are tolerance-based
downsampled grids and robust to driver noise, so the likely outcome is that no grid moves. Likely is not
measured. Row 9 exists to measure it, and its finding is the count of grids that moved, recorded either way.

**R4: the two toolchains corrupt each other in one process.** Measured, section 2.3 result 4. Mitigated by
ordering (row 8 after row 4) and by atomicity (one commit). The residual risk is a developer adding a
`Veldrid.SPIRV` reference back for a comparison, so row 8's gate includes an assertion that no assembly in the
tree references both.

**R5: reflection order.** SPIRV-Cross enumerates resources in neither declaration nor binding order, and a naive
port silently permutes resource layouts. This exact class of defect has shipped three times in this repository.
Mitigated by an explicit sort plus a test whose source declares its resources in an order matching neither.

**R6: 250 stale comments are a slow leak, not a break.** Nothing fails when one is missed, and the next reader
believes it. Mitigated by row 11's gate being a grep rather than a judgement, and by the rule in 4.4 that a
comment describing the incumbent is deleted rather than rewritten.

**R7: the four no-Veldrid guards become tautologies and their deletion loses the record of WHY the native
backends are shaped as they are.** The IL walk in particular is described by its own comment as the load-bearing
half. Mitigated by moving that reasoning into `DEPENDENCY-SEAMS.md` before the guards are deleted, in row 11,
which is why row 11 is the last row rather than a tidy-up.

---

## 9. What this document does not decide

- Whether `ShaderValidation` keeps its `GLSL` and `ESSL` targets once the emitter is ours. Row 8.
- The push-versus-schedule CI tiering, which is a wall-clock measurement. Row 6.
- The `Vortice` unpin. Section 5.1, filed and taken afterwards.
- Whether `#597` is fixed at all or the seam doc is narrowed. Section 6 leans to narrowing and does not rule.
- The `KE_D3D11_*`, `KE_VULKAN_*` and `KE_METAL_*` unification. METAL-NATIVE section 19 filed it as a
  consumer-visible rename belonging in its own change note, and that is still true.
