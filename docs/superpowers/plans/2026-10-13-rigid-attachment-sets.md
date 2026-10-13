# Rigid Attachment Sets Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `subagent-driven-development` (recommended) or `executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Load, bind and render an authored set of independently joint-bound rigid parts with safe ownership and stable motion identity.

**Architecture:** A BCL-only Attachments package serves headless validation. Its separate Render3D adapter
uses existing joint maps, importers and draw descriptors. One scene-scoped cache owns uploads and bound
views borrow them.

**Tech Stack:** C#, the repository's .NET SDK, System.Text.Json, System.Numerics, existing Render3D and xUnit.

**Spec:** [Rigid attachment sets](../../design/RIGID-ATTACHMENT-SETS-DESIGN-2026-10-13.md).

## Global Constraints

- Plans await review. Product work starts only after plan approval and execution-method selection.
- Work in the engine task worktree. Preserve other work and follow current `AGENTS.md` and contributor rules.
- No game, inventory, equipment-slot, skill or particular skeleton type enters the engine API.
- Root JSON fields are `formatVersion` and `parts`. Part fields are `id`, `mesh`, `joint` and `local`.
  Mesh fields are `key` and `sha256`. Format version is 1. Part IDs are positive `uint` values. Local matrices contain 16 row-major finite
  values, are affine and invertible. Asset hashes are 64 lowercase hexadecimal SHA-256 characters.
- Keys are relative forward-slash paths. Refuse rooted paths, backslashes, dot/empty segments and URIs.
  The file source refuses symlink/reparse-point traversal. Part GLBs are embedded, rigid geometry only.
- Preserve Render3D's current dependency restrictions. No new third-party dependency and no Foundation
  or Server umbrella expansion. Game3D gains the renderer arm.
- Use `build-slot` for builds/tests/captures, `--heavy` for full suites/captures/pack proofs. No test loops,
  local stress or automatic release tag. A staged version is ridden after checking current main and tags.
- Every task runs its focused red/green check. Run the full Release build/suite once at final integration.
- All new APIs below are planned APIs. Existing API references were checked at engine commit `10072b404`.

## Review Focus

1. A same-length palette from another skeleton must fail, including a skin order different from node order (E2).
2. A part with two materials must produce two distinct temporal keys and retain both meshes (E4).
3. A later upload failure must release only newly acquired resources, including textures, and leave cached assets usable (E3).
4. A glTF node translation must be applied once through the final renderer, with independent reference pixels (E2, E4).
5. Reordered definitions, re-equipment and cache disposal must not leave stale motion, partial draws or live resources (E3, E4).

## API contract shared by the tasks

These are signature sketches, with method bodies omitted. They are not source files to paste verbatim.

CPU namespace `KhaozEngine.Attachments`:

```csharp
public readonly record struct AttachmentMeshRef(string Key, string Sha256);
public sealed record RigidAttachmentPartDefinition(
    uint Id, AttachmentMeshRef Mesh, string Joint, Matrix4x4 Local);
public sealed class RigidAttachmentSetDefinition
{
    public RigidAttachmentSetDefinition(IEnumerable<RigidAttachmentPartDefinition> parts);
    public IReadOnlyList<RigidAttachmentPartDefinition> Parts { get; } // copied, ascending Id
    public IReadOnlyList<AttachmentMeshRef> Meshes { get; } // distinct, ordinal key order
    public string SemanticSha256 { get; }
    public void ValidateReferences(IReadOnlySet<string> joints,
        IReadOnlyDictionary<string, string> meshSha256);
}
public static class RigidAttachmentSetJson
{
    public const int CurrentFormatVersion = 1;
    public static RigidAttachmentSetDefinition Parse(ReadOnlyMemory<byte> utf8, string sourceName);
    public static byte[] Write(RigidAttachmentSetDefinition definition);
}
public interface IAttachmentAssetSource { ReadOnlyMemory<byte> Read(string key); }
public sealed class DirectoryAttachmentAssetSource : IAttachmentAssetSource
{
    public DirectoryAttachmentAssetSource(string root);
    public ReadOnlyMemory<byte> Read(string key);
}
public static class AttachmentGlb
{
    public static void ValidateRigidEmbedded(ReadOnlyMemory<byte> glb, string sourceName);
}
```

Renderer namespace `KhaozEngine.Attachments.Render3D`:

```csharp
public interface IAttachmentMeshLease : IDisposable
{
    IReadOnlyList<MeshHandle> Handles { get; }
}
public interface IRigidAttachmentScene
{
    IAttachmentMeshLease UploadAttachment(IReadOnlyList<GltfMeshPart> parts);
    void DrawMesh(in RigidInstanceDraw draw);
}
public sealed class Scene3DAttachmentScene : IRigidAttachmentScene
{
    public Scene3DAttachmentScene(Scene3D scene);
    public IAttachmentMeshLease UploadAttachment(IReadOnlyList<GltfMeshPart> parts);
    public void DrawMesh(in RigidInstanceDraw draw);
}
public sealed class RigidAttachmentBinding
{
    public RigidAttachmentBinding(RigidAttachmentSetDefinition definition, ContractJointMap map);
    public Skeleton Skeleton { get; }
    public int PartCount { get; }
    public void ComposeWorlds(Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette,
        in Matrix4x4 model, Span<Matrix4x4> destination);
}
public sealed class RigidAttachmentSetCache : IDisposable
{
    public RigidAttachmentSetCache(Scene3D scene, IAttachmentAssetSource source);
    public RigidAttachmentSetCache(IRigidAttachmentScene scene, IAttachmentAssetSource source);
    public LoadedRigidAttachmentSet Get(string key, string expectedSha256);
    public void Dispose();
}
public sealed class LoadedRigidAttachmentSet
{
    public RigidAttachmentSetDefinition Definition { get; }
    public string DefinitionSha256 { get; } // exact source bytes
    public BoundRigidAttachmentSet Bind(ContractJointMap map);
}
public sealed class BoundRigidAttachmentSet
{
    public Skeleton Skeleton { get; }
    public int DrawCount { get; }
    public int WriteDraws(Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette,
        in Matrix4x4 model, MotionKey instanceKey, in RigidInstanceDraw template,
        Span<RigidInstanceDraw> destination);
    public void Queue(Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette,
        in Matrix4x4 model, MotionKey instanceKey);
    public void Queue(Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette,
        in Matrix4x4 model, MotionKey instanceKey, in RigidInstanceDraw template);
}
```

Invalid definitions/references report `InvalidDataException` with source/part/reference context. Invalid
programmatic constructor arguments, wrong skeleton/palette and invalid pose matrices report
`ArgumentException`. Disposed cache-backed views report `ObjectDisposedException`. Neither definitions
nor loaded/bound views expose mutable backing collections or a public resource-disposal operation.

### E1. GPU-free definitions, JSON and asset-source policy

**Files:** Create `KhaozEngine.Attachments/{KhaozEngine.Attachments.csproj,README.md,RigidAttachmentSetDefinition.cs,RigidAttachmentSetJson.cs,AttachmentAssets.cs,AttachmentGlb.cs}` and
`KhaozEngine.Attachments.Tests/{KhaozEngine.Attachments.Tests.csproj,DefinitionTests.cs,JsonTests.cs,AssetSourceTests.cs,GlbPolicyTests.cs}`.
Modify `KhaozEngine.slnx`, root `README.md`, `docs/DEPENDENCY-SEAMS.md` and `KhaozEngine.Tests/ArchitectureTests.cs`.

**Interfaces:** Produces every CPU signature above. `AttachmentAssets.cs` owns the source interface,
directory implementation and one internal key validator. Follow SegmentRig's BCL-only package shape and
the standard narrow xUnit project pattern. Test namespaces stay under `KhaozEngine.Tests.Attachments`.

- [ ] **Write the failing tests.** Use fixtures with IDs 1 and 2, one repeated mesh key and a 16-number
  identity matrix. Pin these assertions:

  ```csharp
  Assert.Equal(first.SemanticSha256, reordered.SemanticSha256);
  Assert.Single(first.Meshes);
  Assert.Equal(2, first.Parts.Count); // still 2 after clearing the input List
  Assert.Throws<InvalidDataException>(() => ParseCase("duplicate-id"));
  Assert.Throws<InvalidDataException>(() => ParseCase("unknown-property"));
  Assert.Throws<InvalidDataException>(() => ParseCase("non-affine-matrix"));
  ```

  Define test-local `ParseCase(string name)` fixtures for duplicate JSON properties, version 2, ID 0,
  missing keys, malformed hashes, NaN/overflow, singular matrices and each rejected path form. Add
  filesystem cases for missing files and reparse-point traversal. GLB cases cover truncated/overflowed
  chunk lengths, URI-bearing buffers/images, skin data and a valid embedded rigid container.
- [ ] **Run red.** `build-slot --label attachments-core -- dotnet test KhaozEngine.Attachments.Tests/KhaozEngine.Attachments.Tests.csproj -c Release`.
  New API declarations may first cause compilation failures. After declarations exist, observe the
  behavioural assertions fail before implementing their logic.
- [ ] **Implement the CPU contract.** Copy and sort parts, reject conflicting hashes for one key, and make
  canonical JSON the input to semantic SHA-256. Use `JsonDocument.Parse(ReadOnlyMemory<byte>, options)`
  with disposal and owned values. Detect duplicate members explicitly. Validate GLB envelopes/URI policy
  before a renderer can read them. Source reads return a fresh byte snapshot. Add a zero-project-reference
  architecture assertion for Attachments and its catalog/readme/solution entries. No umbrella change here.
- [ ] **Run green.** Repeat the focused command. Expect all new CPU cases to pass and no graphics dependency
  in the package. Run `bash scripts/check-doc-versions.sh` for package registration.
- [ ] **Commit explicit paths.** `feat(attachments): add GPU-free attachment definitions`.

### E2. Bind posed skeletons and import material parts from immutable bytes

**Files:** Create `KhaozEngine.Attachments.Render3D/{KhaozEngine.Attachments.Render3D.csproj,README.md,RigidAttachmentBinding.cs}` and
`KhaozEngine.Render.Tests/Render3D/{RigidAttachmentBindingTests.cs,RigidAttachmentTestRig.cs,GltfMaterialPartsMemoryTests.cs}`.
Create `KhaozEngine.Render3D/Models/GltfLoader.MaterialParts.cs`. Modify the original GltfLoader file for the
cohesive extraction only, `KhaozEngine.Render3D/README.md`, `KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj`,
`KhaozEngine.Game3D/{KhaozEngine.Game3D.csproj,README.md}`, `KhaozEngine.slnx`, root `README.md`,
`KhaozEngine.Tests/ArchitectureTests.cs` and `docs/DEPENDENCY-SEAMS.md`.

**Interfaces:** Consumes E1 definitions. Produces `RigidAttachmentBinding` above and this additive importer:
`GltfLoader.LoadPartsWithMaterials(ReadOnlyMemory<byte> glb, string identity) : IReadOnlyList<GltfMeshPart>`.
The renderer arm references only Attachments and Render3D. Add it to Game3D and its exact membership lock.

- [ ] **Write the failing tests.** `RigidAttachmentTestRig` constructs root/hip/knee nodes, with skin order
  `[knee, hip]`. At rest the hip is `(0,2,0)` and knee `(0,1,0)`. With local offset `(1,0,0)` on the hip and
  character translation `(10,0,0)`, assert the literal point `(11,2,0)`. Rotating the hip 90 degrees about Z
  gives `(10,3,0)` for that point and `(11,2,0)` for the knee origin. Also test a turned rest joint,
  reflected/scaled joint basis, insufficient output space, a same-size foreign skeleton and an invalid
  later joint. Adapt the private two-material GLB fixture from `GltfMultiMaterialPartsTests` into the new
  test fixture. Assert path and memory import have equal material order, geometry and maps, including a
  baked node translation. Feed malformed indices to preserve existing importer diagnostics.
  Name and pin these cases with array-backed output buffers:

  ```csharp
  // Reordered_skin_uses_hip_bone_not_node_index
  Assert.Equal(new Vector3(11, 2, 0), Vector3.Transform(Vector3.Zero, worlds[0]));
  // Rotated_hip_moves_its_part_and_child
  Assert.Equal(new Vector3(10, 3, 0), Vector3.Transform(Vector3.Zero, rotated[0]));
  Assert.Equal(new Vector3(11, 2, 0), Vector3.Transform(Vector3.Zero, rotated[1]));
  // Foreign_skeleton_with_equal_count_is_refused
  Assert.Throws<ArgumentException>(() => binding.ComposeWorlds(other, palette, model, worlds));
  ```
- [ ] **Run red.** `build-slot --label attachments-bind -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~RigidAttachmentBindingTests|FullyQualifiedName~GltfMaterialPartsMemoryTests'`.
- [ ] **Implement binding and the overload.** Resolve `map.Node`, `map.Bone` and `map.BodyAlignment` once.
  Reject a frame unless `ReferenceEquals(skeleton, map.Skeleton)` and palette length matches the skin.
  Compose `BoneSocket.ComposeRigid(part.Local * alignment, palette[bone], model)` into canonical part
  order. Validate model/joint matrices and capacity. Move the existing material-part entry point,
  BuildParts and its material-corner helper into the new cohesive partial before adding the memory overload.
  Reuse `LoadModel(glb, identity)` and BuildParts without forking decoding or growing the large file.
  Attachment callers apply E1's embedded policy first.
  Keep the base Render3D dependency allowlist unchanged.
- [ ] **Run green.** Run the focused command plus existing `BoneSocketTests`, `ContractJointMapTests` and
  `GltfLoaderNodeTransformTests`. Expect the literal point, byte-import and negative cases to pass.
- [ ] **Commit explicit paths.** `feat(attachments): bind rigid parts to posed skeletons`.

### E3. Transactional loading, caching and resource lifetime

**Files:** Create `KhaozEngine.Attachments.Render3D/{RigidAttachmentSetCache.cs,LoadedRigidAttachmentSet.cs,BoundRigidAttachmentSet.cs,AttachmentUpload.cs,IRigidAttachmentScene.cs,Scene3DAttachmentScene.cs}`,
`KhaozEngine.Render3D/Scene3D.SurfaceMapUpload.cs` and
`KhaozEngine.Render.Tests/Render3D/{RigidAttachmentAssetTests.cs,SurfaceMapUploadFailureTests.cs}`.
Modify `KhaozEngine.Render3D/{Scene3D.cs,README.md}` for surface-map delegation and documentation,
`KhaozEngine.Render.Tests/Gpu/FakeGpuDevice.cs` for deterministic failure injection, and
`KhaozEngine.TileWorld.Render3D/{ITileWorldScene.cs,KhaozEngine.TileWorld.Render3D.csproj,README.md}`
plus root dependency documentation for the optional attachment capability.

**Interfaces:** Consumes E1 sources/definition and E2 importer. Produces cache/loaded-set signatures above.
`AttachmentUpload` is internal and owns only its newly created mesh and texture handles. Loaded views are
cache-owned. E3 creates the bound view with its Skeleton/DrawCount properties and valid E2 binding, while
E4 adds its draw methods. Add `Scene3D.UnloadSurfaceMaps(in SurfaceMaps maps) : void` for an owning caller, unloading
distinct valid handles. The existing `LoadSurfaceMaps(GltfMaterialMaps)` gains failure rollback.

- [ ] **Write the failing tests.** Count source reads for two parts sharing one `(key, hash)` and assert one
  upload. Change a byte and assert hash refusal before GPU upload. Fail a later mesh and a later texture
  creation, then drain the existing deferred-retirement test seam and assert new resources are disposed.
  Keep a previously cached asset alive and drawable through that failure. Assert repeated cache disposal
  is harmless, and get/bind after disposal throws. Use `FakeGpuResourceFactory` counters with a relative
  failure index set after scene construction. Test all-absent maps without unloading scene defaults.
  Capture the newly created resource range before draining, excluding later frame resources:

  ```csharp
  // Failed_later_upload_retires_only_staged_buffers
  Assert.All(stagedBuffers, buffer => Assert.True(buffer.Disposed));
  Assert.All(previousBuffers, buffer => Assert.False(buffer.Disposed));
  Assert.Same(previousAsset, cache.Get(previousKey, previousHash));
  // Disposed_cache_refuses_reuse
  cache.Dispose();
  Assert.Throws<ObjectDisposedException>(() => cache.Get(previousKey, previousHash));
  ```
- [ ] **Run red.** `build-slot --label attachments-assets -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~RigidAttachmentAssetTests|FullyQualifiedName~SurfaceMapUploadFailureTests'`.
- [ ] **Implement ownership.** Verify exact definition bytes and all distinct mesh snapshots/hashes, apply
  embedded GLB policy, then decode before uploading. Stage new uploads separately from committed cache
  entries. Roll back staged meshes before staged textures on any failure, retaining older entries.
  Refactor the existing surface-map method into the cohesive new partial so it records each successful
  texture before attempting the next. Do not grow the large Scene3D file or change its size baseline.
  Cache disposal releases each unique upload once through `UnloadMesh` and `UnloadSurfaceMaps`.
  Document and enforce cache-before-scene disposal at call sites. Bound views never own GPU handles.
  Implement the scene overload through `Scene3DAttachmentScene`. Make the shipped `Scene3DTileWorldScene`
  implement the optional `IRigidAttachmentScene` capability, delegating upload to that adapter and retaining
  its existing full-descriptor `DrawMesh`. Do not add members to `ITileWorldScene`. A recording scene can
  implement the capability with a recording lease, preserving headless consumer testing. This adds an
  Attachments.Render3D reference to the tile renderer, with no dependency back to TileWorld.
- [ ] **Run green.** Run the focused command and existing `OutlineMeshUploadTests` and `Scene3DSharedAssetTests`.
  Expect resource counters and deferred disposal to return to the pre-load state on failure.
- [ ] **Commit explicit paths.** `fix(render3d): make attachment asset loading transactional`.

### E4. Set submission, temporal identity and renderer proof

**Files:** Modify `KhaozEngine.Attachments.Render3D/BoundRigidAttachmentSet.cs`. Create
`KhaozEngine.Render.Tests/Render3D/RigidAttachmentSubmissionTests.cs`,
`KhaozEngine.Render.Tests/Gpu/RigidAttachmentSetGoldenTests.cs` and a focused GPU fixture beside it.
Modify the E3 loaded set's `Bind`, both new package READMEs and `docs/USING-KHAOZENGINE.md`.

**Interfaces:** Consumes E2 binding/E3 resources. Produces `WriteDraws` and both `Queue` overloads.
The template overload replaces Mesh, World and Motion and forwards every other descriptor property.
The convenience overload uses `new RigidInstanceDraw(default, Matrix4x4.Identity)` for defaults.

- [ ] **Write the failing tests.** Record actual packed instance matrices with `HeadlessSceneRig.Frame`,
  `RecordingGpuCommandList` and the existing `ModelRenderer.InstanceData` upload pattern. Assert E2's
  literal points after packing. A two-material part yields two distinct nonzero keys. Reordering parts
  retains keys, a changed mesh hash changes keys, different caller keys differ, and `MotionKey.None`
  stays untracked. An invalid later palette entry queues no prefix. Forward tint, material, dissolve and
  shadow flags. Queue after cache disposal throws. The no-template overload draws opaque white and casts
  shadows. Add a five-part rig fixture with independent limb motion.
  `WriteDraws` returns DrawCount borrowed descriptors and rejects insufficient capacity before writing.
  Assert those descriptors match Queue output and can be consumed by a recording scene or an outline pass.
  Use two materials in one part, `MotionKey.From(123)` and a false shadow flag for the descriptor assertions.
  Aim the renderer fixture at `(10,2,0)` so the literal-point geometry is inside its view, and identify the
  packed instance by the descriptor's mesh handle rather than buffer upload order:

  ```csharp
  // Material_subdraws_have_distinct_temporal_keys
  Assert.Equal(2, bound.WriteDraws(skeleton, palette, model, key, template, draws));
  Assert.NotEqual(draws[0].Motion, draws[1].Motion);
  // Draw_template_flags_survive_binding
  Assert.All(draws, draw => Assert.False(draw.CastsShadows));
  // Packed_instance_uses_the_independent_literal_world_point
  Assert.Equal(new Vector3(11, 2, 0), Vector3.Transform(Vector3.Zero, packedModelForFirstPart));
  ```
- [ ] **Run red.** `build-slot --label attachments-submit -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~RigidAttachmentSubmissionTests`.
- [ ] **Implement submission.** Use binding-owned scratch matrices so the whole pose is validated before
  any scene call. Derive each key by nested `MotionKey.Combine` calls over caller key, the two `uint`
  halves of the semantic SHA-256's first 64 bits, stable part ID and material ordinal. Use explicit
  little-endian reads for those halves. Clone the template with the bound mesh/world/key in `WriteDraws`.
  Queue uses a preallocated descriptor buffer and the scene capability's `DrawMesh`, whose shipped adapter
  forwards to `Scene3D.Draw`. Frame submission performs no loading, hashing or allocations. Document the per-scene,
  render-thread ownership and caller equipment-incarnation key responsibility.
- [ ] **Run green and prove pixels.** Run the focused tests. Add GPU facts comparing the posed set against
  independently positioned control geometry at literal world transforms, including the baked-node case,
  two materials, shadow omission and replacement motion. Capture through real Scene3D, verify nonempty
  coverage and inspect the captured PNGs. Reuse a shared GPU fixture only with a fresh-scene parity case.
  These Golden-named absolute/control-image assertions need no locally baked grid. After pushing the
  branch, dispatch `.github/workflows/cross-platform-gpu.yml` with `legs=all,bake=false`, retain its run ID,
  and require all three backends. If a committed golden genuinely changes, use the documented hosted bake
  followed by a distinct hosted verify run, never concurrent dispatches on the same ref.
- [ ] **Commit explicit paths.** `feat(attachments): submit posed sets with stable draw identities`.

## Final integration and release handoff

- [ ] Sweep Markdown for all new public names and the former absence of attachment sets. Update canonical
  package/umbrella/edge/API references, not only the design history. Check version/tag state and follow the
  engine version ritual for package-bearing work. Keep any pre-merge pack proof in an explicit private feed.
- [ ] Reconcile current main in the task branch. Run `build-slot --label attachments-build -- dotnet build KhaozEngine.slnx -c Release` and
  `build-slot --heavy --label attachments-suite -- dotnet test KhaozEngine.slnx -c Release --no-build --filter 'Category!=LiveSocket'`.
  Create the ignored `local-feed` directory before restore. Run the whole-tree dash/prose/file-size/agent
  guards and `bash scripts/check-doc-versions.sh`. Refresh hosted GPU proof after relevant main changes.
- [ ] Integrate and push the verified engine work, then follow the engine's main-only local-feed ritual.
  Stop at the release gate. The consumer obtains approval for the exact tag and uses the engine-rooted
  release handoff. No task above authorizes a tag or adopting an unpublished package.

## References and self-review

Checked source homes: `ContractJointMap.cs`, `Skeleton.cs`, `BoneSocket.cs`, `GltfLoader.cs`,
`Scene3D.DrawDescriptors.cs`, `Scene3D.SharedAssets.cs`, `Scene3D.Unload.cs`, `RigidInstanceDraw.cs`,
`MotionKey.cs`, `HeadlessSceneRig.cs` and `SkinnedColorCutoutContractTests.cs` in their existing Render3D/test
directories. The JSON byte-memory overload and document lifetime were checked against the
[.NET 10 API reference](https://learn.microsoft.com/en-us/dotnet/api/system.text.json.jsondocument.parse?view=net-10.0).

Coverage: definition/ref validation E1, rig spaces/palette identity E2, loading/failure/lifetime E3,
draw options/material keys/final rendering E4. Review Focus cases map to those same tests. All public names
consumed by later tasks are defined above. No implementation or validation run is claimed by this plan.
