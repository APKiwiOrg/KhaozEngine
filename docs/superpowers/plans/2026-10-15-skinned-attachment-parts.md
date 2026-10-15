# Skinned Attachment Parts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let one attachment set combine rigid pieces and skinned garments that follow the host's current pose, with compatible rigid asset bytes and complete rendering parity.

**Architecture:** Keep definitions and strict embedded GLB validation in the BCL-only Attachments package. Its Render3D arm loads material groups transactionally, checks garment binds against an explicit host mesh, and borrows scene-owned uploads. Bound views precompute name mappings and allocate descriptor and palette scratch at bind time, then use the existing skinned draw, motion and outline paths.

**Tech Stack:** C#, repository .NET SDK, System.Numerics, System.Text.Json, existing SharpGLTF importer behind Render3D, xUnit, headless recording GPU seams, hosted native Metal/D3D11/Vulkan verification.

**Spec:** [Approved skinned stone armour straps design](https://github.com/APKiwiOrg/Grimhollow/blob/0112d03c0144ff1f85526ff1057c5355bf260ee2/docs/superpowers/specs/2026-10-15-skinned-armour-straps-design.md), especially sections 2, 3, 6 and 8. The owner approved the written spec on 2026-10-15.

## Global Constraints

- Start in `~/KhaozEngine/.worktrees/skinned-attachment-parts`. `git rev-parse --abbrev-ref HEAD` must print `feature/skinned-attachment-parts`. Mapped source is `764db3ed56694f5ad117516cd0eb27d8c0d497e5`, released as `v20.40.0`.
- Maximum garment skin joints: 128, equal to `SkinningMath.MaxBonesPerDraw`. Maximum vertex influences: 4 through `JOINTS_0` and `WEIGHTS_0`. Refuse further sets.
- Bind tolerance: absolute difference at most `1e-4` per matrix element, over all 16 elements. Joint names match ordinally against host skin bones.
- Version 1 parses unchanged, with all parts rigid. Rigid-only canonical writing is byte-identical and preserves semantic SHA-256. Version 2 requires `kind` on every part. Kind tokens are exactly `"rigid"` and `"skinned"`.
- Skinned GLBs have exactly one skin referenced by every mesh node, one embedded buffer, glTF 2.0, no external URIs and no duplicate JSON keys. No animations, morph targets, mesh weights, all-zero weight vertices or out-of-range joint indices.
- Steady-state submission allocates nothing. No asset reads, hashing, joint-name searches or animation advancement occur during submission. Palettes are host joint model matrices, not already composed skin matrices.
- Preserve every rigid template field on skinned draws, including shadow-only, inverted shadow dissolve and dissolve complement. Preserve rigid motion identity composition and `MotionKey.None` behaviour.
- The breaking public rename stages major version `21.0.0` unless current main has already staged another unreleased major that accommodates this change. An unreleased 20.x minor cannot absorb a breaking rename. Re-read main and tags before choosing.
- Keep Attachments BCL-only and its Render3D arm's existing dependency edges. No new package, third-party dependency, Foundation/Server membership, game rule, protocol, journal or content change.
- Each single-material skinned part submits one `Scene3D.DrawSkinned`. A multi-material part submits one per loaded material group, as rigid sets do, retaining the spec's material-index motion identity and texture ownership.
- Preserve rigid stone and all existing rigid semantics. No AO workaround, segmented rigid straps or body variants. Art authoring and Grimhollow adoption belong to the parallel game plan.
- Every local build/test/pack uses `build-slot`, with `--heavy` for full suites and packaging. Focused red/green runs are single runs, never retry loops. GPU proof is hosted, not a local GPU loop or consumer boot.
- Read current `AGENTS.md`, `docs/CONTRIBUTOR-RULES.md` and `docs/CROSS-PLATFORM.md` at execution. Never grow a KESIZE baseline or bypass hooks. Namespaces for tests remain `KhaozEngine.Tests.*`.
- Implementation stops at a verified, pushed task branch and draft PR. The owning orchestrator reconciles, merges and pushes main, then runs its guarded shared-feed pack. No task creates a tag. The owner approves the exact tag and the engine-rooted `release-khaozengine` handoff performs release.

## Review Focus

1. A warm cache must not reuse an upload under the wrong part kind. Pin `Warm_cache_cannot_bypass_requested_kind_policy` in Task 5.
2. A name existing as a host node is insufficient if it is absent from the host skin. Pin `Named_non_skin_node_cannot_bind_garment` in Task 6, alongside reordered skin indices.
3. Bind tolerance must be inclusive, finite and elementwise, rather than a translation-only or aggregate comparison. Pin `Bind_tolerance_checks_every_element_and_exact_boundary` in Task 6.
4. An invalid later pose or insufficient output capacity must leave both the destination and scene untouched, including outlines. Pin `Invalid_later_part_publishes_no_prefix` in Task 7.
5. Complement endpoints and shadow-only must work on both CPU and GPU skinning. Pin `Complement_endpoints_and_shadow_only_match_rigid_policy` in Task 4 and its rendered control in Task 8.

---

### Task 1: Generalize definitions and rename the rigid set API without changing v1 bytes

**Files:**

- Rename/modify: `KhaozEngine.Attachments/RigidAttachmentSetDefinition.cs` to `AttachmentSetDefinition.cs` (existing lines 10-72), `RigidAttachmentSetJson.cs` to `AttachmentSetJson.cs` (11-107).
- Create: `KhaozEngine.Attachments/AttachmentPartDefinition.cs` and `KhaozEngine.Attachments.Tests/Fixtures/stone-body.v1.json`, `Fixtures/stone-helmet.v1.json`, `Fixtures/stone-legs.v1.json`.
- Rename/modify: `KhaozEngine.Attachments.Render3D/RigidAttachmentBinding.cs` to `AttachmentSetBinding.cs`, `RigidAttachmentSetCache.cs` to `AttachmentSetCache.cs`, `LoadedRigidAttachmentSet.cs` to `LoadedAttachmentSet.cs`, `BoundRigidAttachmentSet.cs` to `BoundAttachmentSet.cs`, `IRigidAttachmentScene.cs` to `IAttachmentScene.cs`.
- Modify: `KhaozEngine.Attachments.Render3D/Scene3DAttachmentScene.cs`, `KhaozEngine.TileWorld.Render3D/ITileWorldScene.cs`.
- Test/modify: `KhaozEngine.Attachments.Tests/{DefinitionTests.cs,JsonTests.cs,AuthoredStringTests.cs,KhaozEngine.Attachments.Tests.csproj}`, `KhaozEngine.Render.Tests/Render3D/{RigidAttachmentTestRig.cs,RigidAttachmentBindingTests.cs,RigidAttachmentAssetTests.cs,RigidAttachmentSubmissionTests.cs}`, `KhaozEngine.Render.Tests/Gpu/{RigidAttachmentSetGoldenTests.cs,RigidAttachmentGpuScene.cs}`.
- Modify living docs: `KhaozEngine.Attachments/README.md`, `KhaozEngine.Attachments.Render3D/README.md`, `KhaozEngine.TileWorld.Render3D/README.md`, `docs/USING-KHAOZENGINE.md` attachment section (6265-6300). Retain historical test class names that specifically exercise rigid behaviour.

**Interfaces:**

Consumes unchanged `IAttachmentAssetSource.Read(string key) : ReadOnlyMemory<byte>`, `ContractJointMap`, `RigidInstanceDraw`, `IAttachmentMeshLease` and `AttachmentGlb.ValidateRigidEmbedded(ReadOnlyMemory<byte> glb, string sourceName) : void`.

Produces these public signatures in `KhaozEngine.Attachments`:

```csharp
public readonly record struct AttachmentMeshRef(string Key, string Sha256);
public abstract record AttachmentPartDefinition(uint Id, AttachmentMeshRef Mesh);
public sealed record RigidAttachmentPartDefinition(uint Id, AttachmentMeshRef Mesh,
    string Joint, Matrix4x4 Local) : AttachmentPartDefinition(Id, Mesh);
public sealed record SkinnedAttachmentPartDefinition(uint Id, AttachmentMeshRef Mesh)
    : AttachmentPartDefinition(Id, Mesh);
public sealed class AttachmentSetDefinition
{
    public AttachmentSetDefinition(IEnumerable<AttachmentPartDefinition> parts);
    public IReadOnlyList<AttachmentPartDefinition> Parts { get; }
    public IReadOnlyList<AttachmentMeshRef> Meshes { get; }
    public string SemanticSha256 { get; }
    public void ValidateReferences(IReadOnlySet<string> joints,
        IReadOnlyDictionary<string, string> meshSha256);
}
public static class AttachmentSetJson
{
    public const int RigidOnlyFormatVersion = 1;
    public const int CurrentFormatVersion = 2;
    public static AttachmentSetDefinition Parse(ReadOnlyMemory<byte> utf8, string sourceName);
    public static byte[] Write(AttachmentSetDefinition definition);
}
```

Produces renamed signatures in `KhaozEngine.Attachments.Render3D`, initially with their existing rigid behaviour. Tasks 5-7 extend them:

```csharp
public interface IAttachmentScene
{
    IAttachmentMeshLease UploadAttachment(IReadOnlyList<GltfMeshPart> parts);
    void DrawMesh(in RigidInstanceDraw draw);
}
public sealed class Scene3DAttachmentScene : IAttachmentScene
{
    public Scene3DAttachmentScene(Scene3D scene);
    public IAttachmentMeshLease UploadAttachment(IReadOnlyList<GltfMeshPart> parts);
    public void DrawMesh(in RigidInstanceDraw draw);
}
public sealed class AttachmentSetBinding
{
    public AttachmentSetBinding(AttachmentSetDefinition definition, ContractJointMap map);
    public Skeleton Skeleton { get; }
    public int PartCount { get; }
    public void ComposeWorlds(Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette,
        in Matrix4x4 model, Span<Matrix4x4> destination);
}
public sealed class AttachmentSetCache : IDisposable
{
    public AttachmentSetCache(Scene3D scene, IAttachmentAssetSource source);
    public AttachmentSetCache(IAttachmentScene scene, IAttachmentAssetSource source);
    public LoadedAttachmentSet Get(string key, string expectedSha256);
    public void Dispose();
}
public sealed class LoadedAttachmentSet
{
    public AttachmentSetDefinition Definition { get; }
    public string DefinitionSha256 { get; }
    public BoundAttachmentSet Bind(ContractJointMap map);
}
public sealed class BoundAttachmentSet
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

- [ ] **Write failing compatibility and format tests.** Read only the supplied Grimhollow `assets/content/wearable-assets.json`. Decode each stone entry's base64 `definitionUtf8` into the three engine fixtures, without reformatting or adding a newline. Copy fixtures to test output through the test csproj. Their SHA-256 values are body `878eb4752608bcc51aa5d8447d76782ae21323c08bd0c974cff8a1e12fd1c8ea`, helmet `66d0bd8d083cb5b8cbebb968f96d8920980778ce7558e7b0e46229db00b3d9df`, legs `57cce9c9f089b3b1bcd041863997fd547e02cfbe46378c8c98a8cc88b6eea798`. Pin `Shipped_stone_v1_bytes_and_semantic_hash_are_unchanged`, `Mixed_v2_round_trips_and_sorts_by_id`, `Rigid_only_v2_canonicalizes_to_v1`, `V2_refuses_ambiguous_members` and `Definition_captures_owned_parts`:

  ```csharp
  Assert.Equal(expectedSha, Convert.ToHexStringLower(SHA256.HashData(shippedBytes)));
  var legacy = AttachmentSetJson.Parse(shippedBytes, fixtureName);
  Assert.All(legacy.Parts, p => Assert.IsType<RigidAttachmentPartDefinition>(p));
  Assert.Equal(shippedBytes, AttachmentSetJson.Write(legacy));
  Assert.Equal(expectedSha, legacy.SemanticSha256);
  Assert.Equal(1, JsonDocument.Parse(AttachmentSetJson.Write(legacy)).RootElement.GetProperty("formatVersion").GetInt32());
  Assert.Equal(new uint[] { 1, 2 }, mixed.Parts.Select(p => p.Id));
  Assert.Equal(2, writtenMixedVersion);
  Assert.IsType<RigidAttachmentPartDefinition>(reparsed.Parts[0]);
  Assert.IsType<SkinnedAttachmentPartDefinition>(reparsed.Parts[1]);
  Assert.Equal(mixed.SemanticSha256, reparsed.SemanticSha256);
  Assert.Equal(2, captured.Parts.Count); // after input list is cleared
  Assert.Throws<InvalidDataException>(() => ParseCase(refusal));
  ```

  `refusal` theory rows are unknown/case-changed/non-string kind, missing v2 kind, v1 kind, `joint` or `local` on skinned, duplicate root/part/mesh members (including escaped duplicate spelling), unknown root/rigid/skinned/mesh members, duplicate ID across kinds, missing mesh, unsupported version, zero ID and conflicting hashes for one key. Keep all existing key/hash/UTF-16/matrix/ordinal-reference tests. `ValidateReferences` checks mesh hashes for every kind and explicit joints only for rigid parts. Programmatic unknown derived kinds throw `ArgumentException`.
- [ ] **Run red once.** `build-slot --label skinned-format -- dotnet test KhaozEngine.Attachments.Tests/KhaozEngine.Attachments.Tests.csproj -c Release`. Expect missing neutral types, then behavioural v2 assertions to fail after declarations exist.
- [ ] **Implement the signatures above and migrate all 18 mapped C# call sites.** Canonical property order remains v1 `id, mesh, joint, local`. V2 is `id, kind, mesh` followed by rigid `joint, local`. The writer chooses 1 only when every part is rigid, regardless of input format. Preserve float writing, signed-zero normalization, ID order and semantic hashing. Rename set-level symbols and files, retaining the rigid part name and `Scene3DAttachmentScene`. Replace target-typed part construction with explicit rigid/skinned constructors wherever the new collection element type is abstract. Until Task 5, cache loading of a skinned part explicitly refuses with `InvalidDataException` rather than entering a rigid decoder. The map-only binding explicitly refuses mixed sets. Update living examples and describe format 2 in the core README.
- [ ] **Run green once.** Repeat the core command, then `build-slot --label skinned-rename -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~RigidAttachment|FullyQualifiedName~TemporalEntryAttachmentSceneTests'`. Expect all headless rows green, unchanged conditional GPU skips, and no production references to removed set-level names.
- [ ] **Commit explicit paths.** Include deleted names as pathspecs so the renames are staged. Do not stage unrelated changes.

  ```bash
  task_paths=(
    KhaozEngine.Attachments/RigidAttachmentSetDefinition.cs KhaozEngine.Attachments/AttachmentSetDefinition.cs
    KhaozEngine.Attachments/AttachmentPartDefinition.cs KhaozEngine.Attachments/RigidAttachmentSetJson.cs
    KhaozEngine.Attachments/AttachmentSetJson.cs KhaozEngine.Attachments/README.md
    KhaozEngine.Attachments.Tests/DefinitionTests.cs KhaozEngine.Attachments.Tests/JsonTests.cs
    KhaozEngine.Attachments.Tests/AuthoredStringTests.cs KhaozEngine.Attachments.Tests/KhaozEngine.Attachments.Tests.csproj
    KhaozEngine.Attachments.Tests/Fixtures/stone-body.v1.json KhaozEngine.Attachments.Tests/Fixtures/stone-helmet.v1.json
    KhaozEngine.Attachments.Tests/Fixtures/stone-legs.v1.json KhaozEngine.Attachments.Render3D/RigidAttachmentBinding.cs
    KhaozEngine.Attachments.Render3D/AttachmentSetBinding.cs KhaozEngine.Attachments.Render3D/RigidAttachmentSetCache.cs
    KhaozEngine.Attachments.Render3D/AttachmentSetCache.cs KhaozEngine.Attachments.Render3D/LoadedRigidAttachmentSet.cs
    KhaozEngine.Attachments.Render3D/LoadedAttachmentSet.cs KhaozEngine.Attachments.Render3D/BoundRigidAttachmentSet.cs
    KhaozEngine.Attachments.Render3D/BoundAttachmentSet.cs KhaozEngine.Attachments.Render3D/IRigidAttachmentScene.cs
    KhaozEngine.Attachments.Render3D/IAttachmentScene.cs KhaozEngine.Attachments.Render3D/Scene3DAttachmentScene.cs
    KhaozEngine.Attachments.Render3D/README.md KhaozEngine.TileWorld.Render3D/ITileWorldScene.cs
    KhaozEngine.TileWorld.Render3D/README.md KhaozEngine.Render.Tests/Render3D/RigidAttachmentTestRig.cs
    KhaozEngine.Render.Tests/Render3D/RigidAttachmentBindingTests.cs KhaozEngine.Render.Tests/Render3D/RigidAttachmentAssetTests.cs
    KhaozEngine.Render.Tests/Render3D/RigidAttachmentSubmissionTests.cs KhaozEngine.Render.Tests/Gpu/RigidAttachmentSetGoldenTests.cs
    KhaozEngine.Render.Tests/Gpu/RigidAttachmentGpuScene.cs docs/USING-KHAOZENGINE.md
  )
  git add -- "${task_paths[@]}"
  git commit --only -m 'feat(attachments): generalize set definitions and preserve rigid bytes' -- "${task_paths[@]}"
  ```

### Task 2: Validate embedded skinned GLBs before any renderer import

**Files:** Modify `KhaozEngine.Attachments/AttachmentGlb.cs` (14-93 envelope and rigid policy), create `AttachmentGlb.Container.cs`, `AttachmentGlb.Skinned.cs`, `AttachmentGlbAccessors.cs`. Create `test-shared/SkinnedAttachmentGlbFixture.cs`, `KhaozEngine.Attachments.Tests/SkinnedGlbPolicyTests.cs`. Modify `KhaozEngine.Attachments.Tests/GlbPolicyTests.cs`, both `KhaozEngine.Attachments.Tests/KhaozEngine.Attachments.Tests.csproj` and `KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj` to link the BCL fixture. Update `KhaozEngine.Attachments/README.md`.

**Interfaces:** Consumes Task 1 definitions and immutable GLB bytes. Produces:

```csharp
// KhaozEngine.Attachments. Rigid signature and behaviour are retained.
public static partial class AttachmentGlb
{
    public static void ValidateRigidEmbedded(ReadOnlyMemory<byte> glb, string sourceName);
    public static void ValidateSkinnedEmbedded(ReadOnlyMemory<byte> glb, string sourceName);
}
```

The container/accessor types and shared fixture are internal. The fixture creates real BIN-backed position, joint, weight and inverse-bind accessors, not JSON-only pretend geometry.

- [ ] **Write failing policy tests.** `Valid_skin_at_128_joints_and_four_influences_is_accepted` and `Skinned_policy_refuses_lossy_or_unsafe_data` pin:

  ```csharp
  AttachmentGlb.ValidateSkinnedEmbedded(valid128, "valid.glb");
  Assert.Equal(128, fixtureJointCount);
  Assert.Equal(4, fixtureInfluencesPerVertex);
  var error = Assert.Throws<InvalidDataException>(() => AttachmentGlb.ValidateSkinnedEmbedded(bad, "bad.glb"));
  Assert.Contains("bad.glb", error.Message);
  Assert.Contains(expectedContext, error.Message);
  ```

  Theory rows cover zero/two skins, a mesh node with no skin or a different skin index, missing `JOINTS_0`/`WEIGHTS_0` on a later primitive, `JOINTS_1`, `WEIGHTS_1` and `_2`, a zero-weight vertex, index equal to joint count even on a zero-weight lane, 129 joints, repeated joint indices/names, whitespace/empty/missing joint names, animations, primitive targets, node weights and mesh weights. Add negative/non-finite weights, fractional/negative joint indices, mismatched accessor counts and malformed accessor references. Exercise the existing envelope failure table for both policies, plus multiple buffers, external/data buffer/image URIs and nested duplicate keys. Accept strided views, legal normalized UBYTE/USHORT weights, USHORT joints, and sparse accessors. Refuse short/overflowing view or sparse ranges with `InvalidDataException`, before slicing. Reject a sparse override that makes a vertex all-zero or out of range. This pins actual accessor values, not only declarations.
- [ ] **Run red once.** `build-slot --label skinned-policy -- dotnet test KhaozEngine.Attachments.Tests/KhaozEngine.Attachments.Tests.csproj -c Release --filter 'FullyQualifiedName~GlbPolicy'`. Expect missing `ValidateSkinnedEmbedded` and then refusal assertions to fail.
- [ ] **Implement the validator.** Extract the common envelope/JSON/URI checks without tightening existing rigid acceptance. Skinned validation requires exactly one buffer and skin, verifies each mesh node's skin and every primitive's paired VEC4 streams, and decodes every vertex before accepting it. Accessors use checked bounds, component types, offsets, strides, normalized weights and sparse overrides. Read all four indices, including zero-weight lanes. Check weights before importer normalization can repair zeros. Use an internal BCL constant 128, with Task 3 proving equality to `SkinningMath.MaxBonesPerDraw`, rather than adding a Render3D reference. Diagnostics include source and skin/node/primitive/accessor or vertex context. Leave unknown ordinary glTF extension members accepted, while refusing duplicate keys throughout.
- [ ] **Run green once.** Repeat the focused command. Expect both legacy rigid policy tests and every skinned policy row green, with Attachments still having no project/package dependency beyond the BCL.
- [ ] **Commit.**

  ```bash
  task_paths=(
    KhaozEngine.Attachments/AttachmentGlb.cs KhaozEngine.Attachments/AttachmentGlb.Container.cs
    KhaozEngine.Attachments/AttachmentGlb.Skinned.cs KhaozEngine.Attachments/AttachmentGlbAccessors.cs
    KhaozEngine.Attachments/README.md test-shared/SkinnedAttachmentGlbFixture.cs
    KhaozEngine.Attachments.Tests/SkinnedGlbPolicyTests.cs KhaozEngine.Attachments.Tests/GlbPolicyTests.cs
    KhaozEngine.Attachments.Tests/KhaozEngine.Attachments.Tests.csproj KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj
  )
  git add -- "${task_paths[@]}"
  git commit --only -m 'feat(attachments): validate embedded skinned geometry' -- "${task_paths[@]}"
  ```

### Task 3: Import skinned material groups from verified bytes and pin colour parity

**Files:** Extract skinned decoding from `KhaozEngine.Render3D/Models/GltfLoader.cs` (302-405) into new `Models/GltfLoader.Skinned.cs`. Create `Models/GltfLoader.SkinnedMaterialParts.cs`, `Models/GltfSkinnedMeshPart.cs`, `KhaozEngine.Render.Tests/Render3D/GltfSkinnedMaterialPartsTests.cs`. Modify `KhaozEngine.Render3D/README.md`. Consume Task 2's linked fixture.

**Interfaces:** Consumes existing `LoadModel(ReadOnlyMemory<byte> glb, string identity)` and `SkinnedGltfMesh`, plus `GltfLoader.LoadPartsWithMaterials(ReadOnlyMemory<byte> glb, string identity) : IReadOnlyList<GltfMeshPart>` as the colour control. Produces in `KhaozEngine.Render3D`:

```csharp
public sealed record GltfSkinnedMeshPart(SkinnedGltfMesh Mesh, GltfMaterialMaps Maps);
public static partial class GltfLoader
{
    public static SkinnedGltfMesh LoadSkinned(ReadOnlyMemory<byte> glb, string identity);
    public static IReadOnlyList<GltfSkinnedMeshPart> LoadSkinnedPartsWithMaterials(string path);
    public static IReadOnlyList<GltfSkinnedMeshPart> LoadSkinnedPartsWithMaterials(
        ReadOnlyMemory<byte> glb, string identity);
}
```

Existing path signatures `LoadSkinned(string path)` and `LoadSkinnedWithMaterial(string path) : (SkinnedGltfMesh Mesh, GltfMaterialMaps Maps)` remain unchanged.

- [ ] **Write failing tests.** `Memory_import_preserves_skin_order_and_material_order`, `Same_gltf_material_has_identical_rigid_and_skinned_vertex_colour` and `Attachment_joint_cap_matches_renderer` pin:

  ```csharp
  Assert.Equal(128, SkinningMath.MaxBonesPerDraw);
  Assert.Equal(2, skinnedParts.Count); // first-use material order
  Assert.Equal(new[] { "knee", "hip" }, skinBoneNames);
  Assert.Equal(pathMesh.InverseBind, memoryMesh.InverseBind);
  Assert.Equal(pathMesh.RestPose, memoryMesh.RestPose);
  Assert.Same(skinnedParts[0].Mesh.Skeleton, skinnedParts[1].Mesh.Skeleton);
  Assert.Equal(rigidVertex.Color, skinnedVertex.Color);
  Assert.Equal(new Vector4(.2f, .15f, .2f, .4f), skinnedVertex.Color);
  Assert.Equal(rigidParts[material].Maps.AlphaCutoff, skinnedParts[material].Maps.AlphaCutoff);
  ```

  Use material factor `(.4,.6,.8,.5)` and COLOR_0 `(.5,.25,.25,.8)` for the literal colour. Cover absent COLOR_0, VEC3 alpha widening, normalized colour accessors, an embedded albedo/normal/metallic-roughness map and a null material group. Compare map presence, dimensions and RGBA bytes as well as cutoff. Verify weighted vertices and tangents stay aligned when material groups split. Include a translated mesh node with no geometry double-transform: garment vertices stay in skin model coordinates, with the inverse binds carrying the authored rig space. Prove path and memory entry points agree and never require temporary files for the memory path.
- [ ] **Run red once.** `build-slot --label skinned-import -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~GltfSkinnedMaterialPartsTests`. Expect missing bytes/material APIs.
- [ ] **Implement the signatures.** Share the existing skinned decoder, skin-order metadata, weight normalization and tangent calculation rather than creating a second decoder. Split primitives in the rigid loader's stable first-use material order, with one mesh and its maps per group, and share one skeleton across groups. Do not rigid-bake mesh-node transforms into skinned vertices. Preserve legacy single-mesh path behaviour. Both importers already use `ReadBaseColor` and multiply COLOR_0, so preserve that representation and prove parity at loaded vertex/map level. This avoids a backend-specific colour tolerance hiding an importer regression. The render paths share material shading, and Task 8 separately proves real deformation and dissolve pixels.
- [ ] **Run green once.** Run the focused command with `|FullyQualifiedName~GltfMaterialPartsMemoryTests|FullyQualifiedName~GltfLoaderNodeTransformTests` added to the quoted filter. Expect byte/path, colour/map and existing rigid node-transform rows green.
- [ ] **Commit.**

  ```bash
  task_paths=(
    KhaozEngine.Render3D/Models/GltfLoader.cs KhaozEngine.Render3D/Models/GltfLoader.Skinned.cs
    KhaozEngine.Render3D/Models/GltfLoader.SkinnedMaterialParts.cs KhaozEngine.Render3D/Models/GltfSkinnedMeshPart.cs
    KhaozEngine.Render3D/README.md KhaozEngine.Render.Tests/Render3D/GltfSkinnedMaterialPartsTests.cs
  )
  git add -- "${task_paths[@]}"
  git commit --only -m 'feat(render3d): import skinned material groups from bytes' -- "${task_paths[@]}"
  ```

### Task 4: Carry all template flags through CPU and GPU skinned rendering

**Files:** Modify `KhaozEngine.Render3D/{SkinnedInstanceDraw.cs,SkinnedSceneInstances.cs,Scene3D.DrawDescriptors.cs,Scene3D.MotionKeys.cs,Scene3D.SkinnedDrawRecords.cs,Scene3D.GpuSkinning.cs,Scene3D.ShadowCasters.cs,Scene3D.SkinnedPointShadows.cs,ShadowDepthSelection.cs}`, `Rendering/{ModelRenderer.Skinned.cs,ShadowMapRenderer.cs}`, `Internal/{ShaderSources.Model.cs,ShaderSources.Shadow.cs,ShaderSources.MotionModel.cs,ShaderSources.Motion.cs}`. Extract the skinned frame-recording responsibility from `Scene3D.cs` (1480-1540) into new `Scene3D.SkinnedSubmission.cs` instead of growing its baseline. Test/modify `KhaozEngine.Render.Tests/Render3D/{SkinnedInstanceDrawTests.cs,Scene3DSkinnedQueueTests.cs,ShadowDepthSelectionTests.cs}` and create `SkinnedDrawFlagTests.cs`. Update `KhaozEngine.Render3D/README.md`.

**Interfaces:** Consumes unchanged `Scene3D.DrawSkinned(in SkinnedInstanceDraw draw, ReadOnlySpan<Matrix4x4> boneMatrices) : void` and rigid shadow/dissolve semantics. Produces these added public members:

```csharp
// On SkinnedInstanceDraw. Constructor defaults are false, false, 0f respectively.
public bool ShadowOnly { get; init; }
public bool InvertShadowDissolve { get; init; }
public float DissolveComplement { get; init; }
// On SkinnedSceneInstances.Instance, copied from the descriptor.
public readonly bool ShadowOnly;
public readonly bool InvertShadowDissolve;
public readonly float DissolveComplement;
```

No public draw overload is added. ModelRenderer, ShadowMapRenderer, shader packing helpers and shadow variant enums remain internal implementation surfaces.

- [ ] **Write failing tests.** `Descriptor_preserves_all_skinned_flags` and Review Focus `Complement_endpoints_and_shadow_only_match_rigid_policy` exercise both `UseGpuSkinning` values and pin:

  ```csharp
  Assert.False(defaults.ShadowOnly);
  Assert.False(defaults.InvertShadowDissolve);
  Assert.Equal(0f, defaults.DissolveComplement);
  Assert.True(queued.ShadowOnly);
  Assert.True(queued.InvertShadowDissolve);
  Assert.Equal(1f, queued.DissolveComplement);
  Assert.Throws<ArgumentException>(() => scene.DrawSkinned(shadowOnlyWithoutCasting, bones));
  Assert.Equal(beforePalette, scene.BonePaletteSlotForTests(0)); // refused before palette mutation
  Assert.Equal(0, recordedShadowOnlyMainDraws);
  Assert.Equal(0, recordedShadowOnlyMotionKeys);
  Assert.Equal(rigidClassification, skinnedClassification);
  Assert.Equal(1f, packedMainComplementAtByte116);
  Assert.Equal(1f, packedShadowComplement);
  ```

  Inspect real recorded CPU instance data and GPU main/depth slots, not only the descriptor. Theory thresholds are `0f, .5f, 1f`, complement `0f, 1f`, inverted shadow `false, true`, and casting `false, true`. Complement 1 at threshold 0 is fully absent in colour, while complement 1 at threshold 1 retains the complementary coverage. Shadow-only suppresses colour/motion but retains key and point shadow participation. Assert invalid shadow-only/casting rejection before any new palette/queue entry. Keep all existing overload/default parity tests.
- [ ] **Run red once.** `build-slot --label skinned-flags -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~SkinnedDrawFlagTests|FullyQualifiedName~SkinnedInstanceDrawTests|FullyQualifiedName~ShadowDepthSelectionTests'`. Expect missing fields and then packed/classification assertions to fail.
- [ ] **Implement descriptor-to-render parity.** Preserve constructor defaults and propagate all three flags through queue entries, visibility, caster classification, CPU InstanceData, GPU slots and both shadow families. `ShadowOnly && !CastsShadows` throws before queue/palette writes. Set main visibility false for shadow-only and skip its motion history recording. Treat complement as dissolving even at threshold 0, matching rigid classification. Store GPU colour complement in the unused `P[3].y` word at main-header byte offset 116, preserving the existing 128-byte header, and widen the skinned dissolve varying at location 9 to `vec3`, retaining motion locations 10/11. Set that varying to `vec3(P[3].z, P[3].w, P[3].y)`. Update generated motion shader anchors and fragment inputs together. Store depth complement in the existing dissolve block `.z`. Add the GPU-skinned inverted cascade pipeline using the existing inverted fragment, with its resource lifecycle and CPU variant counterpart. Pass complement to point shadows independently of inverted-shadow classification. Keep colour inversion and shadow inversion separate, with the same meaning as `RigidInstanceDraw`.
- [ ] **Run green once.** Repeat the focused command, adding `|FullyQualifiedName~Scene3DSkinnedQueueTests|FullyQualifiedName~SkinnedPointShadow` to the quoted filter. Expect flag/default/refusal/packing/classification rows green for both skinning paths. Shader and rendered behaviour is additionally gated by Task 8, since headless packing cannot prove pixels.
- [ ] **Commit.**

  ```bash
  task_paths=(
    KhaozEngine.Render3D/SkinnedInstanceDraw.cs KhaozEngine.Render3D/SkinnedSceneInstances.cs
    KhaozEngine.Render3D/Scene3D.DrawDescriptors.cs KhaozEngine.Render3D/Scene3D.MotionKeys.cs
    KhaozEngine.Render3D/Scene3D.SkinnedDrawRecords.cs KhaozEngine.Render3D/Scene3D.GpuSkinning.cs
    KhaozEngine.Render3D/Scene3D.ShadowCasters.cs KhaozEngine.Render3D/Scene3D.SkinnedPointShadows.cs
    KhaozEngine.Render3D/ShadowDepthSelection.cs KhaozEngine.Render3D/Scene3D.cs
    KhaozEngine.Render3D/Scene3D.SkinnedSubmission.cs KhaozEngine.Render3D/Rendering/ModelRenderer.Skinned.cs
    KhaozEngine.Render3D/Rendering/ShadowMapRenderer.cs KhaozEngine.Render3D/Internal/ShaderSources.Model.cs
    KhaozEngine.Render3D/Internal/ShaderSources.Shadow.cs KhaozEngine.Render3D/Internal/ShaderSources.MotionModel.cs
    KhaozEngine.Render3D/Internal/ShaderSources.Motion.cs KhaozEngine.Render3D/README.md
    KhaozEngine.Render.Tests/Render3D/SkinnedInstanceDrawTests.cs KhaozEngine.Render.Tests/Render3D/Scene3DSkinnedQueueTests.cs
    KhaozEngine.Render.Tests/Render3D/ShadowDepthSelectionTests.cs KhaozEngine.Render.Tests/Render3D/SkinnedDrawFlagTests.cs
  )
  git add -- "${task_paths[@]}"
  git commit --only -m 'feat(render3d): give skinned draws full dissolve and shadow parity' -- "${task_paths[@]}"
  ```

### Task 5: Load mixed assets transactionally with typed skinned leases

**Files:** Modify Task 1's `KhaozEngine.Attachments.Render3D/{AttachmentSetCache.cs,LoadedAttachmentSet.cs,IAttachmentScene.cs,Scene3DAttachmentScene.cs}`, existing `AttachmentUpload.cs`, and `KhaozEngine.TileWorld.Render3D/ITileWorldScene.cs`. Create `SkinnedAttachmentUpload.cs`, `AttachmentPartResources.cs`, `KhaozEngine.Render.Tests/Render3D/SkinnedAttachmentAssetTests.cs`. Extend recording scenes in `RigidAttachmentAssetTests.cs` and `RigidAttachmentSubmissionTests.cs`. Update `KhaozEngine.Attachments.Render3D/README.md`.

**Interfaces:** Consumes Tasks 1-3 and unchanged `Scene3D.LoadSkinnedMesh(SkinnedGltfMesh mesh, Scene3D.SurfaceMaps maps) : SkinnedMeshHandle`, `UnloadSkinnedMesh(SkinnedMeshHandle handle) : void`. Produces:

```csharp
// KhaozEngine.Attachments.Render3D
public interface ISkinnedAttachmentMeshLease : IDisposable
{
    IReadOnlyList<SkinnedMeshHandle> Handles { get; }
}
// Added to IAttachmentScene, Scene3DAttachmentScene and Scene3DTileWorldScene.
public ISkinnedAttachmentMeshLease UploadSkinnedAttachment(IReadOnlyList<GltfSkinnedMeshPart> parts);
```

`AttachmentPartResources` is internal, retaining immutable skin metadata and material-ordered typed handles. `Get`, `Definition`, `DefinitionSha256` and `Dispose` keep Task 1 signatures. This task loads skinned resources, while Task 6 supplies their bind overload.

- [ ] **Write failing tests.** `Mixed_load_verifies_all_CPU_assets_before_upload`, `Skinned_upload_rollback_preserves_previous_assets`, `Skinned_cache_disposal_retires_each_handle_once` and Review Focus `Warm_cache_cannot_bypass_requested_kind_policy` pin:

  ```csharp
  Assert.Equal(1, source.Reads[sharedSkinnedKey]);
  Assert.Equal(1, skinnedUploads); // shared reference across parts and sets
  Assert.Equal(2, materialHandles.Count);
  Assert.Equal(beforeUploads, uploadsAfterLaterInvalidAsset);
  Assert.Throws<InvalidDataException>(() => cache.Get(wrongKindDefinitionKey, wrongKindHash));
  Assert.All(newBuffers, b => Assert.True(b.Disposed));
  Assert.All(newTextures, t => Assert.True(t.Disposed));
  Assert.All(previousBuffers, b => Assert.False(b.Disposed));
  Assert.Same(previous, cache.Get(previousKey, previousHash));
  Assert.Throws<ObjectDisposedException>(() => loaded.Definition);
  Assert.Equal(0, liveSetsNamingFreedTextures);
  ```

  Exercise both directions of wrong-kind reuse, including a definition mixing rigid/skinned parts with the same key/hash, and a wrong-kind request after another set warmed the cache. Fail a later skinned buffer upload and a later texture upload after a rigid upload succeeded. Use the existing relative factory failure injection and deferred retirement drain. After failure, previous rigid and skinned uploads remain drawable and retry succeeds. Mutating source memory after a subsequent source read must not change captured geometry or metadata. Idempotent disposal retires rigid and skinned meshes before any of their textures.
- [ ] **Run red once.** `build-slot --label skinned-cache -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~SkinnedAttachmentAssetTests`. Expect the Task 1 unsupported-kind refusal or missing skinned upload capability.
- [ ] **Implement mixed loading and lease ownership.** Classify all references by requested part kind before cache hits can bypass validation. Key cached geometry by `(AttachmentMeshRef, part kind)` and preflight each requested kind, retaining one read/upload per compatible reference. A reference used under contradictory policies is refused, even warm. Verify all exact snapshots/hashes and decode all pending CPU assets before uploading. Add typed transactional skinned leases using the existing surface-map rollback path. Preserve staged/committed separation and release every staged mesh of both kinds before staged textures. Forward skinned upload through the Scene3D adapter and tile facade without widening `ITileWorldScene` itself. Existing map-only Bind still refuses mixed sets clearly until Task 6. Update all recording capability implementers to compile.
- [ ] **Run green once.** Repeat with `|FullyQualifiedName~RigidAttachmentAssetTests|FullyQualifiedName~SurfaceMapUploadFailureTests` added to the quoted filter. Expect both kinds' ownership and rollback rows green, including actual deferred disposal and warm-cache refusal.
- [ ] **Commit.**

  ```bash
  task_paths=(
    KhaozEngine.Attachments.Render3D/AttachmentSetCache.cs KhaozEngine.Attachments.Render3D/LoadedAttachmentSet.cs
    KhaozEngine.Attachments.Render3D/IAttachmentScene.cs KhaozEngine.Attachments.Render3D/Scene3DAttachmentScene.cs
    KhaozEngine.Attachments.Render3D/AttachmentUpload.cs KhaozEngine.Attachments.Render3D/SkinnedAttachmentUpload.cs
    KhaozEngine.Attachments.Render3D/AttachmentPartResources.cs KhaozEngine.Attachments.Render3D/README.md
    KhaozEngine.TileWorld.Render3D/ITileWorldScene.cs KhaozEngine.Render.Tests/Render3D/SkinnedAttachmentAssetTests.cs
    KhaozEngine.Render.Tests/Render3D/RigidAttachmentAssetTests.cs KhaozEngine.Render.Tests/Render3D/RigidAttachmentSubmissionTests.cs
  )
  git add -- "${task_paths[@]}"
  git commit --only -m 'feat(attachments): cache mixed material uploads transactionally' -- "${task_paths[@]}"
  ```

### Task 6: Bind garment joints to the explicit host skin once

**Files:** Modify `KhaozEngine.Attachments.Render3D/{AttachmentSetBinding.cs,LoadedAttachmentSet.cs,BoundAttachmentSet.cs}`. Create internal `SkinnedAttachmentBinding.cs`, `KhaozEngine.Render.Tests/Render3D/SkinnedAttachmentBindingTests.cs`. Extend `test-shared/SkinnedAttachmentGlbFixture.cs` with reordered skin and mismatch cases. Update `KhaozEngine.Attachments.Render3D/README.md`.

**Interfaces:** Consumes `ContractJointMap.Node(string name) : int`, `Bone(string name) : int`, `Skeleton`, `SkinnedGltfMesh.{Skeleton,InverseBind,RestPose}` and Task 5's captured skin metadata. `Skeleton` has no inverse-bind property, so do not infer binds by inverting a posed/rest palette. Produces:

```csharp
// AttachmentSetBinding, in addition to the retained rigid-only constructor.
public AttachmentSetBinding(AttachmentSetDefinition definition, ContractJointMap map,
    SkinnedGltfMesh hostMesh,
    IReadOnlyDictionary<AttachmentMeshRef, SkinnedGltfMesh> skinnedMeshes);
public int SkinnedJointCount(uint partId);
public void CopySkinnedPalette(Skeleton skeleton, ReadOnlySpan<Matrix4x4> hostPalette,
    uint partId, Span<Matrix4x4> destination);
// LoadedAttachmentSet, in addition to the retained rigid-only Bind(map).
public BoundAttachmentSet Bind(ContractJointMap map, SkinnedGltfMesh hostMesh);
```

`ComposeWorlds` retains Task 1's signature and canonical all-part count. It writes composed rigid worlds and the unchanged model for skinned parts. The map-only constructor/Bind accepts rigid-only sets and throws `InvalidDataException` with a host-mesh explanation for mixed sets. Wrong host skeleton or frame arguments throw `ArgumentException`. Missing garment references, missing/non-skin joint names and mismatched binds throw contextual `InvalidDataException`.

- [ ] **Write failing tests.** `Garment_palette_uses_named_host_skin_indices`, `Named_non_skin_node_cannot_bind_garment`, `Bind_tolerance_checks_every_element_and_exact_boundary`, `Host_mesh_identity_and_joint_cap_are_checked` and `Binding_snapshots_its_index_table` pin:

  ```csharp
  Assert.Equal(2, binding.SkinnedJointCount(2));
  binding.CopySkinnedPalette(hostSkeleton, hostPalette, 2, garmentPalette);
  Assert.Equal(hostPalette[1], garmentPalette[0]); // garment hip, host [knee, hip]
  Assert.Equal(hostPalette[0], garmentPalette[1]); // garment knee
  Assert.Throws<InvalidDataException>(() => BindWithJoint("root")); // named node, not skin bone
  Assert.Throws<InvalidDataException>(() => BindWithJoint("missing"));
  Assert.Throws<InvalidDataException>(() => BindWithJoint("Hip")); // ordinal name mismatch
  Assert.Null(Record.Exception(() => BindWithOffDiagonalDelta(1e-4f)));
  Assert.Null(Record.Exception(() => BindWithOffDiagonalDelta(-1e-4f)));
  Assert.Throws<InvalidDataException>(() => BindWithOffDiagonalDelta(MathF.BitIncrement(1e-4f)));
  Assert.Throws<InvalidDataException>(() => BindWithElementDelta(element, 2e-4f));
  Assert.Throws<InvalidDataException>(() => BindWithElementValue(element, float.NaN));
  Assert.Throws<ArgumentException>(() => loaded.Bind(map, equalSizedForeignHostMesh));
  Assert.Equal(128, acceptedJointCount);
  Assert.Throws<InvalidDataException>(() => BindProgrammaticSkin(129));
  ```

  `element` ranges over all 16 entries. Boundary cases use a zero off-diagonal source element so float subtraction measures the exact `1e-4f` boundary. Also cover infinity, garment/host bind-array length mismatch, absent garment metadata, a host mesh whose actual inverse bind differs despite a matching RestPose, insufficient destination, a foreign same-size frame skeleton, palette-length mismatch and unused destination tail preservation. After binding, mutate the source garment joint-to-node array and verify copied palette order stays fixed. A garment subset of a larger host is accepted, and rigid-only bindings still pass all existing literal-point tests.
- [ ] **Run red once.** `build-slot --label skinned-bind -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~SkinnedAttachmentBindingTests`. Expect missing host overload/table methods.
- [ ] **Implement binding and scratch preparation.** Require `ReferenceEquals(hostMesh.Skeleton, map.Skeleton)` and consistent host bind count. For garment bone `g`, read its skin node name from `garment.Skeleton.NodeNames[garment.Skeleton.JointToNode[g]]`, call `map.Bone(name)`, compare `garment.InverseBind[g]` with `hostMesh.InverseBind[hostBone]`, then store that host index. Require finite matrices before the 16 comparisons. Snapshot tables once and allocate one garment palette per logical part. `CopySkinnedPalette` validates the exact host skeleton, full host palette length, named part and output capacity, then copies joint model matrices without multiplying inverse binds. Preserve rigid `Local * BodyAlignment` composition. The renderer alone applies garment inverse binds.
- [ ] **Run green once.** Repeat with `|FullyQualifiedName~RigidAttachmentBindingTests|FullyQualifiedName~ContractJointMapTests|FullyQualifiedName~BoneSocketTests` added to the quoted filter. Expect mapping, every-element tolerance, identity and rigid compatibility rows green.
- [ ] **Commit.**

  ```bash
  task_paths=(
    KhaozEngine.Attachments.Render3D/AttachmentSetBinding.cs KhaozEngine.Attachments.Render3D/SkinnedAttachmentBinding.cs
    KhaozEngine.Attachments.Render3D/LoadedAttachmentSet.cs KhaozEngine.Attachments.Render3D/BoundAttachmentSet.cs
    KhaozEngine.Attachments.Render3D/README.md KhaozEngine.Render.Tests/Render3D/SkinnedAttachmentBindingTests.cs
    test-shared/SkinnedAttachmentGlbFixture.cs
  )
  git add -- "${task_paths[@]}"
  git commit --only -m 'feat(attachments): bind garment skin palettes by host joint name' -- "${task_paths[@]}"
  ```

### Task 7: Submit mixed descriptors, motion palettes and outlines without frame allocations

**Files:** Modify `KhaozEngine.Attachments.Render3D/{BoundAttachmentSet.cs,IAttachmentScene.cs,Scene3DAttachmentScene.cs}` and `KhaozEngine.TileWorld.Render3D/ITileWorldScene.cs`. Create `AttachmentDraw.cs`, `KhaozEngine.Render.Tests/Render3D/{SkinnedAttachmentSubmissionTests.cs,SkinnedAttachmentOutlineTests.cs}`. Modify recording implementers in `RigidAttachmentAssetTests.cs`, `RigidAttachmentSubmissionTests.cs`, plus `TemporalEntryAttachmentSceneTests.cs` and `KhaozEngine.Tests/ArchitectureTests.AttachmentScenes.cs`. Update `KhaozEngine.Attachments.Render3D/README.md` and `KhaozEngine.TileWorld.Render3D/README.md`.

**Interfaces:** Consumes Tasks 4-6 and existing `MeshOutlineGroup`, `Scene3D.DrawSkinned`, `DrawMeshOutlineDissolved`, `DrawSkinnedOutlineDissolved` and recorded previous-palette motion. Produces:

```csharp
// KhaozEngine.Attachments.Render3D. Internal construction, borrowed palette memory.
public readonly struct AttachmentDraw
{
    public uint PartId { get; }
    public int MaterialIndex { get; }
    public bool IsSkinned { get; }
    public RigidInstanceDraw Rigid { get; }
    public SkinnedInstanceDraw Skinned { get; }
    public ReadOnlyMemory<Matrix4x4> BoneMatrices { get; }
}
// BoundAttachmentSet. Existing Queue overloads now cover both kinds.
public int WriteDraws(Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette,
    in Matrix4x4 model, MotionKey instanceKey, in RigidInstanceDraw template,
    Span<AttachmentDraw> destination);
public void QueueOutline(Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette,
    in Matrix4x4 model, MeshOutlineGroup group,
    float dissolve = 0f, bool dissolveComplement = false);
// Add to IAttachmentScene and implement on Scene3DAttachmentScene and Scene3DTileWorldScene.
public void DrawSkinned(in SkinnedInstanceDraw draw, ReadOnlySpan<Matrix4x4> boneMatrices);
public void DrawMeshOutlineDissolved(MeshOutlineGroup group, MeshHandle mesh,
    Matrix4x4 world, float dissolve, bool dissolveComplement);
public void DrawSkinnedOutlineDissolved(MeshOutlineGroup group, SkinnedMeshHandle mesh,
    ReadOnlySpan<Matrix4x4> boneMatrices, Matrix4x4 world,
    float dissolve, bool dissolveComplement);
```

The rigid-descriptor `WriteDraws` overload remains valid for rigid-only sets and explicitly throws `InvalidOperationException` for mixed sets before writing anything. `DrawCount` counts all material draws. A descriptor's palette borrows the bound view's preallocated memory and remains valid only until its next submission/preparation or cache disposal. Consume it synchronously. An outline group is created by the caller through its scene's existing `BeginMeshOutline` API. Occlusion/target settings belong to that group.

- [ ] **Write failing submission and outline tests.** `Mixed_template_fields_and_palette_match_scene_submission`, `Mixed_material_motion_keys_are_stable_and_distinct`, `Steady_mixed_submission_allocates_nothing`, `Mixed_outline_uses_the_same_pose_and_dissolve` and Review Focus `Invalid_later_part_publishes_no_prefix` pin:

  ```csharp
  Assert.Equal(3, bound.DrawCount); // one rigid group, two garment material groups
  Assert.Equal(3, bound.WriteDraws(skeleton, palette, model, key, template, draws));
  Assert.Equal(1, rigidCalls);
  Assert.Equal(2, skinnedCalls);
  Assert.Equal(model, draws[1].Skinned.Model);
  Assert.Equal(palette[1], submittedGarmentPalette[0]);
  Assert.Equal(palette[0], submittedGarmentPalette[1]);
  Assert.Equal(template.Tint, skin.Tint);
  Assert.Equal(template.Material, skin.Material);
  Assert.Equal(template.Dissolve, skin.Dissolve);
  Assert.Equal(template.DissolveEdgeWidth, skin.DissolveEdgeWidth);
  Assert.Equal(template.DissolveEdgeColor, skin.DissolveEdgeColor);
  Assert.Equal(template.CastsShadows, skin.CastsShadows);
  Assert.Equal(template.ShadowOnly, skin.ShadowOnly);
  Assert.Equal(template.InvertShadowDissolve, skin.InvertShadowDissolve);
  Assert.Equal(template.DissolveComplement, skin.DissolveComplement);
  Assert.Equal(3, keys.Distinct().Count());
  Assert.Equal(keys, reorderedKeys);
  Assert.NotEqual(keys, revisedMeshKeys);
  Assert.NotEqual(keys, replacementInstanceKeys);
  Assert.All(untrackedKeys, k => Assert.True(k.IsNone));
  Assert.Equal(beforeDestination, afterRefusedDestination);
  Assert.Equal(0, callsAfterInvalidLaterPose);
  Assert.Equal(0, outlinePartsAfterInvalidLaterPose);
  Assert.Equal(SkinningMath.Compose(palette[1], garmentInverseBind[0]), actualOutlinePose[0]);
  Assert.Equal(.5f, recordedOutlineDissolve);
  Assert.True(recordedOutlineComplement);
  AllocAssert.NoPerCallAllocation("one warmed mixed submit", () =>
  {
      fresh.WriteDraws(skeleton, palette, model, key, template, draws);
      fresh.Queue(skeleton, palette, model, key, template);
  }); // exact zero bytes, using preallocated arrays and warmed scene capacities
  ```

  Use two material groups sharing one garment palette buffer, a nonidentity inverse bind, a translated character model and reordered host indices. Compare copied descriptors with actual Scene3D queue entries and composed palette windows. After a second posed frame, inspect the existing motion-history/previous-palette seams to prove the first composed palette survives, rather than aliases overwritten scratch. Invalid later palette values, output capacity and shadow-only without casting publish no prefix for either kind. Map-only Bind and rigid-only WriteDraws must fail explicitly on a mixed set. Queue/default-template uses white tint, `Material.None`, zero dissolve and enabled casting. Queue/write/outline after cache disposal all throw `ObjectDisposedException`.

  Place the allocation case in `[Collection("AllocSensitive")]`, whose existing definition disables parallelization. Warm scene capacities, both temporal history generations and a representative bound set, then measure a fresh binding's descriptor write and Queue once through `AllocAssert.NoPerCallAllocation`. Use preallocated non-recording sinks, or pre-warmed real Scene3D queues. Set the source read callback to throw during the measurement. Expect exactly zero allocated bytes. Do not measure LINQ, assertions, fixture creation or recording palette clones. Follow `RigidAttachmentSubmissionTests.First_submission_and_descriptor_writing_allocate_no_frame_memory` and `MotionFrameAllocationTests` rather than writing a test retry loop.
- [ ] **Run red once.** `build-slot --label skinned-submit -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~SkinnedAttachmentSubmissionTests|FullyQualifiedName~SkinnedAttachmentOutlineTests'`. Expect missing mixed descriptor/outline APIs or zero skinned submissions.
- [ ] **Implement mixed preparation and submission.** Allocate all descriptor and per-part palette arrays in the bound constructor. Validate and prepare the entire pose and template before touching caller output or making scene calls. For skinned descriptors copy every template property explicitly, replace Mesh/Model/Motion, and attach the part's borrowed palette. Rigid descriptors retain `template with { Mesh, World, Motion }`. Queue walks canonical part-ID/material order and dispatches by kind. Keep the existing key algorithm, including little-endian revision halves:

  ```csharp
  revision = MotionKey.Combine(MotionKey.Combine(instanceKey, revisionLow), revisionHigh);
  motion = MotionKey.Combine(MotionKey.Combine(revision, partId), (uint)materialIndex);
  ```

  The revision halves come from the first 64 bits of `Definition.SemanticSha256`, captured once at binding. QueueOutline reuses complete pose preparation with the caller's dissolve/complement and invokes the existing rigid/skinned group paths. Do not invent another outline renderer or transform the garment palette twice. The tile facade must forward full descriptors, not its legacy default method that drops material/shadow/motion fields. Retain dependency graph assertions and add an interface-assignment/forwarding test for this capability.
- [ ] **Run green once.** Repeat the focused command with `|FullyQualifiedName~RigidAttachmentSubmissionTests|FullyQualifiedName~TemporalEntryAttachmentSceneTests` added to the quoted filter. Expect palette, motion, allocation and outline cases green. Run `build-slot --label skinned-scene-contract -- dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj -c Release --filter FullyQualifiedName~Attachment` once to verify the unchanged package edges and optional capability.
- [ ] **Commit.**

  ```bash
  task_paths=(
    KhaozEngine.Attachments.Render3D/BoundAttachmentSet.cs KhaozEngine.Attachments.Render3D/AttachmentDraw.cs
    KhaozEngine.Attachments.Render3D/IAttachmentScene.cs KhaozEngine.Attachments.Render3D/Scene3DAttachmentScene.cs
    KhaozEngine.Attachments.Render3D/README.md KhaozEngine.TileWorld.Render3D/ITileWorldScene.cs
    KhaozEngine.TileWorld.Render3D/README.md KhaozEngine.Render.Tests/Render3D/SkinnedAttachmentSubmissionTests.cs
    KhaozEngine.Render.Tests/Render3D/SkinnedAttachmentOutlineTests.cs KhaozEngine.Render.Tests/Render3D/RigidAttachmentAssetTests.cs
    KhaozEngine.Render.Tests/Render3D/RigidAttachmentSubmissionTests.cs KhaozEngine.Render.Tests/Render3D/TemporalEntryAttachmentSceneTests.cs
    KhaozEngine.Tests/ArchitectureTests.AttachmentScenes.cs
  )
  git add -- "${task_paths[@]}"
  git commit --only -m 'feat(attachments): submit mixed draws motion and outlines without allocation' -- "${task_paths[@]}"
  ```

### Task 8: Prove posed garment and dissolve parity on all three hosted backends

**Files:** Create `KhaozEngine.Render.Tests/Gpu/SkinnedAttachmentSetGoldenTests.cs` and `SkinnedAttachmentGpuScene.cs`. Consume the shared CPU fixture. Follow `RigidAttachmentSetGoldenTests.cs`, `RigidAttachmentGpuScene.cs`, `Render3DSkinnedGpuTests.cs`, `SkinnedMotionGpuTests.cs` and `SkinnedTargetOutlineGoldenTests.cs`. No workflow change or committed golden-grid file is required for independent control-image comparisons.

**Interfaces:** Consumes Task 7's public contract. Produces no engine public API. Test fixture lifetime uses `IClassFixture<SkinnedAttachmentGpuScene>` with lazy scene creation, the existing `[GpuFact]`/`[GpuTheory]` gates, evidence PNGs and an aged-versus-fresh parity test.

- [ ] **Write failing GPU facts.** `Posed_garment_on_host_matches_independent_skinned_controls`, `Mixed_dissolve_and_shadow_flags_match_rigid_controls` and `Reused_scene_matches_fresh_after_pose_and_flag_changes` pin:

  ```csharp
  Assert.True(garmentColoredPixels > 500);
  Assert.True(hostColoredPixels > 500);
  Assert.Equal(independentControlPixels, attachmentPixels);
  Assert.Equal(cpuSkinningPixels, gpuSkinningPixels);
  Assert.Equal(rigidDissolveControlPixels, skinnedGarmentPixels);
  Assert.Equal(receiverOnlyPixels, shadowOnlyColourPixels);
  Assert.True(shadowOnlyChangedReceiverPixels > 100);
  Assert.Equal(freshScenePixels, agedScenePixels);
  ```

  Pose a synthetic host with two bones, a garment spanning both at nontrivial weights, nonidentity inverse binds and reordered garment indices. Independently pose control geometry from literal per-bone transforms, without using AttachmentSetBinding or its copied palette. Use the shared renderer capture conventions for deterministic pixel equality, with sufficiently large flat-coloured triangles and nonempty coverage. Render a rigid control made from independently CPU-posed garment vertices at the same model transform, so the dissolve comparison shares geometry/noise space. Threshold cases are `0f, .5f, 1f`, complement `0f, 1f`, inverted shadow both values, and shadow-only. Compare key/point shadow receiver regions as well as colour for both skinning modes. Also capture the garment in a target outline group and prove nonempty outline coverage against the direct skinned outline control. Keep the class name containing `Golden` so PR-tier D3D11/Vulkan selection runs it. Use existing per-backend comparison conventions rather than loosening a tolerance to make parity pass.
- [ ] **Run red on hosted GPU infrastructure.** Commit the test-only changes and push this task branch, then dispatch one filtered `cross-platform-gpu` run at that exact commit. Command: `gh workflow run cross-platform-gpu.yml --ref feature/skinned-attachment-parts -f legs=all -f bake=false -f tier=push -f renderTestFilter=FullyQualifiedName~SkinnedAttachmentSetGoldenTests`. At the test-only checkpoint, expect failure because the new fixture capture implementation is absent. Once that fixture is declared, a pose/flag regression must fail the corresponding assertion. Ordinary `dotnet test` skips these facts and cannot serve as red evidence. Record the SHA and run URL. No local GPU run, full-suite retry loop or stress dispatch.
- [ ] **Complete the fixture and fix only demonstrated rendering defects.** Keep all substantive production behavior owned by Tasks 3, 4 and 7. A failed GPU assertion returns to its owning task's focused tests and commit, then a corrected fixture/proof commit follows. The fixture's independent control and aged/fresh assertions prevent a shared mapping or stale-scene error from validating itself. Preserve PNG evidence by backend. These are absolute/control-image golden facts like the rigid prior art, so do not seed expected pixels from the feature under test.
- [ ] **Run green once at the corrected pushed head.** Use the same filtered hosted command. Require every new and existing attachment golden row selected by the final proof to pass without skips on `metal-native` (hosted `macos-26`), `direct3d11-native` (hosted Windows/WARP) and `vulkan-native` (hosted Linux/lavapipe). A filter-only run is scoped feature proof, not a green full matrix. Task 9's unfiltered exact-head dispatch and Vulkan synchronization leg remain required. If a future correction truly introduces stored reference grids, bake only on the owning hosted leg, inspect its evidence, commit its grids, then run a separate `bake=false` verification. Do not overlap dispatches on one ref.
- [ ] **Commit the final proof changes with explicit paths.** The red-test checkpoint uses the same explicit paths and subject `test(attachments): pin skinned garment GPU acceptance`. The final commit is:

  ```bash
  task_paths=(
    KhaozEngine.Render.Tests/Gpu/SkinnedAttachmentSetGoldenTests.cs KhaozEngine.Render.Tests/Gpu/SkinnedAttachmentGpuScene.cs
  )
  git add -- "${task_paths[@]}"
  git commit --only -m 'test(attachments): prove skinned garment pose and dissolve parity' -- "${task_paths[@]}"
  ```

### Task 9: Stage the breaking major, verify the whole engine and open the gated draft PR

**Files:** Modify `Directory.Build.props`, `CHANGELOG.md`, `README.md`, `docs/USING-KHAOZENGINE.md`, `docs/INDEX.md`, `docs/design/RIGID-ATTACHMENT-SETS-DESIGN-2026-10-13.md`, `KhaozEngine.Attachments/README.md`, `KhaozEngine.Attachments.Render3D/README.md`, `KhaozEngine.Render3D/README.md`, `KhaozEngine.TileWorld.Render3D/README.md`. Create `docs/design/SKINNED-ATTACHMENT-PARTS-DESIGN-2026-10-15.md`. Test using existing whole-tree and doc-version guards, with no new production API.

**Interfaces:** Consumes all preceding public signatures. Produces the living consumer contract, one indexed design record, consistent staged version/changelog, verified branch and draft PR. The new design record links the approved spec and this plan. Mark the rigid design superseded for current APIs without rewriting its historical audit or previous plan.

- [ ] **Write the failing version/contract check.** Before staging, run this inline assertion as the `Staged_breaking_major_and_documented_version_agree` gate. For a concurrently staged different major, substitute the value selected after the same tag/main checks. This is a repository acceptance check, not a new runtime test:

  ```python
  import pathlib, re
  root = pathlib.Path('.')
  planned = '21.0.0'
  props = (root / 'Directory.Build.props').read_text()
  assert re.search(r'<KhaozEngineVersion>([^<]+)</KhaozEngineVersion>', props)[1] == planned
  log = (root / 'CHANGELOG.md').read_text()
  assert re.search(r'^## (.+)$', log, re.M)[1] == planned
  for path in ('README.md', 'docs/USING-KHAOZENGINE.md'):
      versions = re.findall(r'PackageReference Include="KhaozEngine[^"\n]*" Version="([^"]+)"', (root / path).read_text())
      assert versions and all(v == planned for v in versions), path
  ```

- [ ] **Run red and select the version.** Expect the inline check to fail while this branch is `20.40.0`. `git fetch origin`, inspect `main`, `origin/main`, version and newest tags. Merge the current integration base into this branch, resolve here, and select `21.0.0` if no other major is staged. Ride a staged unreleased major rather than adding another bump. Never ride a 20.x additive version for this rename or overwrite a released tag. Report a changed major selection to the orchestrator.
- [ ] **Implement the documentation/version contract.** Put the version and matching newest `CHANGELOG.md` heading in the same commit. Explain the breaking set-level renames, new part hierarchy, v1/v2 canonicalization, strict garment policy, explicit host-mesh bind, material-group draws, palette lifetime, allocation guarantee and rendering parity. Living examples use `loaded.Bind(map, hostMesh)`, `Span<AttachmentDraw>` or Queue, and one scene-created outline group. Note that rigid-only `Bind(map)`/rigid descriptor writing remain valid and that mixed use of the latter fails explicitly. Update every `PackageReference` declaration checked by `scripts/check-doc-versions.sh`. Sweep all Markdown and package READMEs for removed symbols and old rigid-only statements using `rg`, distinguishing historical design/audit records and rigid-specific test names from live API instructions. No source/dependency inventory is duplicated into the design doc.
- [ ] **Run green and full verification on the reconciled branch.** The inline gate and all commands below must exit 0. Create the ignored worktree `local-feed` before restore. Run the full suite once at the finish, with zero build warnings. Conditional GPU skips in this local suite are expected and reported separately from hosted proof.

  ```bash
  mkdir -p local-feed
  build-slot --label skinned-final-build -- dotnet build KhaozEngine.slnx -c Release
  build-slot --heavy --label skinned-final-suite -- dotnet test KhaozEngine.slnx -c Release --no-build --filter 'Category!=LiveSocket'
  build-slot --label skinned-final-format -- dotnet format KhaozEngine.slnx --verify-no-changes --no-restore
  sh scripts/check-dashes.sh --tree
  sh scripts/check-prose.sh --tree
  sh scripts/check-file-size.sh --tree
  sh scripts/check-agent-instructions.sh --tree
  bash scripts/check-doc-versions.sh
  ```

  Private package proof uses `KHAOZENGINE_FEED=/tmp/khaozengine-skinned-attachment-parts-private-feed build-slot --heavy --label skinned-private-pack -- bash scripts/pack-local-feed.sh`. Verify staged-version packages and symbols exist, assembly bytes match the tested build, repository commit metadata matches the committed proof head, and the Attachments/Render3D dependency edges are unchanged. Never run a bare pack into the shared feed. Pack after the explicit final commit below so commit metadata names that head. Re-run verification only if reconciliation, formatting or an actual correction changes the tested source.
- [ ] **Commit explicit documentation/version paths.** Do not absorb unrelated staged content. If another major was selected, use that exact value as the release commit scope.

  ```bash
  task_paths=(
    Directory.Build.props CHANGELOG.md
    README.md docs/USING-KHAOZENGINE.md
    docs/INDEX.md docs/design/SKINNED-ATTACHMENT-PARTS-DESIGN-2026-10-15.md
    docs/design/RIGID-ATTACHMENT-SETS-DESIGN-2026-10-13.md KhaozEngine.Attachments/README.md
    KhaozEngine.Attachments.Render3D/README.md KhaozEngine.Render3D/README.md
    KhaozEngine.TileWorld.Render3D/README.md
  )
  git add -- "${task_paths[@]}"
  git commit --only -m 'release(21.0.0): stage skinned attachment parts' -- "${task_paths[@]}"
  ```

- [ ] **Open the draft PR and collect the exact-head engine gate.** Push `feature/skinned-attachment-parts`, open a draft PR to main with the concise problem/change/validation description prepared in `/tmp/khaozengine-skinned-attachment-parts-pr.md` through `gh pr create --draft --base main --head feature/skinned-attachment-parts --title "Add skinned attachment parts" --body-file /tmp/khaozengine-skinned-attachment-parts-pr.md`, and link it to the owning T3 thread when that tool is available. Follow the rigid attachment design's replacement integration gate, not the older feature-push assumption: draft PR `ci` and PR-tier `cross-platform-gpu` must be green at the reconciled head containing current main, so its PR merge tree equals its head tree. Record all required PR checks and the head SHA. Dispatch one unfiltered hosted proof for that exact head with `gh workflow run cross-platform-gpu.yml --ref feature/skinned-attachment-parts -f legs=all -f bake=false -f tier=push`. Verify the returned run tests that head before treating it as evidence. Require new and existing attachment facts on all three backends without skips and a passing Vulkan synchronization validation leg with its validation producer armed. Historical allowed failures from the rigid feature are not an automatic waiver for this branch. Any red check requires attribution and the owning orchestrator's decision before integration. Main movement invalidates the gate until reconciliation, verification and fresh head checks complete.
- [ ] **Stop at the orchestrator handoff.** Return commit, draft PR, version, build/test/guard summaries, private package evidence, hosted run URLs, per-backend attachment results and blockers. A whole-branch reviewer checks spec coverage and implementation before the orchestrator integrates. The orchestrator performs the clean merge, main push, verifies actual main push checks, and runs `scripts/pack-local-feed.sh` from clean main only after `origin/main` contains the commit. It reports a failed main check without a revert/force operation. This plan authorizes no merge or tag by the implementation workers. Consumer adoption waits for owner approval of the exact major tag, the engine-rooted release handoff and independent tag/CI/publish/package proof.

## Self-review and coverage map

- Spec 3.1 is Task 1, including all three real shipped stone fixtures, v1 semantic hashes, mixed v2 and strict member refusal.
- Spec 3.2 is Tasks 2 and 3, covering actual accessors, shared container policy, joint/influence caps and literal vertex/map colour parity.
- Spec 3.3 is Task 6. Explicit host mesh is required because the mapped Skeleton does not hold inverse binds.
- Spec 3.4 is Tasks 4 and 7, plus Task 8's rendered acceptance. All template fields, material keys, previous palettes, allocation and both outline presentations are pinned.
- Spec 3.5 is Tasks 1 and 9. Fixed names are unchanged. Every mapped production/test call site migrates, while historical rigid-specific test names remain descriptive.
- Spec section 6's Engine bullet is Tasks 1-9, with final hosted Metal/D3D11/Vulkan and exact-head draft PR gates. Sections 4-5 and owner/game acceptance remain in the parallel game plan.
- All five Review Focus cases have named tests in their owning tasks. Tasks expose only the signatures later work needs. No unplaced engine requirement, speculative compatibility alias or automatic release is included.
