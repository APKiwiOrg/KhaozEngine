# GltfLoader rigid node-transform baking — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `GltfLoader.BuildRigid` bake node world transforms (position by the world matrix; normal + tangent.xyz by the normal matrix) so rigid glTF positioned via the scene graph loads correctly, matching the skinned path — without changing identity-node output.

**Architecture:** `BuildRigid` currently iterates `root.LogicalMeshes` and copies raw POSITION/NORMAL/TANGENT, ignoring the scene graph. Rework it to iterate meshes but emit one transformed copy per referencing node (`node.WorldMatrix`); meshes with no node load once at identity (preserves the historical mesh-walk for orphan/pre-baked assets). Per node: POSITION → `Vector3.Transform`, NORMAL + TANGENT.xyz → normal matrix (`Transpose(Invert(world))`) renormalized, TANGENT.w preserved. An exact-identity world matrix is a no-op fast path → byte-identical output. `MeshAssembler` and `LoadWithMaterial` are unchanged in shape (per-primitive base color still read per primitive).

**Tech Stack:** C# / net10.0, SharpGLTF (`ModelRoot`, `Node.WorldMatrix`, `Node.Mesh`), `System.Numerics` (`Matrix4x4`, `Vector3.Transform`/`TransformNormal`), xUnit, `SharpGLTF.Geometry`/`.Scenes`/`.Materials` for in-process test assets.

## Global Constraints

- Engine version line: single `<KhaozEngine5xVersion>` in `Directory.Build.props`. Bump **7.53.0 → 7.53.1** (PATCH; conformance fix). Highest existing tag is `v7.53.0`.
- Every behaviour change updates BOTH `CHANGELOG.md` (newest-first detailed) AND `CHANGENOTES.md` (newest-first one-line digest), in the same commit as the version bump.
- Update the 3 guard-checked declarations: `docs/CONSUMERS.md` "Engine current version", `docs/ROADMAP.md` "Current released version", `README.md` `<PackageReference>` example. Run `scripts/check-doc-versions.sh` (must pass).
- No em-dashes anywhere. Terse commit subjects `area(scope): summary` with the new version as scope on the bump commit.
- Scope: rigid static meshes only. NO morph targets, NO rigid-mesh animation, NO camera/light nodes, NO change to the skinned path or the asset pipeline/scatter.
- Regression sweep result (already done, record it): 7 engine assets (`TerrainWalkSample/assets/props/{oak_a,oak_b,pine_a,pine_b,pine_c,rock_a,rock_b}.glb`) carry non-identity mesh-node transforms (translation + uniform scale, NO rotation). They are loaded only by `TerrainWalkSample` via `PropLoader.LoadProp` → `Normalize`, which renormalizes (bbox → height, drop base, recenter) and is algebraically invariant to translation+uniform-scale, so output is unchanged. NO GPU golden or automated test loads any affected asset. All consumer assets + `asteroid.glb`/`testmodel.glb` are identity-node (byte-identical). **Zero golden re-bakes; no test changes; no visual regression.**

---

### Task 1: Failing tests for node-transform baking

**Files:**
- Test: `KhaozEngine.Tests/Render3D/GltfLoaderNodeTransformTests.cs` (create)

**Interfaces:**
- Consumes: `GltfLoader.Load(string path) -> GltfMesh`; `GltfMesh.Vertices : ModelVertex[]` with `Position`, `Normal`, `Tangent (Vector4)`; `SharpGLTF.Scenes.SceneBuilder.AddRigidMesh(IMeshBuilder, Matrix4x4)`, `SceneBuilder.ToGltf2().SaveGLB(path)`.
- Produces: nothing (tests only).

- [ ] **Step 1: Write the failing tests.** Helper builds a rigid triangle glb with one or more node transforms and explicit per-vertex normals; tests assert:
  1. `TranslatedNode_PlacesGeometryAtTransformedPosition` — triangle authored at local origin, node `CreateTranslation(10,0,0)`; loaded positions' min.X ≈ 10 (not 0).
  2. `RotatedNode_TransformsNormals` — authored normal +Z, node `CreateRotationX(π/2)`; loaded normal ≈ (0,-1,0).
  3. `NonUniformScale_UsesNormalMatrixNotWorldMatrix` — authored normal (1,1,0)/√2, node scale diag(2,1,1); loaded normal ≈ normalize(1,2,0)=(0.447,0.894,0), and clearly NOT normalize(2,1,0)=(0.894,0.447,0).
  4. `MeshInstancedByMultipleNodes_EmitsOneCopyPerNode` — one mesh, added at `(+10,0,0)` and `(-10,0,0)`; loaded mesh has 6 verts, some min.X≈-10 and some max.X≈+10.
  5. `IdentityNode_PassesGeometryThroughUnchanged` — triangle authored at distinct coords, node identity; each loaded position bit-exactly equals one authored position (no transform applied).
  6. `NodeTransform_EqualsPreBakedGeometry` — mesh A authored at translated coords placed at identity; mesh B authored at local coords placed via the same translation; loaded A and B have identical position sets (node baking == pre-baking).
- [ ] **Step 2: Run the tests, verify they FAIL.** `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter FullyQualifiedName~GltfLoaderNodeTransform` — expect tests 1–4/6 to fail (positions at origin / normals untransformed / single copy).
- [ ] **Step 3: Commit the failing tests.** `test(render3d): node-transform baking tests for BuildRigid (red)`.

### Task 2: Implement node-transform baking in BuildRigid

**Files:**
- Modify: `KhaozEngine.Render3D/Models/GltfLoader.cs` (`BuildRigid`; add private `AppendMeshCorners`)

**Interfaces:**
- Consumes: `ModelRoot.LogicalMeshes`, `ModelRoot.LogicalNodes`, `Node.Mesh`, `Node.WorldMatrix`; `Matrix4x4.IsIdentity`, `Matrix4x4.Invert`, `Matrix4x4.Transpose`; `Vector3.Transform`, `Vector3.TransformNormal`.
- Produces: unchanged public surface (`BuildRigid` still returns `GltfMesh`; `LoadWithMaterial` unaffected).

- [ ] **Step 1: Rewrite `BuildRigid`** to iterate `root.LogicalMeshes`; for each mesh, call `AppendMeshCorners(corners, mesh, node.WorldMatrix)` for every `LogicalNode` whose `Mesh == mesh`, or once with `Matrix4x4.Identity` if no node references it. Keep the `corners.Count == 0` throw and `MeshAssembler.Build(corners)`.
- [ ] **Step 2: Add `AppendMeshCorners(List<MeshCorner>, Mesh, Matrix4x4 world)`** — compute `identity = world.IsIdentity`; `normalMatrix = Invert(world,out inv) ? Transpose(inv) : world`. Per primitive: read POSITION/NORMAL/TEXCOORD_0/TANGENT + base color exactly as today; local funcs transform POSITION via `Vector3.Transform` (identity → raw), NORMAL + TANGENT.xyz via `Vector3.TransformNormal(normalMatrix)` + renormalize (identity → raw), TANGENT.w preserved; emit 3 corners per triangle as today.
- [ ] **Step 3: Run the new tests, verify PASS.** `dotnet test ... --filter FullyQualifiedName~GltfLoaderNodeTransform`.
- [ ] **Step 4: Run the full suite, verify all green** (incl. existing GltfLoader/Prop/golden tests). `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`.
- [ ] **Step 5: Commit.** `render3d: BuildRigid bakes node world transforms (glTF conformance)`.

### Task 3: Docs sweep, version bump, pack, release

**Files:**
- Modify: `Directory.Build.props` (7.53.0 → 7.53.1), `CHANGELOG.md`, `CHANGENOTES.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`, `docs/USING-KHAOZENGINE.md`.

- [ ] **Step 1: Bump** `<KhaozEngine5xVersion>` to `7.53.1`.
- [ ] **Step 2: CHANGELOG.md** newest-first entry: BuildRigid now bakes node world transforms (position by world matrix, normal+tangent by the normal matrix); identity-node assets byte-identical; instancing emits N copies; note no golden affected.
- [ ] **Step 3: CHANGENOTES.md** one-line digest.
- [ ] **Step 4: Guard declarations** — `docs/CONSUMERS.md` "Engine current version", `docs/ROADMAP.md` "Current released version", `README.md` `<PackageReference>` example → `7.53.1`.
- [ ] **Step 5: USING note** — rigid glTF now honours node transforms; kit-ingest `transform_apply` no longer required for placement (still harmless).
- [ ] **Step 6:** `bash scripts/check-doc-versions.sh` → passes.
- [ ] **Step 7:** `dotnet pack -c Release -o ./local-feed` (from the worktree; repack from main root after merge).
- [ ] **Step 8: Commit** the bump+docs as one commit `render3d(7.53.1): rigid glTF honours node transforms`.
- [ ] **Step 9: Release** — merge `feature/gltf-node-transforms` into `main`, repack from main root to `local-feed`, `git tag v7.53.1`, push `main` + tag, remove worktree, delete branch.

## Self-Review

- Spec coverage: BuildRigid node walk (Task 2) ✓; normal matrix (Task 2 step 2, Task 1 test 3) ✓; instancing N copies (test 4) ✓; identity byte-identical (tests 5/6) ✓; LoadWithMaterial alignment — base color still read per primitive, no change needed ✓; regression sweep + golden statement (Global Constraints) ✓; patch bump + docs + USING note (Task 3) ✓; out-of-scope items untouched ✓.
- Placeholder scan: none.
- Type consistency: `AppendMeshCorners(List<MeshCorner>, Mesh, Matrix4x4)`, `MeshCorner(Vector3, Vector3?, Vector4, Vector2, Vector4?)`, `GltfMesh.Vertices[i].{Position,Normal,Tangent}` consistent throughout.
