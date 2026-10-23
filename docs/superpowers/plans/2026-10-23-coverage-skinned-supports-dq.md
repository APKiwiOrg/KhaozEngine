# Coverage Skinned Supports and DQ Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development to implement this plan task by task. The selected method is Codex-only SDD, with a fresh task reviewer after each independently testable commit and a whole-branch review at each milestone. Steps use checkbox (`- [ ]`) syntax. The owner reviews this concrete plan before runtime work starts. Do not switch execution method.

**Goal:** Add NC2 rigid-DQ coverage (S4) and compound posed-support coverage (L), preserving ordinary DQ, legacy LBS and the separate private fit and cost gates.

**Architecture:** Select the source graph independently from the covering graph. Retain one upload-time source snapshot, skin original host and garment vertices precisely, and publish those garment words to both finite support queries and every displayed pass. V2 reduces weighted whole-face constraints into one scalar displacement per owned position group, then reuses refinement, ordered gathers, selected final surfaces and history.

**Tech Stack:** C#, System.Numerics data carriers, the existing binary32 arithmetic/analyzer and exact predicates, word-addressed compute, xUnit, and Metal/Direct3D11/Vulkan artifact and device checks.

**Spec:** [Approved amendment](../../design/EQUIPMENT-COVERAGE-SKINNED-SUPPORTS-DQ-AMENDMENT-2026-10-23.md), supplementing [Amendment R](../../design/EQUIPMENT-COVERAGE-AMENDMENT-R-2026-10-18.md), the [kernel contract](../../design/EQUIPMENT-COVERAGE-KERNEL-CONTRACT-2026-10-18.md) and [arithmetic proof](../../design/EQUIPMENT-COVERAGE-GPU-ARITHMETIC-PROOF.md). The accepted DQ stage-3 design/plan/bounds proof travel from revision `ad86b4a6193800de118ad9da9e824683aa3e7b9c` in J0. This plan replaces that plan's S4 joint-amendment filename and fallback wording, retaining its seven S4 packages and DQ1 to DQ6.

## Global Constraints

- Owner approval is dated 2026-10-23. Preserve 32 actors and the 15 percent aggregate GPU overhead gate. Added CPU preparation remains at most 2 ms at 32 actors, with zero warm managed allocations.
- Hard per-frame policy includes cold equips and synchronized events. No new colour, shadow or outline passes. No per-strap copy of the full pipeline.
- Four disjoint active host commands, 32 named regions, eight distinct covering sources per actor, refinement levels 1 to 4, 65,536 final host vertices/triangles, 1,024 support triangles and 256 boundary edges per command. Compound components count toward the same totals. Material slices do not inflate source count.
- Each positive-strength position group has one region and recession direction. Protected points and shared zero-strength boundaries cannot move. No anatomical inference or nearest-position welding.
- Keep 1.5 to 4 mm unrecessed stance stand-off, 0 to 2 mm actual leather stack separation, 0.5 mm slab/join limits and G-1's 0.35 m/s. Keep accepted G-2 references and G-3 interval classification. Investigate 0.5 mm leather clearance as a trial, never a default or proved fit.
- Per-region world recession and optional tighter per-source footprint caps all apply to the combined final result. Strictest overlap cap wins. No clamping, automatic cap increase or inherited plate allowance.
- Numerical collar, recession and replacement/continuity appearance limits await matched private evidence and owner selection under DB-5 before content admission. Clipping tolerance stays zero.
- NC2 equality is every accepted source/setup/final word and failure attribution. Preserve signed zeros, inclusive F at magnitude at most 2^-126, B27/B30, C3 corrected division/root, C5/C6 restrictions and C8 module caps.
- Keep all 72 legacy LBS cases and 144 frozen output hashes. Legacy constructors/JSON keep their meaning and semantic hashes. Attachment set JSON v1/v2 bytes do not change.
- DQ source admission retains four integer indices (including unused slots), finite nonnegative weights, stored X/Y/Z/W order and the scalar three-add total. The existing domain is total below 1e-8 or abs(total - 1) at most 2^-20. The precise graph separately accounts for inclusive-flush reads and rounded totals. DQ uses the accepted prepared eight-word joint payload. LBS retains sixteen-word matrices and existing composition. Intentional mixed-method host/garment binding is unsupported. Rigid sockets stay matrix driven.
- Public fixtures are independently authored synthetic geometry. No private mesh, vertex/palette payload, map, asset hash or capture enters this repository, commits, artifacts or runs. Private ownership is [Grimhollow #547](https://github.com/APKiwiOrg/Grimhollow/issues/547).
- PL-1 ordering distinguishes provisional stance L1a, preliminary complete-domain L1c/L6 at the exact reviewed oracle revision before P1, and final caps/appearance plus released-pin revalidation before private admission. Complete L1 never gates construction of the L3 reference that completes it.
- Exact oracle samples are not continuous carry cells. DB-2/DB-4 require certified alternatives or complete certified coverage for every admitted pose in any claimed hard no-clipping domain. Old LBS cells and refresh lifetimes do not transfer.
- This document authorizes no timing, load, native/paid run, release or adoption. The executing orchestrator obtains the required separate authorizations. [#1539](https://github.com/APKiwiOrg/KhaozEngine/issues/1539) remains a Vulkan validation limitation, even if word tests pass.

## Review Focus

1. A garment inverse bind differs within the existing binding tolerance. Its own remapped prepared palette must drive both query and display, never the host palette merely because bone counts agree. Tests in L2 and L4.
2. A narrow strip crosses a host face with no covered corner, or skin is already wholly beyond the garment. Final full-face clearance must fail or recess correctly. Tests in L3b/L3c.
3. Plate plus strap constraints require displacement beyond the strap's tighter cap, regardless of source order. Refuse joint fit without spending the plate cap. Tests in L3b/L5.
4. GPU Select changes an actor outcome while an unrelated ordinary actor also changes. Both rejection regions survive one target clear and both resolve paths consume them. Tests in S4.5b/L7.
5. A candidate partition fits the source count but exceeds a region's support/boundary cap after cross-region duplication, or a positive authored strength flushes to zero, a projection degenerates between safe endpoints, or an ambiguity queue fills. Refuse with the first ordered witness, never divide by zero, drop a constraint or accept sampled endpoints as domain proof. Tests in L1/L3a/L3b/L5.

---

## Source map and ownership

Inspected coverage snapshot `8be362eecff2a28c6798e6f59bbb116fe1eb8268` and accepted DQ snapshot `ad86b4a6193800de118ad9da9e824683aa3e7b9c`. Paths below are repository relative. In task file lists, `Coverage/` and bare coverage filenames resolve under `KhaozEngine.Render3D/Coverage/`, `Scene3D.*` under `KhaozEngine.Render3D/`, and `Render3D/`, `Gpu/` and `Fixtures/` under `KhaozEngine.Render.Tests/`. `Attachments/`, `Attachments.Render3D/` and `Attachments.Tests/` resolve to their matching `KhaozEngine.*` projects. `Internal/` and `Rendering/` resolve under Render3D. The source map uses its separately stated common prefix. New test files live in the named matching test project. Existing symbols are mapped here. Explicitly labelled new interfaces below are implementation decisions, not claims that they exist now.

| Existing home and symbol | Use and boundary |
| --- | --- |
| `Scene3D.SkinningProfiles.cs`, `RetainSkinningSource(SkinnedGltfMesh)` at DQ revision | DQ-R1 clones every profile's source, indices, inverse binds, rest pose and rig at upload. Coverage must bind this retained snapshot, not retain a second caller-mutable mesh. |
| `Internal/PreparedSkinningStore.cs`, `Prepare`, `Lbs(in PreparedSkinningPose)`, `Dq(in PreparedSkinningPose)` | Frame-owned typed payloads. Tokens bind owner/frame/profile/offset/count. `Models/DualQuaternionPalette.Prepare(jointWorld, inverseBind, destination, context)` is the scalar CPU producer. LBS coverage still uses `CoverageReferenceSkinning.Compose`. |
| `Models/SkinningProfile.cs`, `RigidDualQuaternion.cs`, `DualQuaternionBounds.cs` | Immutable method/version, 32-byte real/dual values and ordinary `DqBoundsSource`/`TryBoundModel`/`TryBoundWorld`. Ordinary bounds are not a proof for inclusive-flush coverage. |
| `Coverage/HostCoveragePart.cs`, `Create`/`Matches`, `CoverageSourceTopology.cs`, `CoverageRefinement.cs` | Exact decoded material provenance, retained source words and source-skin-before-refine topology. Existing alias validation canonicalizes influences for LBS. DQ aliases additionally need equal ordered influences or an explicit rejection because order can affect DQ. |
| `Coverage/CoverageOracle.cs`, constructor and `TryCheck(ReadOnlySpan<Matrix4x4>, in Matrix4x4, ReadOnlySpan<CoverageCommandPose>, out CoverageOracleCertificate, out CoverageOracleFailure)` | Legacy LBS oracle remains. Its certificate currently contains three geometric values, not an authenticated identity. Add new evidence wrappers rather than claiming old certificates already carry a digest. |
| `CoverageReferenceSkinning.TrySkin`, `CoverageReferenceConstructor.TryInterpolate`/`TryConstruct`, `CoverageReferenceFrames.TryPrepare`, `CoverageReferenceCorrection` | Source dispatch and unchanged rigid-v1 conformation. V2 gets separate posed setup/weighted-face reference files. Reuse arithmetic and ordered normal gathers where semantics agree. |
| `Coverage/Gpu/CoverageGpuLayout.FrameWords(int)`, `CoverageGpuUpload`, `CoverageGpuPipeline.Record`, `Scene3D.CoverageGpu.StageGpu` | Existing matrix ABI. Add typed variants, checked reservations and distinct v2 phase layouts. Do not rewrite v1 offsets. |
| `Attachments/EquipmentCoverageDefinition.cs`, `EquipmentCoverageJson.cs` | BCL-only rigid-v1 companion. Add a separate v2 definition/codec, not a schema change to attachment sets. |
| `Attachments.Render3D/AttachmentSetCache.Coverage.cs`, `GetCoverage`, `HostCoverageBinding.TryPrepare`, `SkinnedAttachmentBinding.CopyPalette` | Current coverage requires a rigid part. V2 resolves compound sources and copies garment-remapped joint matrices, then composes using that garment's own inverse binds. Preserve profile and rigid binding checks. |
| `Coverage/CoverageActorBounds`, `CoverageOrdinaryBounds`, `CoverageSurfaceBounds`, `Scene3D.CoveragePaletteBounds.cs` | Existing LBS enclosure and origin-anchored relative box with outward world conversion. Add DQ/v2 producers before using them in any admission/culling/history consumer. |
| `CoverageMotionHistory`, `CoverageSubmissionState`, `Gpu/CoverageGpuActor.History.cs`, `CoverageGpuSelectGlsl.cs` | Selected FINAL words and Submitted/Abandoned allocation ownership. Preserve selected-kind checks and current-position seeding. |
| `Rendering/TemporalHistoryRejection.cs`, `Scene3D.SkinningHistoryRejection.cs` at DQ revision | Generic CPU-region R8 rejection, persistent zero input and first resolving view. Extend this owner with coverage conditions. |
| `Rendering/CoverageTransitionMask.cs`, `AddGpuChangedBounds(in CoverageChangedBounds, IGpuBuffer, uint)`, `Scene3D.CoverageMotion.PrepareCoverageMotion` | GPU `TransitionChanged` is read at `p.GpuReservation.CurrentBase + 15`. Move this conditional producer and anchored interval projection into the generic owner. It must not be lost in a CPU-only merge. |

Paths in the table omit the common `KhaozEngine.Render3D/` prefix except when prefixed `Attachments/` or `Attachments.Render3D/`, which mean `KhaozEngine.Attachments/` and `KhaozEngine.Attachments.Render3D/`.

The filtered checkout was inspected read only at base `8be362eec` with staged unfinished work. Its `CoverageGpuFilterPolicy`, `CoverageGpuFilteredPipeline`, `CoverageGpuFilteredSources` and light ball/job helpers are informative, not accepted dependencies. No SHA for that result is pinned here. Its present Vulkan-only capability policy does not become a promise of Metal/D3D11 filters. L5 requalifies the reviewed result or keeps exact execution on those backends.

Public engine ownership covers J0, S4, synthetic L1, L2 to L5 and L7. A separately scoped private game owner covers the read-only L1 preflight, L6 and A1 game adoption. P1 is a separate reviewed feasibility/cost handoff. Never edit another lane's worktree to implement a dependency.

## Planned interface register

These names are new unless listed in the source map. Define them in the named task, then consumers use them unchanged. Public classes own cold inputs. `CoveragePoseInput` borrows spans only for a synchronous call and is never stored in a collection. Multi-source pose tokens are ordinary readonly structs over cold-sized frame-owned stores, so `ReadOnlySpan<CoverageCoveringPose>` is legal C#. Frame staging copies all spans before returning.

- **S4.1, `Coverage/CoverageSkinningIdentity.cs`:** `CoverageSourceGraph { LinearBlendV1 = 1, RigidDualQuaternionV1 = 2 }`, `CoverageSupportGraph { RigidSheetV1 = 1, PosedFiniteV2 = 2 }`. Immutable `CoverageSkinningIdentity.Create(CoverageSourceTopology source, SkinningProfile profile, ReadOnlySpan<Matrix4x4> inverseBind, ReadOnlySpan<int> jointRemap)` binds exact source/attribute/influence order, material provenance, inverse binds, remap and profile. `SourceGraph` and `StaticSha256` are get-only. Never include a live palette in this cold key.
- **S4.1, same file:** readonly ref struct `CoveragePoseInput` with named factories `FromLbs(CoverageSkinningIdentity identity, ReadOnlySpan<Matrix4x4> composed)` and `FromDq(CoverageSkinningIdentity identity, ReadOnlySpan<RigidDualQuaternion> prepared)`. Expose get-only `Identity`, `Lbs`, `Dq`. Reject wrong graph/count. No conversion between payload kinds. A separate allocation-free evidence digest appends exact current pose/model/command words during checks. Scene caches keep prepared store owner/frame tokens and retained source identity, not newly allocated per-frame strings.
- **S4.2:** `CoverageReferenceDualQuaternion.TrySkin(CoverageReferenceArithmetic f, CoverageReferenceSkin skin, CoverageSourceTopology source, ReadOnlySpan<RigidDualQuaternion> palette, Span<ModelVertex> posed, out CoverageFailureCode code) : bool`.
- **S4.4:** `CoverageDualQuaternionBounds.TryBound(in DqBoundsSource source, ReadOnlySpan<RigidDualQuaternion> palette, in Matrix4x4 model, float maxWorldRecession, Vector3 renderOrigin, out CoverageSurfaceBounds bounds) : bool`, internal. Cover precise source, fixed refinement, final recession and placement. Ordinary no-compute bounds continue through `DualQuaternionBounds` with explicit shader/projection error enclosure.
- **S4.1, `Coverage/CoverageSampleEvidence.cs`:** define shared readonly record structs `CoverageInputDigest(ulong W0, ulong W1, ulong W2, ulong W3)` and `CoverageSampleEvidence(CoverageOracleCertificate Geometry, CoverageInputDigest InputSha256)`. `CoverageInputDigest.WriteBytes(Span<byte> destination) : void` writes exactly 32 bytes, W0 through W3 in big-endian order, matching the original SHA-256 digest bytes. Require at least 32 destination bytes, leave any tail unchanged and throw `ArgumentException` before writing when shorter. The evidence digest binds all exact source, pose, graph, map, recipe and command inputs. Hash in preallocated oracle scratch and return value words, with no warm string/evidence-object allocation. These are shared value contracts, not draw/carry authority. S4.6 computes rigid-v1 evidence and L3c computes posed-v2 evidence using these same types.
- **S4.1, `Coverage/CoverageOracle.cs`:** produce public `CoverageOracle(CoverageRefinement topology, CoverageSkinningIdentity identity, int commandCapacity = CoverageScratch.MaxCommands)` as a cold identity-bearing constructor. Validate nonnull arguments, exact topology/source identity, supported profile/graph and inverse-bind/remap/count agreement before owning bounded work arrays. Null throws `ArgumentNullException`, mismatched identity throws `ArgumentException` and unsupported command capacity throws `ArgumentOutOfRangeException`. Identity owns its retained input snapshots, not caller arrays. This constructor establishes storage for either source graph without activating DQ coverage or depending on the later DQ evaluator. S4.1 also owns internal `ulong CheckGeneration { get; private set; }` and `BeginDiagnosticCheck() : void`, which invalidates the constructed count and advances the generation before validation in the existing legacy check. Later check entry points consume this lifecycle seam. A DQ identity passed to the legacy LBS-only check throws `NotSupportedException` after entry invalidation, never evaluates LBS. L3c later exposes the read-only public `DiagnosticGeneration` projection of this state. Preserve the existing constructor and v1 semantics. L3c can construct its oracle after S4.1/S4.2/L2/L3b, without S4.5/S4.6.
- **S4.6:** produce `CoverageOracle.TryCheck(in CoveragePoseInput pose, in Matrix4x4 model, ReadOnlySpan<CoverageCommandPose> commands, out CoverageSampleEvidence evidence, out CoverageOracleFailure failure) : bool` for rigid-support DQ behavior. Consume the S4.1 constructor and shared value types, rather than redeclaring them. Legacy overloads remain unchanged. The posed-v2 L3c implementation dispatches its source through S4.2 directly, not through this later rigid-check overload.
- **L2, `Attachments/PosedEquipmentCoverageDefinition.cs`:** immutable `PosedEquipmentCoverageDefinition`, `PosedEquipmentCoverageCommand`, `PosedEquipmentSupport`. The definition binds attachment byte hash, host map semantic hash, graph IDs and sorted command IDs. A command owns `HostRegion`, `CoverageLocal` relative to an explicit `FrameJoint` resolved through the existing `ContractJointMap`, finite `MinZ/MaxZ`, regional `MaxWorldRecession` and `Supports`. A support owns `PartId`, sorted `EquipmentSupportRange` entries, world `Clearance`, optional tighter `MaxWorldRecession`, and explicit `AssociatedHost` ranges using new BCL-only `EquipmentHostRange(int Mesh, int Primitive, int FirstTriangle, int Count)` in Attachments. Count retained source incarnations by exact snapshot and complete binding/pose/placement producer identity across commands. Several mesh ordinals, component objects, part references or material slices can belong to one source when that complete producer identity agrees. Different placements or equipped instances count separately. Neither object count nor material count substitutes for this identity. Form the rigid frame with the existing socket matrix order `CoverageLocal * jointWorld[FrameJoint] * model`, then apply the existing finite/orientation/conditioning admission. This requires no rigid covering part to exist and does not interpolate a DQ frame per vertex. No v1 collar smoothstep is applied in v2. The permitted collar is the host map's positive patch and budget.
- **L2, `Coverage/CoveragePosedSupportSet.cs`:** immutable `CoveragePosedSupportSet` binds retained source snapshots/skin identities, inward ranges and complete host association. Public readonly struct `CoverageCoveringPose` is an opaque token with internal store/set/source/instance/frame/view identity and public get-only `SourceKey`, `EquipmentInstanceId`, `SourceIndex` and `Model`. `EquipmentInstanceId` and `SourceIndex` agree with `SourceKey`, never a separate index namespace. New `Coverage/CoverageCoveringPoseStore.cs` owns copied cold-sized matrix/DQ slots. Define readonly record struct `CoverageCoveringReservation(ulong EquipmentInstanceId, CoveragePosedSupportSet Supports)`. Its constructor takes `IReadOnlyList<CoverageCoveringReservation> reservations`, allowing distinct instances of one compiled set. `BeginFrame(ulong frameGeneration, ulong viewGeneration)` invalidates earlier tokens. `Prepare(ulong equipmentInstanceId, CoveragePosedSupportSet supports, int sourceIndex, ReadOnlySpan<Matrix4x4> remappedJointWorld, in Matrix4x4 model)` composes with that source's retained inverse binds through the pinned producer and returns `CoverageCoveringPose`. `PrepareRigid(ulong equipmentInstanceId, CoveragePosedSupportSet supports, int sourceIndex, in Matrix4x4 model)` handles rigid sources. Validate set/source kind, palette count, pose and model before publishing a token. Internal span access checks the owning store and generations. Do not retain caller spans or permit overwriting a published source incarnation slot within a frame. `CoveragePosedCommand` is a readonly record struct with constructor `(uint Id, ulong EquipmentInstanceId, CoverageRefinement Topology, int RegionIndex, CoveragePosedSupportSet Supports, Matrix4x4 CoverageWorld, float MinZ, float MaxZ, float MaxWorldRecession, float TransitionWeight)`. It validates positive IDs and admitted finite caps/budgets/weight.
- **L2, `Coverage/CoveragePosedSupportSet.cs`, shared by L3a/L3c:** define readonly record struct `CoverageCoveringSourceKey(ulong EquipmentInstanceId, CoverageInputDigest SupportSetIdentity, int SourceIndex, CoverageInputDigest SourceIdentity)`. `SupportSetIdentity` is the immutable compiled support-set semantic digest, including ordered membership/graph/association identity. `SourceIdentity` binds the exact retained source snapshot, source kind/profile and complete cold bind/remap/placement-producer identity. `SourceIndex` is nonnegative and local to that set. The equipment incarnation ID distinguishes two equipped instances of the same set/source. The key contains no object references or runtime store offsets and is stable across reconstruction of identical inputs. `WriteBytes(Span<byte> destination) : void` writes exactly 76 canonical bytes, equipment ID as uint64 big endian, support-set digest's 32 bytes, source index as uint32 big endian and source digest's 32 bytes. Require at least 76 bytes, preserve a longer tail and throw `ArgumentException` before writing on shorter input. Runtime owner/frame/view validation remains in the full `CoverageCoveringPose` token, not this serializable key. L2 produces this contract before the L3a witness consumes it.
- **L2, `Attachments.Render3D/LoadedPosedEquipmentCoverage.cs`:** separate borrowed immutable loaded view. `AttachmentSetCache.GetPosedCoverage(LoadedAttachmentSet set, string key, string expectedSha256)` loads strict v2 companions. Define `CoveredPosedAttachmentInstance(ulong InstanceId, BoundAttachmentSet Set, LoadedPosedEquipmentCoverage Coverage, float TransitionWeight = 1)` as a readonly record struct. Add `HostCoverageBinding.TrySetPosedEquipment(ReadOnlySpan<CoveredPosedAttachmentInstance> equipment, out CoverageFailure failure) : bool` and `TryPrepare(Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 model, in RigidInstanceDraw rigidTemplate, in SkinnedInstanceDraw garmentTemplate, out SkinnedCoverageFrame frame, out CoverageFailure failure) : bool`. The setter transactionally replaces the complete equipped set, including rigid sources inside v2, rather than mixing independent legacy and v2 positive regions. The preparation path uses its frame-owned covering pose store and copies its complete source tokens into scene staging. Keep old rigid methods callable unchanged.
- **L2, `Coverage/SkinnedHostConformer.cs`:** add `ReservePosedSupports(IReadOnlyList<CoverageCoveringReservation> reservations) : void` and `TryPreparePosed(ReadOnlySpan<Matrix4x4> jointWorld, in Matrix4x4 model, ReadOnlySpan<CoverageCoveringPose> sources, ReadOnlySpan<CoveragePosedCommand> commands, MotionKey actorKey, CoverageFallback fallback, out SkinnedCoverageFrame frame, out CoverageFailure failure) : bool`. Existing `TryPrepare` remains the rigid-v1 entry, choosing its source graph from the retained conformer identity. Scene staging owns copied inputs and typed palette preparation, not the caller.
- **L3a, `Coverage/CoveragePosedScratch.cs`:** actor-owned cold-sized `CoveragePosedScratch(CoverageRefinement topology, IReadOnlyList<CoverageCoveringReservation> reservations)`. It owns exact posed garment vertices, current projected triangles/area/AABB/slope metadata, ordered associations, immutable face results and final host work, with checked capacity. L3a defines and stores pending internal witnesses using new readonly record struct `CoveragePosedFailureWitness(CoverageCoveringSourceKey Source, int RefinedFace, CoverageSourceTriangle HostSource, CoverageSourceTriangle SupportSource, Vector3 Location, float Clearance, CoverageSourceGraph Graph, CoverageInputDigest Input)`. L3c owns public nullable `CoveragePosedFailureWitness? LastFailureWitness { get; }` on scratch. Clear pending and public witnesses on every new scratch check/setup attempt. The outer L3c oracle binds `Input` to its complete frozen admitted host/covering/model/command input digest before publishing a witness. Standalone internal helper failures remain pending internal observations and expose no public full-input witness. A covering-related geometric failure carries the complete L2 `Source` key, original source triangle provenance and final refined-face index. `Location` is in host model space, `Clearance` is a world-space length, `Graph` is the host source graph and `Input` is the full frozen attempted-input digest including every incarnation, pose and placement. Preserve first ordered failure attribution. When no meaningful covering source exists, leave the witness null and retain structured code/count failure. Associated oracle/scratch generation changes or any last-input store frame/view advance invalidate the nullable property, which then returns null. Copied value witnesses and their 76-byte source keys can be serialized privately without object/store identity. No static cache owns current pose geometry.
- **L3a:** `CoverageReferencePosedSupport.TryPrepare(ReadOnlySpan<CoverageCoveringPose> sources, ReadOnlySpan<CoveragePosedCommand> commands, CoveragePosedScratch scratch, out CoverageFailure failure) : bool` skins each original source once through its selected reference and builds current metadata using pinned frames.
- **L3b:** `CoverageReferencePosedConstructor.TryConstruct(CoverageRefinement topology, ReadOnlySpan<ModelVertex> posedHost, in Matrix4x4 model, ReadOnlySpan<CoveragePosedCommand> commands, CoveragePosedScratch scratch, Span<ModelVertex> final, out CoverageFailure failure) : bool`. Calls fixed interpolation, weighted finite-face reduction, ordered max gather, one transition application and final normal reconstruction. Separate new `CoverageReferenceWeightedFaces.cs` owns ratio/correction arithmetic.
- **L3c, public host diagnostics:** produce `public ReadOnlySpan<ModelVertex> LastConstructedHostVertices { get; }` and `public ulong DiagnosticGeneration { get; }` on `CoverageOracle`, with the diagnostic output contract below. The existing internal `LastConstructedVertices` remains an internal test observation, not the private consumer API.
- **L3c:** `CoverageOracle.TryCheckPosed(in CoveragePoseInput host, in Matrix4x4 model, ReadOnlySpan<CoverageCoveringPose> sources, ReadOnlySpan<CoveragePosedCommand> commands, CoveragePosedScratch scratch, out CoverageSampleEvidence evidence, out CoverageOracleFailure failure) : bool`. Produce `public ReadOnlySpan<ModelVertex> LastCoveringVertices(in CoverageCoveringPose source)` on `CoveragePosedScratch` for headless validation/export, using the complete diagnostic output contract below. An index-only getter is not part of the API. Certification independently examines actual final words and posed closed solids.
- **S4.5b:** extend `TemporalHistoryRejection` with `AddCoverage(in CoverageChangedBounds bounds, IGpuBuffer? state, uint transitionWordOffset)` and `ReserveCoverage(CoverageGpuBatch batch, int count, GpuRetireQueue retired)`. Preserve current `Add(MeshBounds?, MeshBounds?)`, `Select` and `Record`. Clear the shared target once in `Record`, draw CPU and conditional rectangles into the union, then bind that single texture to both resolve paths. A null state is unconditional. GPU state is read after Select with no CPU readback.
- **L4/L5, `Coverage/Gpu/CoverageGpuPosedLayout.cs`:** separate versioned layout and checked reservations for all source/current/previous/setup/face/queue rows. `CoverageGpuPosedPipeline.Record(IGpuCommandList commands, CoverageGpuBatch batch)` owns one batched source/setup/constraint/gather/select sequence, not one full sequence per support. The reviewed filter adapter consumes these finite pair rows, with an exact complete route retained.

The exact integer/scalar operation inventory for the new weighted graph is a proof deliverable of L3b before device transcription. This is a bounded numerical proof dependency, not permission to choose another conformation algorithm. A contradiction that changes approved behavior returns to root.

## Diagnostic output contract (L3c)

L3c owns the public sampled-geometry output contract. `CoverageOracle.LastConstructedHostVertices` returns a borrowed `ReadOnlySpan<ModelVertex>` over the oracle-owned published final row, in exactly `CoverageOracle.Topology.Vertices` order. A published row contains exactly `Topology.VertexCount` vertices, including material/shading aliases. Positions, normals and tangents are in host model space, before the supplied host `model` transform and any coverage/view projection. The private consumer uses the frozen model transform when comparing world-space geometry. No capacity tail or partial prefix is visible, and no mutable array, `Memory<ModelVertex>` or working-scratch view is public. Copy the existing final words, never independently re-skin them.

`CoverageOracle.DiagnosticGeneration` starts at zero. On entry to every check attempt, including both `TryCheck` overloads and `TryCheckPosed`, advance it without wrap, invalidate all prior borrowed views and clear the published host count before any argument validation, admission or construction. A thrown invalid-argument check therefore cannot leave the previous sample looking current. Generation exhaustion throws `InvalidOperationException` after invalidating outputs rather than reusing a generation. Construction must finish the complete finite candidate, including normals/attributes, before the host row is published. Publication precedes exact certification so a fully constructed geometric failure can be diagnosed.

| Last attempt state | Public final-host view | Verdict/evidence semantics |
| --- | --- | --- |
| No check yet, or an attempt is not fully constructed | Empty, count 0. | No candidate or positive evidence is implied. |
| Argument exception or admission, setup or construction refusal | Empty, count 0, never a prefix or previous candidate. | Preserve the existing exception or construction-stage refusal. Failed `TryCheck` evidence is default. |
| Complete construction followed by certification failure | Complete current host row, count `Topology.VertexCount`. | Return false, default success evidence and the certification-stage failure. Diagnostic geometry is not accepted geometry. |
| Complete construction and successful certification | Complete current host row, count `Topology.VertexCount`. | Existing full/partial-clearance certificate semantics still apply. A diagnostic view grants no additional authority. |

The S4.1 internal `BeginDiagnosticCheck`/`CheckGeneration` seam supplies this lifecycle before either independent branch exists. L3c exposes `DiagnosticGeneration` and wires the posed/public views, while S4.6 wires its later typed rigid-check entry through the same internal seam. L3c tests the existing legacy and posed entries without calling an absent S4.6 overload. S4.6 tests its typed entry through internal count/generation observations, and the final combined milestone covers all overloads. The oracle and its paired scratch remain single-caller and non-reentrant. A borrower must finish reading or copy the valid words before the next check attempt on that oracle. An older span is logically invalid after the generation changes even if its backing memory remains readable. Keeping the generation or a copied diagnostic row is not a certificate. These outputs are sampled diagnostics only, never drawing, GPU presentation or carry authority. The existing legacy check already clears its constructed count before validation. S4.1 centralizes that behavior with its generation, and L3c exposes it without changing construction, certificates or old LBS words.

`CoveragePosedScratch.LastCoveringVertices(in CoverageCoveringPose source)` returns the current published exact row for that full prepared incarnation. It is in retained `CoverageSourceTopology.Vertices` order across all mesh/material aliases, with exactly that source's vertex count, before `source.Model` placement and coverage/view projection. Skinned sources return their precisely skinned source-model `ModelVertex` words. Rigid sources return the admitted canonical source-model words, including rigid import's baked node placement. Apply the same frozen `source.Model` and pinned frame graph used by queries/display for world comparisons. No host recession, independent re-skinning, inverse reconstruction, convex proxy or ordinary DQ substitute is performed.

A posed check records the exact admitted token set and its store owner, support-set object/semantic identity, source key/index, equipment instance, slot, frame and view generations. The token must be an exact member of that last admitted input set, not merely a token with an equal source index or serializable key. Validate owner/set/source/instance and current store frame/view on every getter call. Check the current admitted diagnostic context first. With no such context or a superseded oracle/scratch generation, throw `InvalidOperationException` before inspecting the token. In a current admitted context, default or foreign owner/set/source/incarnation throws `ArgumentException`, an otherwise owned token with a stale store frame/view throws `InvalidOperationException`, and a current owned token absent from the exact admitted set throws `ArgumentException` naming `source`. A token made stale by `BeginFrame`, a superseded oracle/scratch diagnostic generation, or a call before any admitted posed input set throws `InvalidOperationException`. No lookup substitutes the first row with a matching source index.

Admission clears the prior input set before validating the new bounded set. If admission throws/refuses, no admitted set remains and covering lookup throws `InvalidOperationException`. Once the full set is admitted, a setup or construction refusal returns an empty span for its otherwise valid member tokens. Do not publish individual source prefixes. After complete host construction, atomically publish the full host and all covering diagnostic rows before certification. Success and later certification failure expose the complete current covering rows, while the latter still returns false/default success evidence. Oracle-owned host storage and scratch-owned published covering rows are private, cold-sized diagnostic buffers containing copies of the exact existing result words, not public mutable work arrays.

The covering row is borrowed until the next check attempt on the associated oracle, next reuse of that scratch by another oracle/setup, or `BeginFrame` on its source store, whichever comes first. Those events invalidate old tokens/views and clear or supersede the admitted input set, even after a failed subsequent check. The oracle's `DiagnosticGeneration` identifies the sample and the scratch tracks its own reuse generation. Borrowers must finish reading/copying before invalidation. Previously copied source keys/witness values stay serializable but cannot reacquire old rows. `.LastFailureWitness.Source` uses the same complete source key, and `.Input` distinguishes the specific frozen pose/placement. An early failure can expose a current source-attributed witness even while its diagnostic geometry bundle is unpublished. A generic admission failure with no valid source has no fabricated witness.

## Verification and execution protocol

Each runtime task follows its checkboxes below in order. Assertion excerpts use the named test fixture results described immediately above them, not new production helper APIs. Write the named failing test with the stated assertions, run its exact filter synchronously and retain the expected semantic failure, implement only that task, run green synchronously and commit explicit paths. A missing production symbol is an acceptable initial compiler red, followed by a behavioral negative once the symbol exists. Never weaken a comparison or regenerate legacy expectations. Each commit is followed by fresh Codex review before dependent work.

For a task's `Filter` below, the exact local command is `build-slot --label coverage-<task-id> -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "<Filter>"`. Replace the two angle-bracket values literally with that task ID and printed filter. Expected red is the named assertion/unsupported capability, expected green is exit 0 with every named case matched and no skips in the selected class. Attachments tests use their explicitly named project instead. No local test loops. No full suite per fix round.

GPU tasks add the printed native filter to the existing `cross-platform-gpu.yml` workflow's `renderTestFilter`, `legs=all`, `bake=false`, `tier=push`, at the exact pushed review SHA. The orchestrator must validate inputs against the current workflow and obtain run authorization before dispatch. Wait for final job exits synchronously at that task's verification gate, checking TRX names/counts, zero skipped new tests and each backend. A dispatch ID or a successful headless test does not complete a native task. Keep #1539's validation limitation separate from NC2 output-word results. No native runs occur during this planning assignment.

At each runtime milestone, merge current main only in the orchestrator's integration worktree, then run once through build-slot: Release solution build, full headless solution suite with `--filter "Category!=LiveSocket"` in the heavy lane, and whole-solution `dotnet format KhaozEngine.slnx --verify-no-changes --no-restore`. Run the five whole-tree/doc guards listed in J0 plus `git diff --check`. Never silently accept unrelated failures or hide them by a narrower milestone filter. Any new flake becomes an issue. Runtime version/release work belongs to the orchestrator's later finishing brief, not these docs commits.

## J0: Reconcile accepted source and history contracts

**Owner/dependency:** public engine orchestrator, after concrete-plan approval. No implementation task starts on the unreconciled snapshots.

**Files:** reconcile `Scene3D.SkinnedMeshLoading.cs`, `Scene3D.SkinningProfiles.cs`, `Scene3D.Coverage*.cs`, `Scene3D.SkinningHistoryRejection.cs`, `Internal/PreparedSkinningStore.cs`, `Models/`, `Rendering/TemporalHistoryRejection.cs` and accepted DQ tests/docs from `ad86b4a61`. Retain the coverage files at `8be362eec`. Use a new integration worktree, never another lane's active checkout.

**Interfaces:** preserve DQ-R1, immutable profile, prepared store tokens, all ordinary DQ consumers and separate precise coverage source/covering axes. The new amendment owns the S4 contract. Filter WIP is excluded.

- [ ] Record guards, exact parent SHAs, DQ-R1 `c4c8b48a2` ancestry and the merge conflict map before reconciliation. Create `docs/verification/2026-10-23-coverage-dq-integration.md` with the retained interfaces, accepted DB-1 to DB-6, source/proof manifest paths and exact integrated revision.

```csharp
Assert.Equal(uploadedWords, wordsAfterCallerMutation);
Assert.NotEqual(oldUploadIdentity, replacementUploadIdentity);
```

- [ ] Add a regression to `Render3D/DqCoverageIdentityTests.cs`, `Coverage_and_ordinary_consumers_share_the_retained_source_after_caller_mutation`. Mutate caller vertices, indices, binds and profile-bearing replacement after upload. Assert existing uploaded source words/bounds/history are unchanged for both LBS and DQ and a new upload gets a distinct identity. Red filter `FullyQualifiedName~DqCoverageIdentityTests` before fixing coverage reconciliation.
- [ ] Reconcile the commits in the owned integration worktree. Bind coverage material metadata to the DQ-R1 retained snapshot and keep covered DQ refused until S4.7 activation. Preserve ordinary DQ activation and all LBS behavior.
- [ ] Green filter `FullyQualifiedName~Dq|FullyQualifiedName~CoverageReferenceOutputTests|FullyQualifiedName~CoverageMotion`, then the once-only reconciliation build/full suite/format protocol. Run `sh scripts/check-dashes.sh --tree`, `sh scripts/check-prose.sh --tree`, `sh scripts/check-file-size.sh --tree`, `sh scripts/check-agent-instructions.sh --tree`, `bash scripts/check-doc-versions.sh` and `git diff --check`.
- [ ] Commit explicit reconciliation/test/doc paths as `coverage(integration): reconcile ordinary DQ and retained coverage sources`. Fresh review records the exact compatible SHA. Do not merge the filtered prototype or start timing here.

## S4: Rigid-DQ coverage

### S4.1: Source identity and strict version decoding

**Files:** create `Coverage/CoverageSkinningIdentity.cs` and `Coverage/CoverageSampleEvidence.cs` in Render3D, plus `Render3D/DqCoverageIdentityTests.cs` and `Render3D/CoverageEvidenceContractTests.cs` in Render.Tests. Modify `CoverageOracle.cs`, `CoverageOracleResult.cs`, `HostCoveragePart.cs`, `HostCoverageMap.cs`, `HostCoverageMapJson.cs`, `HostCoverageJsonPreflight.cs`, `CoverageRefinement.cs`, `CoverageSolidCertificate.cs`, and importer/upload binding in `Models/GltfLoader.Coverage.cs`/`Scene3D.CoverageUpload.cs`.

**Interfaces/dependency:** after J0, define the identity/pose register, shared digest/evidence values and cold identity-bearing oracle constructor above. These are S4.1 producers for L3a/L3c, not deferred S4.6 deliverables. Strict host map v2 adds `sourceGraph` as integer 1 or 2 and preserves explicit strengths/refinement/provenance. The v2 companion separately records `supportGraph` as integer 2 and each source graph as integer 1 or 2. Unknown IDs fail closed. Keep `KhaozEngine.HostCoverageMap/1\n` for v1 and use `/2\n` for v2 canonical hashing. Reject unknown versions/fields, duplicate fields, order-unsafe DQ aliases and mixed profiles before publication.

- [ ] Write `Legacy_LBS_hashes_are_unchanged`, `DQ_order_source_bind_or_remap_changes_stale_evidence` and `DQ_aliases_cannot_permute_ordered_influences`. Assert equal v1 semantic hashes and 144 manifest hashes, unequal new identities for every named mutation, and failed admission of stale metadata. An alias permutation that passed LBS must explicitly reject for DQ unless its ordered words match.

```csharp
Assert.Equal(legacySemanticHash, decodedV1SemanticHash);
Assert.Equal(144, frozenOutputHashes.Count);
Assert.False(mutatedIdentityCanReuseEvidence);
```

- [ ] Write `Digest_bytes_are_canonical_big_endian_and_do_not_overwrite_the_tail`, `Short_digest_destination_throws_before_writing` and `Identity_oracle_constructor_owns_and_validates_both_source_graphs` in `CoverageEvidenceContractTests`. Pin independent 32-byte digest words, exact byte order, unchanged tail, short-buffer refusal and null/mismatched topology/source/bind/remap/count exceptions. Construct both admitted profiles before any DQ evaluator exists, retaining the exact topology and legacy LBS behavior. Add `DQ_identity_cannot_enter_the_legacy_LBS_check` and `Check_entry_invalidates_before_arguments_are_validated`, asserting `NotSupportedException` without an LBS evaluation and generation/count invalidation even on argument errors.

```csharp
Assert.Equal(independentDigestBytes, serializedDigestBytes);
Assert.Equal(sentinelTail, destinationTail);
Assert.Same(topology, typedOracle.Topology);
```

- [ ] Red filter `FullyQualifiedName~DqCoverageIdentityTests|FullyQualifiedName~CoverageEvidenceContractTests|FullyQualifiedName~HostCoverageMapTests|FullyQualifiedName~CoverageReferenceOutputTests`.
- [ ] Implement the shared value types/canonical byte contract, cold identity-bearing constructor and strict map-v2 codec without changing old constructor/decoder paths. Allocate and own all constructor storage cold, with no dependency on later presentation or DQ source evaluation. Keep frame payload tokens/digests distinct from cold cache keys.
- [ ] Green same filter. Assert caller mutation and ordinary animation neither mutate cached source nor recreate static coverage assets.
- [ ] Commit `coverage(identity): version source graphs without changing LBS hashes`.

### S4.2: Precise reference and denominator proof

**Files:** create `Coverage/CoverageReferenceDualQuaternion.cs`, `Render3D/DqCoverageReferenceTests.cs`, `Fixtures/dual-quaternion/coverage-v1-words.json`, `docs/design/EQUIPMENT-COVERAGE-DQ-NUMERICAL-PROOF.md`. Modify `CoverageReferenceSkinning.cs`, `CoverageReferenceNormals.cs`, the kernel contract and Render.Tests project fixture inclusion.

**Interfaces/dependency:** after S4.1, implement registered `TrySkin`. Preserve the six ordered source steps in amendment section 5, including actual stage-3 Hamilton parentheses. The independent corpus derivation uses integer/high-precision arithmetic, not ordinary evaluation through a second wrapper.

- [ ] Write `DQ_reference_matches_independent_words`, `DQ_source_is_skinned_before_refinement`, `DQ_flush_identity_and_direction_branches_are_pinned`. Assert every attribute word, signed zero, branch/refusal and exact protected original. Include duplicate/ordered influences, negative and signed-zero running dots, identity/unweighted paths, all integer indices, weight totals at admitted limits, denominator boundary, normal length 1e-8 and tangent squared 1e-12 boundaries.

```csharp
Assert.Equal(independentlyDerivedWords, preciseWords);
Assert.False(skinRestThenRefineWords.SequenceEqual(refinePosedSourceWords));
Assert.Equal(expectedRefusal, actualRefusal);
```

- [ ] Red filter `FullyQualifiedName~DqCoverageReferenceTests|FullyQualifiedName~CoverageReferenceSkinningTests`.
- [ ] Transcribe through `[Binary32Kernel]` and `CoverageReferenceArithmetic`, including every copied-field read and inclusive F. Prove `n >= 1/8` for this graph with flushed weights and prepared-input errors before admitting it. Record exact constants/branch/literal inventory and corrected reciprocal/root dependencies. If the proof fails, stop for root ruling, never borrow ordinary `n > 0.249752` or change the floor silently.
- [ ] Green same filter plus unchanged `CoverageReferenceOutputTests`. Require independent derived word provenance and a witness where skinning interpolated rest weights differs from refining posed sources.
- [ ] Commit `coverage(reference): add precise rigid DQ source evaluation`.

### S4.3: Typed DQ compute source

**Files:** create `Coverage/Gpu/CoverageGpuDualQuaternionGlsl.cs`, `Render3D/DqCoverageKernelContractTests.cs`, `Gpu/DqCoverageKernelGpuTests.cs`. Modify `CoverageGpuSkinRefineGlsl.cs`, `CoverageGpuLayout.cs`, `CoverageGpuUpload.cs`, `CoverageGpuPipeline.cs`, `CoverageGpuResources.cs`, `Gpu/CoverageShaderSizeTests.cs`, `Gpu/ShaderCorpus.cs` and only the changed module rows in `Gpu/shader-corpus/`.

**Interfaces/dependency:** after S4.2, `CoverageGpuDualQuaternionGlsl.Source` implements the new graph. Add `CoverageGpuLayout.FrameWords(int boneCount, CoverageSourceGraph graph)` and graph-selected palette offset/stride helpers. Existing one-argument layout remains v1 LBS. Source graph dispatch precedes unchanged refinement.

- [ ] Write contract `DQ_stride_is_eight_and_LBS_layout_is_unchanged` and native `Every_DQ_source_word_and_refusal_matches_reference`. Assert exact eight-word packing, copied attributes/zeros, aliases and all S4.2 cases, including actual reciprocal correction and first failure.

```csharp
Assert.Equal(8, dqWordsPerJoint);
Assert.Equal(16, lbsWordsPerJoint);
Assert.Equal(referenceWords, nativeWords);
```

- [ ] Red local filter `FullyQualifiedName~DqCoverageKernelContractTests`, native filter `FullyQualifiedName~DqCoverageKernelGpuTests` when separately authorized.
- [ ] Implement typed upload/transcription with existing C3 integer residual workers and C5/C6 constant component/private-array access. Register every physical module in C8 checks. No approximate rcp/root or method switch inside an incorrectly typed matrix slot.
- [ ] Green local filter plus `FullyQualifiedName~CoverageShaderSizeTests|FullyQualifiedName~ShaderCorpus`. Obtain exact source-word/branch evidence on all three backends, cross-CPU prepared input words and changed-module artifact sizes under 30,000 inlined instructions/100,000 bytes. No hard-cap increase.
- [ ] Commit `coverage(compute): evaluate typed precise DQ sources`.

### S4.4: Precise, ordinary fallback and origin envelopes

**Files:** create `Coverage/CoverageDualQuaternionBounds.cs`, `Render3D/DqCoverageBoundsTests.cs`. Modify `CoverageActorBounds.cs`, `CoverageOrdinaryBounds.cs`, `CoverageSurfaceBounds.cs`, `Scene3D.CoveragePaletteBounds.cs`, `Scene3D.CoveragePrepare.cs` and the S4.2 proof.

**Interfaces/dependency:** after S4.2, implement registered `TryBound` using retained radius/prepared values with a separately derived precise proof. Preserve anchored relative bounds before outward absolute conversion. Expose no LBS convex-union fallback for DQ.

- [ ] Write `Precise_source_refinement_and_max_recession_are_enclosed`, `DQ_outside_LBS_union_is_retained`, `Unproved_or_overflowed_DQ_bounds_refuse`. Assert all independently evaluated precise/refined/final vertices lie inside bounds, at large origins and world scales, with maximum recession. Test source/model/clip intermediate overflow and ordinary CPU versus GPU W behavior separately.

```csharp
Assert.True(allPreciseRefinedAndFinalPositionsEnclosed);
Assert.False(unprovedEnvelopeAdmitted);
Assert.True(dqWitnessOutsideLbsUnion);
```

- [ ] Red filter `FullyQualifiedName~DqCoverageBoundsTests|FullyQualifiedName~CoverageActorBoundsTests|FullyQualifiedName~CoverageOrdinaryBoundsTests`.
- [ ] Prove outward inclusive-flush/refinement/placement/recession error, culling sphere, shadow retention and anchored origin admission. Expand by largest region budget, not sum of source caps. Route ordinary no-compute DQ through its ordinary proof plus projection enclosure. Nonfinite/unrepresentable bounds refuse transactionally.
- [ ] Green same filter. Add enclosure assertions to S4.5a native presentation tests, labelled sampled containment rather than a continuous certificate.
- [ ] Commit `coverage(bounds): enclose precise DQ and explicit fallback routes`.

### S4.5a: Selected final DQ presentation and motion seeding

**Files:** modify `Scene3D.CoverageUpload.cs`, `.CoverageGpu.cs`, `.CoverageSurfaces.cs`, `.CoverageOutcomes.cs`, `.CoverageShadows.cs`, `.CoverageOutlines.cs`, `.CoverageMotion.cs`, `CoverageMotionHistory.cs`, `CoverageSubmissionState.cs`, `Gpu/CoverageGpuActor.History.cs`, `Gpu/CoverageGpuSelectGlsl.cs`, `CoverageVerificationPose.cs`, `Scene3D.CoverageVerification.cs`, `SkinnedHostConformer.cs`. Create `Render3D/DqCoveragePresentationTests.cs`, `Gpu/DqCoveragePresentationGpuTests.cs`.

**Interfaces/dependency:** after S4.3/S4.4, selected current/previous leases retain source/graph identity. The existing conformer joint-world `TryPrepare` entry selects profile/graph from its retained identity and composes typed palette inputs before dispatch. GPU guard refusal copies current precise uncovered DQ source from scratch. No compute uses explicitly ordinary DQ. Both report uncovered. Freeze exact inputs for delayed verification.

- [ ] Write `Selected_final_is_never_reskinned`, `Guard_uses_precise_source_and_no_compute_stays_ordinary_DQ`, `Submitted_swaps_and_Abandoned_preserves_final_history`. Native assertions compare colour/depth/motion/all cascades/dynamic and static-transient point faces/independent outlines against selected buffer words, including protected aliases and origin rebase.

```csharp
Assert.Equal(selectedFinalWords, passInputWords);
Assert.Equal(lastPresentedFinalWords, motionPreviousWords);
Assert.False(uncoveredCountsAsCoverageSuccess);
```

- [ ] Red local filter `FullyQualifiedName~DqCoveragePresentationTests`, native `FullyQualifiedName~DqCoveragePresentationGpuTests`.
- [ ] Extend selection, restoration, verification and history correspondence. Preserve current selected-kind header checks and conditional current-position motion seeding. Invalid source/profile/palette fails before partial actor presentation. Ordinary motion does not reset coverage history.
- [ ] Green same filters with exact actual previous FINAL words, replacement/unequip/reset, zero-work, abandoned encode, guarded recovery and unrelated actor controls. All S4.4 native bounds assertions must hold.
- [ ] Commit `coverage(presentation): preserve selected DQ surfaces and history`.

### S4.5b: One temporal rejection owner

**Files:** modify `Rendering/TemporalHistoryRejection.cs`, `Scene3D.SkinningHistoryRejection.cs`, `Scene3D.CoverageMotion.cs`, `Rendering/CoverageTransitionGpu.cs`, `Internal/ShaderSources.HistoryRejection.cs` and both fused/split resolve bindings. Retire `CoverageTransitionMask` only after its projection/tests move. Extend `Render3D/DqHistoryRejectionTests.cs`, `Render3D/CoverageMotionProjectionTests.cs`, `Gpu/DqHistoryRejectionGpuTests.cs`, `Gpu/CoverageComputeHistoryGpuTests.Locality.cs`.

**Interfaces/dependency:** after S4.5a, implement registered generic `AddCoverage`/`ReserveCoverage`. Copy the anchored interval and near-plane uncertainty policy, not a conversion through a lossy absolute sphere. State word offsets remain explicit.

- [ ] Write `CPU_and_GPU_changes_union_without_a_second_clear`, `Gpu_only_transition_rejects_same_frame_without_readback`, `Old_current_and_reprojected_regions_leave_other_actor_history`. Assert a coverage guard transition and ordinary profile replacement together reject both actors' rectangles in fused and split resolve, while a third actor retains history.

```csharp
Assert.Equal(1, rejectionTargetClears);
Assert.True(ordinaryAndGpuConditionalRegionsBothRejected);
Assert.True(unrelatedActorRetainedHistory);
```

- [ ] Red local filter `FullyQualifiedName~DqHistoryRejectionTests|FullyQualifiedName~CoverageMotionProjectionTests`, native `FullyQualifiedName~DqHistoryRejectionGpuTests|FullyQualifiedName~CoverageComputeHistoryGpuTests`.
- [ ] Merge producers into one persistent R8 target and one zero texel, lifetime limited to first resolving view. Record clear once, then unconditional and GPU-conditional union draws after Select. Uncertain near-plane/nonfinite projection rejects the conservative affected region, whole view only where necessary. Do not conflate rejection with motion seeding or wait for readback.
- [ ] Green same filters at large origins, changed views, abandoned/submitted frames, no changes, both resolve modes and mixed ordinary/covered actors. Assert one full-size rejection texture and no global per-frame reset. Update only affected shader identity/layout tables with documented provenance.
- [ ] Commit `coverage(history): union ordinary and conditional coverage rejection`.

### S4.6: Rigid-support DQ oracle and fit identity

**Files:** modify `CoverageOracle.cs`, `CoverageOracleResult.cs`, `CoverageSolidCertificate.cs`, `HostCoveragePart.cs`, `Attachments.Render3D/SkinnedAttachmentBinding.cs`. Create `Render3D/DqCoverageOracleTests.cs`, `Fixtures/dual-quaternion/coverage-oracle-domain.json`.

**Interfaces/dependency:** after S4.2/S4.5a, implement the registered rigid-support `TryCheck` overload and exact sampled-domain digest, consuming the S4.1 evidence values and constructor. S4.6 does not define those shared contracts. `CoverageSolidCertificate` retains existing geometric authority and adds versioned identity where consumed. No old three-field certificate is treated as an authenticated DQ fit result.

- [ ] Write `DQ_sample_evidence_rejects_LBS_or_mutated_inputs`, `Whole_face_clearance_and_fuller_fit_failure_are_real`. Assert a positive complete domain with displaced and protected witnesses, and failures for an interior intersection with clear vertices, fuller DQ fit beyond cap, fold, rear intersection, changed influence order, source and pose. Assert `InputSha256` changes for each actual input mutation. Add `Typed_rigid_check_uses_the_shared_entry_invalidation_protocol`, asserting S4.1 internal generation/count behavior before invalid-argument, unsupported-input and construction/certification results, without depending on L3c public diagnostic members.

```csharp
Assert.True(validSampleAccepted);
Assert.NotEqual(originalDigest, changedInputDigest);
Assert.False(fullerFitOutsideCapAccepted);
```

- [ ] Red filter `FullyQualifiedName~DqCoverageOracleTests|FullyQualifiedName~CoverageOracleTests|FullyQualifiedName~CoverageOracleAllocationTests`.
- [ ] Dispatch the registered source graph before unchanged v1 construction/certification. Bind evidence to complete exact inputs and declared domain. Preserve zero warm oracle allocation, structured stage/code/command/count failure and legacy overload behavior.
- [ ] Green same filter plus frozen output controls. New valid domain passes and deliberate fit failures refuse without changing caps or tolerances. Finite samples remain labelled finite samples.
- [ ] Commit `coverage(oracle): bind exact DQ sampled fit evidence`.

### S4.7: Rigid-DQ milestone

**Files:** create `Gpu/DqCoverageMilestoneGpuTests.cs`, `docs/verification/2026-10-23-coverage-dq-milestone.md`. Update `docs/EQUIPMENT-COVERAGE.md`, `docs/USING-KHAOZENGINE.md`, Render3D and Attachments.Render3D READMEs, `docs/CROSS-PLATFORM.md`, source/kernel inventory and index.

**Interfaces/dependency:** after S4.1 to S4.6, expose covered DQ activation only when typed source, bounds, final surface and history paths are complete. This milestone supports rigid plates, not skinned leather or carry.

- [ ] Write `Rigid_DQ_milestone_requires_words_geometry_bounds_and_history` aggregating public positive/negative fixtures. Assert exactly 72 legacy cases, 144 legacy hashes, every new source/refusal word and all required native class counts, with zero skipped new tests.

```csharp
Assert.Equal(72, legacyCaseCount);
Assert.Equal(144, legacyHashCount);
Assert.Equal(0, skippedNewNativeCases);
```

- [ ] Run aggregate red native filter `FullyQualifiedName~DqCoverageMilestoneGpuTests` and local filter `FullyQualifiedName~DqCoverage`. If already green from completed tasks, retain evidence and make this an activation/docs task, without inventing a behavior change.
- [ ] Enable the gated rigid-DQ path, close only actual integration gaps and sweep living docs for method/source/fallback/certificate semantics. Keep private wardrobe and continuous carry explicitly pending.
- [ ] Run green native filter `FullyQualifiedName~DqCoverage|FullyQualifiedName~CoverageCompute`, milestone build/full headless/format/guards once and fresh whole-branch review. Record exact SHA, backend/compiler/prepared-input/artifact evidence and #1539 separately.
- [ ] Commit `coverage(milestone): gate and document rigid DQ coverage`.

## L: Compound skinned supports

### L1: Stance partition audit, archived failures and complete domain

**Owner/dependency:** the private preflight is planning input, not L1 completion. Public synthetic fixtures follow J0/S4.1. Private checks require a separately scoped private brief with actual files/commands and their own run authorization. They are not run by this planning assignment. L1 has three ordered evidence gates so full-domain validation does not circularly block the reference needed to perform it.

**Files:** public create `Render3D/CoveragePosedFixtureTests.cs`, `Fixtures/CoveragePosedFixtures.cs`. Private paths, named archive selectors and exact candidate identities stay in the private brief/report under [Grimhollow #547](https://github.com/APKiwiOrg/Grimhollow/issues/547).

**Interfaces:** public synthetic builders `NarrowStrip`, `HoleAndStacks`, `MixedRigidAndSkinned`, `ProtectedCrossing`, `IncompatibleDirections` create independent generic sources/maps. The private report supplies only abstract gate status and a private issue pointer to the public owner. Missing source-triangle ownership, welded-group/export provenance, actual inward ranges, associations, positive strengths or zero-strength boundaries is a blocking input failure. Bone labels, dominant influences, equal positions, component names and stored facing classes cannot fill those fields or exempt actual inward geometry.

- [ ] Write public `Fixtures_expose_positive_work_and_protected_witnesses` and `Source_count_cannot_pass_a_partition_with_duplicate_capacity_or_opposed_rates`. Cover LBS/DQ crossed with rigid/skinned supports, seams/material aliases, no-corner strip overlap, a wrap with opposing directions and support triangles duplicated across region associations. Assert positive baseline breakthrough, protected witnesses, explicit refusal of unsafe common directions and a per-command over-capacity partition even though source count passes.

```csharp
Assert.True(baselineBreakthrough > 0);
Assert.True(protectedWitnessCount > 0);
Assert.False(sourceCountPassesButDuplicatedRegionExceedsCapAccepted);
Assert.False(wholeWrappedComponentAssumedOneDirection);
```

- [ ] Red filter `FullyQualifiedName~CoveragePosedFixtureTests`. The private owner separately records a failing missing-map or partition test before any private audit implementation. Do not substitute a motion sweep for the first stance audit.
- [ ] Implement public builders and baseline measurements. **L1a, first private gate:** freeze one selected candidate in the private manifest, retain exact source/export vertex and face correspondence, and explicitly review proposed host source-triangle/welded-group ownership and four conditioned direction regions. Audit one stance only. Enumerate every actual inward face, finite overlap, material alias, shared zero-strength group and cross-region support duplicate. Check each command's 1,024 triangles and 256 true boundary edges after duplication, source count separately, and resulting fixed-refined host/collar counts against 65,536. Record minimum signed conditioned recession rate, zero-boundary deficits, required world recession at the 0.5 mm trial and collar locality. Whole wraparound or shoulder components are not presumed compatible with one direction. Missing provenance or a failed stance partition returns a concrete witness to root, with no limit/permission expansion. A stance pass is provisional permission to develop/test the reference and next bounded audit, not L1 acceptance.
- [ ] Green public filter. **L1b, after L3c is usable:** the private brief names each exact archived failure pose and frame pair from its retained preflight manifest, including protected contacts, inter-item cases, deep fit and genuine G-3 speed failures. Verify selected-candidate/archive identity before replay, never relabel archived geometry as current. Run those bounded witnesses before any complete sweep. Require exact direction/boundary/cap/collar and final finite-face results. Coverage cannot repair equipment collisions or forgive genuine G-3 speed failure. **L1c, after L1b:** check the complete admitted real sample/blend domain, positive work/protected bytes, posed solids and every per-command duplicated-capacity/refinement bound. Endpoint or named-witness passes never certify unsampled poses. Preliminary complete-domain L1c/L6 evidence binds an exact reviewed oracle revision and must pass before P1 where private geometry acceptance is required. Final L1c/L6 acceptance additionally requires selected caps/collar appearance and revalidation with the released precise oracle/profile before content admission. Preliminary success alone grants no final admission. The public report carries status only, no private counts/hashes/geometry.
- [ ] Commit public fixture paths as `test(coverage): add independent compound support fixtures`. Keep the private report/manifest and all measurements outside this repository. Record separate L1a/L1b/L1c states. Only provisional L1a is required to enter L2/L3. Complete L1 and L6 remain content-admission gates.

### L2: Strict v2 companions, compound binding and frozen staging

**Files:** create `Attachments/PosedEquipmentCoverageDefinition.cs`, `Attachments/PosedEquipmentCoverageJson.cs`, `Attachments/PosedEquipmentCoverageJsonPreflight.cs`, `Attachments.Render3D/LoadedPosedEquipmentCoverage.cs`, `Attachments.Render3D/AttachmentSetCache.PosedCoverage.cs`, `Coverage/CoveragePosedSupportSet.cs`, `Coverage/CoverageCoveringPoseStore.cs`. Modify host map v2 validation, `HostCoverageBinding.cs`, `SkinnedAttachmentBinding.cs`, `IAttachmentCoverageScene.cs`, `Scene3D.CoverageEquipment.cs`, `SkinnedHostConformer.cs`, `Scene3D.Coverage.cs`. Create `Attachments.Tests/PosedEquipmentCoverageJsonTests.cs`, `Render3D/CoveragePosedBindingTests.cs`.

**Interfaces/dependency:** after S4.1 and provisional L1a stance-partition acceptance, implement the registered BCL-only companion types and bound poses. Strict v2 uses `formatVersion = 2`, graph fields, compound source lists and domain `KhaozEngine.EquipmentCoverage/2\n`. Strict map-v2 validates each companion association against explicit region membership and disjoint position-group ownership. The companion owns the selected footprint association, the host map owns permission to move. Every source is opaque and preserves closed-solid/provenance obligations.

- [ ] Write `Legacy_companions_and_attachment_bytes_are_unchanged`, `Eight_sources_four_regions_and_compound_totals_are_enforced`, `Own_inverse_bind_and_remap_drive_garment_pose`, `Partial_or_stale_actor_does_not_publish`. Assert exact v1 hashes/attachment v1/v2 bytes, rejection at nine sources/five commands/over-limit aggregate triangles or edges, mismatch profiles, hidden/partial/dissolving source, disposed cache, stale generation/view and duplicate positive ownership. A within-tolerance but different inverse bind must produce a separately prepared payload. Add `Source_key_serialization_retains_set_source_and_equipment_incarnation`: same compiled set and source index zero in equipment instances 101 and 102 produce distinct stable keys, and each 76-byte serialization round-trip preserves its full fields with no tail overwrite or short-buffer partial write.

```csharp
Assert.False(nineSourcesAccepted);
Assert.False(fiveRegionsAccepted);
Assert.Equal(priorPublishedActor, actorAfterPartialStageFailure);
Assert.NotEqual(hostPreparedWords, differentBindGarmentPreparedWords);
```

- [ ] Red `build-slot --label coverage-l2-codec -- dotnet test KhaozEngine.Attachments.Tests/KhaozEngine.Attachments.Tests.csproj -c Release --filter FullyQualifiedName~PosedEquipmentCoverageJsonTests` and Render.Tests filter `FullyQualifiedName~CoveragePosedBindingTests`.
- [ ] Implement canonical strict codecs and cold transactional acquisition. Freeze retained source/remap/palette/placement/membership/visibility/transitions/frame/view together. Require all material slices before actor publication. Cache only immutable topology/identity. Share prepared values only for complete producing identity. Keep v1 rigid socket math and profile matching unchanged.
- [ ] Green both filters plus existing `EquipmentCoverageJsonTests` and `CoverageAttachmentBindingTests`. Assert failed load/stage leaves no leaked handle, partial actor, newly visible draw or mutated prior reservation.
- [ ] Commit `coverage(binding): stage bounded compound posed supports`.

### L3a: Exact posed support setup

**Files:** create `Coverage/CoveragePosedScratch.cs`, `Coverage/CoverageReferencePosedSupport.cs`, `Render3D/CoveragePosedSetupTests.cs`, `Fixtures/coverage-posed-v2/setup-words.json`. Modify the kernel inventory and `CoverageReferenceFrames.cs` through a version-selected path.

**Interfaces/dependency:** after S4.2/L2, with their S4.1 prerequisite already providing `CoverageInputDigest` and the shared value contracts, implement the registered scratch/setup. Each source is skinned once with its own graph, or rigidly transformed by the pinned reference frame graph. Posed triangles keep stable source order.

- [ ] Write `Posed_setup_matches_independent_words_and_is_actor_local`, `Degenerate_fold_or_nonpositive_rate_refuses`. Assert projected words, exact area sign/RN32 area, AABBs/slopes, source order and all geometry counts. Two actors sharing an asset at different poses must have different correct setup, without stale cold grid/area/boundary reuse. Include a folded/zero-area intermediate between valid endpoints and an uncleared zero-strength boundary.

```csharp
Assert.Equal(independentSetupWords, currentSetupWords);
Assert.NotEqual(actorASetupWords, actorBDifferentPoseSetupWords);
Assert.False(degenerateIntermediateAccepted);
```

- [ ] Red filter `FullyQualifiedName~CoveragePosedSetupTests`.
- [ ] Implement current metadata/reductions in preallocated scratch. Pin actual/nominal frame conversion, orientation, positive conditioned recession rate and clearance conversion per support range. Visit full bounded source-ordered lists with conservative current AABB rejection. Any face-side rejection must enclose its full allowed XY drift under maximum admitted recession and rounding, not only its unrecessed projected triangle, so movement cannot create an omitted final overlap. No dynamic grid or sample-derived candidate pruning.
- [ ] Green same filter with independent integer/high-precision setup words and first bad full source-incarnation/triangle attribution, including two equipped instances of one support set whose local source index is zero. Positive fixtures must prepare, not refuse everything.
- [ ] Commit `coverage(reference): derive exact current posed support metadata`.

### L3b: Weighted whole-face construction and rounded correction proof

**Files:** create `Coverage/CoverageReferenceWeightedFaces.cs`, `Coverage/CoverageReferencePosedConstructor.cs`, `Render3D/CoverageWeightedFaceTests.cs`, `Fixtures/coverage-posed-v2/construction-words.json`, `docs/design/EQUIPMENT-COVERAGE-POSED-CORRECTION-PROOF.md`. Reuse `CoverageFaceOverlay.cs`, `CoverageReferencePredicates.cs`, `CoverageReferenceNormals.cs` only where the contract matches, extending ordered incidence storage in `CoverageReferenceTopology.cs` for every constrained face, not only legacy core faces.

**Interfaces/dependency:** after L3a, implement registered constructor. Assign new v2-only failure codes in `CoverageFailure.cs` for unusable strength, protected positive deficit, conflicting direction and per-source cap, with source/triangle witness metadata in the posed scratch/result. Append values without changing any legacy numeric code or attribution order. Include the appended codes in GPU layout/guard mirrors. One immutable face amplitude reduces all applicable support constraints, then one incident-face maximum gather updates each owned group. Apply transition weight once. Preserve separate shading groups and canonical active position aliases.

- [ ] Write `Narrow_strip_with_no_covered_corner_recesses_the_complete_face`, `Holes_stay_open_and_innermost_stack_wins`, `Zero_strength_positive_deficit_refuses`, `Strictest_cap_wins_in_either_source_order`. Assert final full-face clearance on positive strips, no movement in irrelevant holes, exact protected zero-sign words, negative same-frame conflicts and equal accepted outputs under source-order changes where the sorted identity is identical. Include flushed-positive strength, nonfinite ratio, level-4 collar locality failure and plate-plus-strap cap conflict.

```csharp
Assert.True(finalCompleteFaceClearance >= authoredClearance);
Assert.Equal(protectedInputWords, protectedFinalWords);
Assert.False(strapCapExceededButPlateCapAvailableAccepted);
```

- [ ] Red filter `FullyQualifiedName~CoverageWeightedFaceTests`.
- [ ] Implement finite XY overlays and the section-4 deficit/strength maximum at overlay vertices. Pin every numerator/denominator/read/correction operation through reference arithmetic. Extend the correction proof to actual/nominal frame discrepancy, XY drift, positive rate, outward terms and rounded final words. A mathematical affine-ratio proof alone is insufficient. Refuse unusable rounded strengths, unsafe boundaries, cap excess and unsupported common directions without clamping. If the rounded proof cannot support the chosen graph, stop for root ruling before device work.
- [ ] Green same filter and independent construction corpus, including the strict full-face negative already wholly outside the garment. Require positive displacement and protected witnesses, immutable face reductions, no float scatter and all applicable final caps.
- [ ] Commit `coverage(reference): constrain finite faces with weighted recession`.

### L3c: Independent posed final-surface oracle

**Files:** create `Coverage/CoveragePosedCertification.cs`, `Render3D/CoveragePosedOracleTests.cs`, `Render3D/CoveragePosedDiagnosticTests.cs`. Modify `CoverageOracle.cs`, `CoverageOracleResult.cs`, `CoveragePosedScratch.cs`, `CoverageSolidCertificate.cs`, `CoverageCertification.cs` for shared predicates without routing v2 through v1 core containment.

**Interfaces/dependency:** after L3b, consuming the S4.1 shared evidence types/cold constructor and S4.2 source reference through L2/L3a, implement registered `TryCheckPosed`, public host diagnostics/generation, the nullable full-input `LastFailureWitness` property and `LastCoveringVertices(in CoverageCoveringPose source)`. L3c does not depend on S4.5 or S4.6. Exact checks recompute finite overlap from actual final host/support words and inspect posed closed solids, not construction's stored maxima.

- [ ] Write `Final_surface_oracle_catches_outside_skin_and_edge_crossings`, `Changed_skin_is_checked_against_all_equipment_and_host`, `Refusal_and_partial_transition_are_not_full_coverage`. Assert every full applicable face clears by authored C, including skin wholly outside the solid, plus folded/rear intersections, seam aliases and cross-region equipment. Missing/stale/duplicate/partial domain rows or source inputs must fail evidence completeness.

```csharp
Assert.False(whollyOutsideSkinAccepted);
Assert.False(partialDomainAccepted);
Assert.False(transitionalSampleClaimsFullClearance);
```

- [ ] Write `Host_diagnostic_view_clears_on_early_refusal_and_retains_certification_failure`, `Every_check_attempt_invalidates_prior_diagnostic_generation` and `Diagnostic_host_order_space_and_count_match_exact_constructor_words` in `CoveragePosedDiagnosticTests`. Require public read-only accessibility through reflection, exact refined topology/material-alias order and independent host-model words under a nonidentity model transform. Cover initial state, valid success, argument exception, admission/setup/construction failure after a previous success, and a complete candidate that fails certification. Assert empty on every early failure, complete current words after certification failure, false/default evidence on refusal and generation advancement/invalidation for the legacy and posed entries available to L3c. S4.6 owns the corresponding typed rigid-entry extension, so this test has no dependency on that later overload.

```csharp
Assert.Equal(topology.VertexCount, constructedHostWords.Length);
Assert.Equal(independentHostModelWords, constructedHostWords);
Assert.Equal(0, hostCountAfterEarlyFailure);
Assert.False(certificationFailureAccepted);
Assert.Equal(previousGeneration + 1, nextGeneration);
```

- [ ] Write `Two_equipped_instances_with_source_zero_return_distinct_exact_streams`, `Covering_getter_rejects_foreign_stale_or_unchecked_tokens` and `Covering_bundle_refusal_and_failure_witness_keep_full_incarnation` in `CoveragePosedDiagnosticTests`. Use equipment instances 101/102, one shared support set and local source index zero, with different prepared poses as well as different placements, so their source-model result bytes differ. First check a successful pair, then a fully constructed check that fails certification only for instance 102, and finally a refused next admission. Assert both token lookups return their own exact source-model words, frozen placement maps each to its distinct expected world surface, and the serialized failure witness names the failing instance/set/source instead of the other index-zero row. Within a current admitted context, pin default/foreign store/set/instance and same-frame unchecked tokens to `ArgumentException`, stale frame/view/oracle/scratch reuse and no admitted set to `InvalidOperationException`, empty rows after admitted setup/construction failure and full rows after certification failure. A failed next admission must not expose either previous row or witness.

```csharp
Assert.NotEqual(sourceA.SourceKey, sourceB.SourceKey);
Assert.False(expectedABytes.AsSpan().SequenceEqual(expectedBBytes));
Assert.True(expectedABytes.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(scratch.LastCoveringVertices(sourceA))));
Assert.True(expectedBBytes.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(scratch.LastCoveringVertices(sourceB))));
Assert.Equal(sourceB.SourceKey, failingSourceKey);
Assert.Equal(76, serializedSourceKeyBytes.Length);
```

- [ ] Red filter `FullyQualifiedName~CoveragePosedOracleTests|FullyQualifiedName~CoveragePosedDiagnosticTests|FullyQualifiedName~CoverageOracleAllocationTests`.
- [ ] Implement the public host view/generation and refusal/publication contract above, then independent exact geometry certification, strict complete input/domain identity and witness attribution through `CoveragePosedScratch.LastFailureWitness`, including the complete `CoverageCoveringSourceKey`, original source triangles, refined face, exact input digest, host source graph, world clearance and host-model location. The private caller joins its own pose/sample label to that digest. Expose unrecessed contacts separately as diagnostics. Keep protected contacts and actual stacked/closed-solid tests. Transitional weight below one is labelled transitional and never full success without its own certificate.
- [ ] Green same filter with valid complete positive samples, malicious no-op/refuse-all/broad-move controls and negative whole-face cases. Zero warm managed allocation applies to valid and refusal checks.
- [ ] Commit `coverage(oracle): certify final posed support clearance independently`.

### L4: Batched exact garment streams, setup and envelopes

**Files:** create `Coverage/Gpu/CoverageGpuPosedLayout.cs`, `Coverage/Gpu/CoverageGpuPosedSourcesGlsl.cs`, `Coverage/Gpu/CoverageGpuPosedSetupGlsl.cs`, `Coverage/Gpu/CoverageGpuPosedPipeline.cs`, `Render3D/CoveragePosedUploadTests.cs`, `Gpu/CoveragePosedSetupGpuTests.cs`. Modify batch/resources/upload, `Scene3D.CoverageGpu.cs`, `.CoverageEquipment.cs`, `.CoverageSurfaces.cs`, `.CoverageShadows.cs`, `.CoverageOutlines.cs`, `.CoverageVerification.cs`, and source bounds/history lease owners.

**Interfaces/dependency:** after S4.3/S4.4/L3c, define registered v2 layout/pipeline. Source/current/previous garment rows retain their own topology/attributes, with no host recession. Every pass and setup reads the same selected precise garment row.

- [ ] Write `Garment_query_and_every_display_pass_read_identical_source_words`, `Distinct_actor_pose_and_bind_cannot_share_setup`, `Composite_bounds_union_garments_without_summing_caps`. Assert exact LBS/DQ garment words against L3a, pass readbacks and motion previous FINAL values. Test two actors, independent hides/transitions, source replacement, conditioned world placement and large origins.

```csharp
Assert.Equal(queryGarmentWords, displayedGarmentWords);
Assert.Equal(presentedPreviousGarmentWords, garmentMotionPreviousWords);
Assert.True(compositeEnvelopeContainsEverySource);
```

- [ ] Red local filter `FullyQualifiedName~CoveragePosedUploadTests`, native `FullyQualifiedName~CoveragePosedSetupGpuTests`.
- [ ] Implement batched source skin/setup, ordered per-region reductions, checked cold reservation and precreated pull bindings. Extend precise bounds to each garment, union for composite rejection/visibility and retain host largest-recession expansion. Reject unproved envelopes/capacity before partial actor acceptance. Keep actual garment inverse binds/remap and delayed verification inputs frozen.
- [ ] Green same filters plus C8/shader-corpus controls. Require exact all-backend setup/display words, cross-CPU producers, coherent Submitted/Abandoned garment history and zero warm staging allocations. No native equality is substituted for L3c geometry proof.
- [ ] Commit `coverage(compute): share precise posed garment streams across consumers`.

### L5a: Exact v2 face work and total capacity

**Files:** create `Coverage/Gpu/CoverageGpuWeightedFacesGlsl.cs`, `Coverage/Gpu/CoverageGpuPosedCapacity.cs`, `Render3D/CoveragePosedCapacityTests.cs`, `Gpu/CoveragePosedFacesGpuTests.cs`. Modify v2 pipeline/layout, guard reductions, final normal gathering and actor selection, preserving physical module caps.

**Interfaces/dependency:** after L3c/L4. Physical query integration additionally waits for a reviewed filtered-exact commit and its compatibility review. Until that result exists, this task is blocked. Do not import its staged files. Once reviewed, record its full SHA, exact ABI, capability/fallback policy, worker seams, artifact evidence and tests in the J0 integration record before writing the adapter.

- [ ] Write `Exact_v2_words_and_first_failure_match_reference`, `Worst_case_reservations_are_complete_or_refused`, `Compound_sources_do_not_multiply_full_actor_chains`. Assert every source/setup/face/final/refusal word, positive constrained work, four/eight aggregate limits, checked overflow/device storage limits and all applicable caps. Full admitted candidate capacity is `sum(associated refined face count * selected support triangle count)` per command, checked in wide arithmetic with other frame rows included.

```csharp
Assert.Equal(referenceFinalAndFailureWords, exactDeviceFinalAndFailureWords);
Assert.True(reservedPairs >= admittedWorstCasePairs);
Assert.False(overflowingReservationPublished);
```

- [ ] Red local filter `FullyQualifiedName~CoveragePosedCapacityTests`, native `FullyQualifiedName~CoveragePosedFacesGpuTests`.
- [ ] Implement the exact complete v2 continuation/gather/select route using the reviewed physical worker seams. Reserve all admitted candidates or refuse transactionally with CapacityExceeded and counts before any partial result. Publish one whole-actor selected host and coherent garments, reuse ordered normal/tangent gathers, and record actual pass counts. Never emit a 36-dispatch chain per strap or silently discard a source.
- [ ] Green same filters, C8 sizes/constant indexing and shader identity checks. Exact controls cover full queues, overflow, first bad pair, reversed staging, aliases and strict strap cap under a looser plate cap. No timing claim.
- [ ] Commit `coverage(compute): construct exact compound finite-face coverage`.

### L5b: Requalify the reviewed filter for v2

**Files:** create `Coverage/Gpu/CoverageGpuPosedFilter.cs`, `Render3D/CoveragePosedFilterContractTests.cs`, `Gpu/CoveragePosedFilterGpuTests.cs`. Modify only the accepted filter modules named in L5a's pinned compatibility record and v2 pipeline registration. Keep exact workers outside physical light-filter modules.

**Interfaces/dependency:** after L5a and accepted external filter result. Adapter consumes v2 finite-pair rows and preserves stable source order, whole-face/independent-pair ownership and ordered replay. Devices lacking the proved capability/domain use L5a exact execution.

- [ ] Write `Filtered_v2_empty_sparse_and_full_queues_equal_exact`, `Ambiguous_dynamic_boundaries_always_replay_exact`, `Speculative_later_failure_cannot_replace_first_ordered_failure`. Assert all final/setup/refusal words against exact control on near-boundary, zero/negative strength, dynamic area, folded, subnormal and midpoint cases. Include a full queue at capacity and a forced unsupported capability path.

```csharp
Assert.Equal(exactWords, filteredEmptyQueueWords);
Assert.Equal(exactWords, filteredSparseQueueWords);
Assert.Equal(exactWords, filteredFullQueueWords);
Assert.Equal(firstOrderedFailure, publishedFailure);
```

- [ ] Red local filter `FullyQualifiedName~CoveragePosedFilterContractTests`, native `FullyQualifiedName~CoveragePosedFilterGpuTests`.
- [ ] Reprove every v2 filter certificate over its actual dynamic input and weighted ratio/correction graph. Queue ambiguity, never approximate acceptance. Retain exact complete capacity, checked offsets, deterministic replay, no CPU readback and no discarded constraints. Rejection/queue policy follows the reviewed capability result, not observed native sample agreement.
- [ ] Green same filters on all backends with exact fallbacks where required. Audit physical light modules for absence of heavy exact workers and every new module for C3/C5/C6/C8. Existing v1 word corpora stay unchanged. An exact-only v2 result is correctly labelled and books no filter gain.
- [ ] Commit `coverage(filter): requalify exact replay for posed finite constraints`.

### L6: Private LB-1 checker handoff

**Owner/dependency:** private game owner, after provisional L1a/L3c and usable reviewed engine APIs. Named archived L1b precedes complete-domain checking. Preliminary L1c/L6 complete-domain evidence binds the exact reviewed oracle revision and precedes P1. Final L1c/L6 acceptance additionally waits for accepted caps/appearance and released-pin revalidation. This is a handoff contract, not permission to edit the game from this public plan. G-3 remains owned by its existing task.

**Files:** the private owner names its concrete checker, sampler and test paths in a separate private implementation brief under [Grimhollow #547](https://github.com/APKiwiOrg/Grimhollow/issues/547). Do not put those payloads or paths in engine fixtures. The required reviewed headless oracle contracts are complete before this handoff. Independent presentation milestones need not precede preliminary checker integration.

**Interfaces:** consume `CoverageOracle.TryCheckPosed`, `CoverageOracle.LastConstructedHostVertices` and `DiagnosticGeneration`, exact evidence identities, `CoveragePosedScratch.LastCoveringVertices(in CoverageCoveringPose source)` for each exact admitted token and the accepted game sampler/G-3 result. Check the current generation and exact valid host count before reading. Use the final host model-space row with the frozen host model transform, without reconstructing/re-skinning it. Early refusal has no host output. Covering output follows the admitted-set/token/generation rules above. Pair source-model words with that token's frozen placement and export its full `SourceKey` with any failure witness. Certification-failed output is diagnostic and remains a failed sample. New LB-1 result format separates unrecessed contact diagnostics from final full-face acceptance and preserves historical R2/M1/plate results.

- [ ] Private owner writes red tests preserving genuine G-3 speed failures even after host recession, complete domain accounting, stale/duplicate/omitted rows, final face already outside the garment, protected source words, every join/pin anchor and interval witness. Assert all required samples/blends represented, stance 1.5 to 4 mm against unrecessed selected body, stacks 0 to 2 mm against actual underlying leather, joins/slab pins at most 0.5 mm and popping 0.35 m/s with accepted near-key displacement versus frame-speed classification.
- [ ] Private owner runs those tests synchronously in its own worktree through build-slot and records semantic red evidence. No native/paid run is inferred or started here.
- [ ] Private owner implements coverage-aware checks using exact engine-selected profiles/poses/words and matching posed barycentric anchors. Preserve G-2 fixed unrecessed-body/slab references, complete topology-derived join membership, protected-contact rules and equipment-to-equipment/plate gates. DQ opt-in requires actual Preserve Volume/export comparison and complete remeasurement, never a Python counterfactual certificate.
- [ ] Preliminary L1c/L6 green checks the complete declared geometric domain at the exact reviewed oracle revision, including measured total recession against every explicitly declared candidate cap, positive representative construction, protected witnesses and every retained stance/stack/join/popping/G-3/inter-item gate. Final caps/appearance remain pending, not passed. Final L1c/L6 green revalidates against the released precise oracle/profile and owner-accepted caps/collar locality/continuity/shading limits. A changed cap, source or profile invalidates affected preliminary evidence and requires the relevant geometry/cost gates again. No cap is inferred, raised to hide a miss or granted by a diagnostic result. Refusal/no-compute/partial/full-domain holes fail both phases. Original 4 mm stance ceiling does not apply to final recessed skin. Keep all artifacts private. Existing genuine G-3 speed failures remain blocking independently of otherwise successful conformation. A diagnostic continuity fix, a fixed reference moved with recession or a different candidate's archive cannot relabel them as passes.
- [ ] Private owner commits its own exact files and returns a reviewed SHA/verdict through the existing issue. A separate private plan must supply actual file names/commands before that implementation. This public plan cannot execute L6 by guessing them.

### L7: Combined native scene and history milestone

**Files:** create `Gpu/CoveragePosedMilestoneGpuTests.cs`, `Render3D/CoveragePosedAllocationTests.cs`, `docs/verification/2026-10-23-coverage-posed-milestone.md`. Update living coverage/API/package docs, kernel inventory and index.

**Interfaces/dependency:** after S4.7/L5b. This is synthetic engine functionality acceptance, with private L6, P1 and A1 separately pending.

- [ ] Write `Combined_LBS_DQ_rigid_skinned_scene_preserves_one_surface`, `Warm_staging_resolution_and_oracle_allocate_zero`. Assert 32 actors sharing assets across different poses, four commands/eight sources, exact host/support/pass words, rigid socket control, positive conformation and exact protected bytes.

```csharp
Assert.Equal(32, actorCount);
Assert.Equal(0, warmManagedBytes);
Assert.Equal(querySupportWords, allPassSupportWords);
```

- [ ] Red local filter `FullyQualifiedName~CoveragePosedAllocationTests`, native `FullyQualifiedName~CoveragePosedMilestoneGpuTests`.
- [ ] Complete scene integration only. Exercise dynamic supports, mixed plate/strap caps, source/profile/membership replacement, hidden/partial garments, guard/refusal/recovery, CPU/no-compute fallbacks, synchronized events, origin rebase and Submitted/Abandoned. Preserve one rejection union and unrelated ordinary actor history. No new render pass.
- [ ] Green native filter `FullyQualifiedName~CoveragePosed|FullyQualifiedName~DqCoverage|FullyQualifiedName~CoverageCompute`, exact counts/no new skips and milestone build/full suite/format/guards once. Fresh whole-branch review audits words, geometry, bounds, allocations, queues/artifacts and #1539 limitation independently. Zero cost-success inference.
- [ ] Commit `coverage(milestone): verify and document compound posed supports`.

## P1 and A1: Bounded downstream gates

These are dependency handoffs, not executable runtime tasks or permission to reuse another lane's authorization.

**P1, combined feasibility/cost owner.** Entry requires accepted filtered integration, L7, complete preliminary L1c/L6 private geometry evidence tied to the exact reviewed oracle revision and a separately reviewed stage-5 plan. PL-1 does not require final released-pin admission before P1, but a failed preliminary domain or retained G-3 gate still blocks this entry. That plan names new continuous host-plus-garment enclosures, conversion/hemisphere branches, positive denominator/projection-rate bounds, folds, actual final geometry, certified bootstrap/alternatives, expiry and worst-frame refresh scheduling. No LBS twelve-coefficient/translation-only certificate transfers. First obtain explicit authorization naming hardware/workload/iterations/duration before any repeated or paid run. Measure total CPU/allocations and completed-frame GPU cost, not resolution alone, at the retained 32 actors and 15 percent ceiling with cold equip and synchronized attacks. Include actual candidate scanning, every setup/source/filter/exact queue/gather/history phase, bytes and dispatch count. A failed cell or hard budget gate returns measured content/scheduling/budget choices to the owner. No averaging across frames or actors waives a worst-frame failure.

Historical measurement is approximately 11.634 ms resolution before the accepted register change and 10.073 ms after, at 32 T4 actors. It is not a full-frame or leather measurement. The earlier filter study's 3.5 to 6.5 ms F0 scenario and the amendment report's 12.6 to 13.6 ms exact-v2 and about 4.5 to 9.3 ms empty-queue scenarios are estimates under stated assumptions. Nonempty queue latency, real v2 pair counts, one-actor latency and combined costs are unmeasured. None passes the roughly 1.2 ms aggregate feasibility target. Do not multiply prototype gains.

**A1, release and private adoption owners.** The public capability release packet requires S4.7/L7 and P1 hard-budget/continuous-domain acceptance where claimed. It does not declare private garment acceptance. The engine-rooted release owner handles exact tag approval and release separately. Final game content admission additionally requires L1b/L1c and L6 revalidated against that released precise oracle/profile, with owner-approved numerical caps/collar/appearance from matched renders. The game adopts only that released pin through its own worktree/catalog/oracle workflow, then owner look/playtest and real-device conformance. No public synthetic pass closes private garment fit. If any prerequisite fails, adoption stays blocked and the failing witness returns to the owning task or owner ruling. No release, art or playtest occurs from this documentation assignment.

## Milestone dependency and acceptance checklist

- [ ] **J0 ready:** reviewed compatible coverage/DQ revision, DQ-R1 preserved, ordinary DQ and legacy LBS retained, no filter WIP imported.
- [ ] **Rigid DQ ready:** S4.1 to S4.7, denominator/bounds proofs, exact native words/all-pass history and frozen LBS controls. This is an independently useful milestone and excludes leather/carry.
- [ ] **Reference v2 ready:** S4.1 shared value contracts and cold identity-bearing constructor, S4.2 precise source reference, provisional L1a stance-partition acceptance, L2, L3a to L3c with current posed metadata, rounded weighted correction proof, independent final geometry and public host and full-incarnation covering diagnostic count/space/lifetime/refusal tests, including two equipped instances using local source index zero and serializable witness identity. These reference tests do not call S4.6's later rigid overload. No count-only feasibility verdict.
- [ ] **Synthetic v2 ready:** L4, reviewed external filter pin, L5a/L5b, L7 with exact/fallback queues, coherent displayed garments, one history union and zero warm allocation.
- [ ] **Preliminary private geometry ready:** L1a, named archived L1b and complete preliminary L1c/L6 at the exact reviewed oracle revision, including all retained geometric/stance/stack/join/popping/G-3 gates at the explicitly declared candidate caps, with final appearance still pending. This state precedes P1 where private geometry acceptance is required and does not require or grant final released-pin admission.
- [ ] **Capability release packet ready:** S4.7/L7, the reviewed filtered result and P1 acceptance where claimed, followed by the engine-rooted owner's separate exact-tag authorization. No private content acceptance follows from release alone.
- [ ] **Final private admission ready:** preliminary L1c/L6 evidence revalidated against the released precise oracle/profile, P1, selected appearance/caps and A1 owner look/playtest/conformance. No runtime checkbox is marked complete by the planning commit.

Self-review follows the writing-plans checklist. Its section-by-section matrix, source snapshots, decisions and actual docs-only verification exits are recorded in the planning report beside the orchestrator brief. It is not a runtime completion certificate.
