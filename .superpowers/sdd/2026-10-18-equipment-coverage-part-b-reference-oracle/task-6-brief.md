### Task 6: Evaluate per-pose enclosures and expose Part C's internal API

**Files**

- Create: `KhaozEngine.Render3D/Coverage/CoverageReferenceEnclosure.cs`
- Create: `KhaozEngine.Render3D/Coverage/CoverageReferenceEnclosureScratch.cs`
- Modify: `KhaozEngine.Render3D/Coverage/CoverageReferencePrecision.cs`, compose outward host/equipment maxima
- Test: `KhaozEngine.Render.Tests/Render3D/CoverageReferenceEnclosureTests.cs`

**Interfaces**

- Consumes: Tasks 2 to 5's scalar operation graph, immutable topology/support data and rounded CPU frame uniforms. `composedPalette` is the exact binary32 upload produced by `SkinningMath.Compose`, not joint matrices composed a second time on the device.
- Produces: `internal sealed class CoverageReferenceEnclosureScratch`, constructor `(CoverageRefinement topology, int commandCapacity = CoverageScratch.MaxCommands)`. Preallocate source, refined, group, face and final interval arrays plus branch/guard state. Expose `internal ReadOnlySpan<CoverageVertexEnclosure> HostModelBounds`, `HostWorldBounds` and `internal double DeviceTolerance`, invalidated by the next evaluation. Store no variable-size equipment array.
- Produces: `internal static void CoverageReferenceEnclosure.Evaluate(CoverageRefinement topology, ReadOnlySpan<Matrix4x4> composedPalette, ReadOnlySpan<ModelVertex> referenceFinal, in Matrix4x4 model, ReadOnlySpan<CoverageCommandPose> commands, CoverageReferenceScratch referenceScratch, CoverageReferenceEnclosureScratch scratch)`.
- Produces: `internal static CoverageVertexEnclosure CoverageReferenceEnclosure.EquipmentWorldVertex(CoverageSourceTopology source, int vertex, in Matrix4x4 equipmentWorld)`. This value API exposes any full-solid vertex without per-call storage. Evaluate streams these values into its equipment maximum.
- Evaluate neither decides the budget refusal nor invokes certification. No finite proof returns unbounded boxes and `DeviceTolerance=PositiveInfinity`. Invalid lengths or scratch identity throw before mutation. Each evaluation resets active lengths, maxima and branch/guard state.

- [ ] Write `Every_fixture_float_vertex_is_inside_its_pose_enclosure` over all 72 frozen cases, every seam member and zero/half/full transition. Assert `HostModelBounds.Length==topology.VertexCount`, `HostModelBounds[i].Contains(referenceFinal[i].Position)` for every vertex, finite model/world bounds and finite epsilon. Compute world centers independently with outward binary64 arithmetic and assert their inclusion. Report RV1 conflicts or unbounded fixture rows, never omit them.

```csharp
Assert.Equal(topology.VertexCount, enclosure.HostModelBounds.Length);
Assert.True(enclosure.HostModelBounds[i].Contains(referenceFinal[i].Position));
Assert.True(enclosure.HostWorldBounds[i].X.IsFinite);
Assert.True(enclosure.HostWorldBounds[i].Y.IsFinite);
Assert.True(enclosure.HostWorldBounds[i].Z.IsFinite);
Assert.True(double.IsFinite(enclosure.DeviceTolerance));
Assert.True(CoverageReferencePrecision.Radius(deviceBox, zeroCenter) >= 5d);
Assert.True(CoverageReferencePrecision.ComposeTolerance(5d, 12d) >= 17d);
```

  Add `Enclosures_propagate_conditioning_flushing_and_branch_alternatives`. Assert the skinny-triangle query interval contains both analytical heights. Reducing positive correction rate from `2^-10` to `2^-20` increases correction width. A divisor interval crossing zero is unbounded. At an uncertain support edge or nearest-edge tie both candidate outputs belong to the joined box. An uncertain guard encloses covered and uncovered positions. A final-store case contains the adjacent permitted floats and CPU value. Tests must not replay only the CPU-selected branch.

  Add `World_tolerance_sums_host_and_full_equipment_bounds`. With independent host box `[-3,3] x [-4,4] x [0,0]` centered at zero and equipment radius `12`, assert host radius at least `5`, combined epsilon at least `17`, and excess limited to the specified outward binary64 steps. A large finite equipment vertex outside the inward-sheet subset must contribute transform error. Test nonuniform models, negative coordinates, large-translation cancellation and protected vertices. Reordered commands produce identical epsilon and ID-keyed bounds. Unbounded then valid evaluation reproduces the first valid bounds and epsilon exactly.

- [ ] Run to fail:

```bash
build-slot --label ec-b6-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~CoverageReferenceEnclosureTests'
```

  Expected: pose enclosure types are absent.

- [ ] Implement the declared API as a second evaluation through Task 2's interval set. Expand skin-matrix blending, source point/direction transforms, canonical interpolation, conformation, analytic face overlays, core correction, transition, normal guards, final selection/store and world transforms in reference order. Uploaded frame fields are point inputs. Normal guard arithmetic affects covered/uncovered selection even when normals do not move positions. Keep the evaluator separate from float construction and do not change existing constructor refusal codes.

  Apply NC1's widening, subnormal operand hull, domains, candidate inclusion and branch joins. Evaluate full-equipment transforms from `Support.Source.Positions` and `EquipmentWorld`. Centers match exact geometry certification, using outward binary64 arithmetic without device widening. Compute component radii, maximum Euclidean host/equipment radii and `ComposeTolerance`. Expose host model bounds for future GPU-buffer tests and world bounds for epsilon composition. Implement no native tests or dev readback here.

- [ ] Run to pass and finish:

```bash
build-slot --label ec-b6-build -- dotnet build KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release
build-slot --label ec-b6-tests -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~CoverageReferenceEnclosureTests|FullyQualifiedName~CoverageReferencePrimitiveTests|FullyQualifiedName~CoverageReferenceFidelityTests'
build-slot --label ec-b6-engine-format -- dotnet format whitespace KhaozEngine.Render3D/KhaozEngine.Render3D.csproj --verify-no-changes --no-restore --include Coverage/CoverageReferenceEnclosure.cs Coverage/CoverageReferenceEnclosureScratch.cs Coverage/CoverageReferencePrecision.cs
build-slot --label ec-b6-test-format -- dotnet format whitespace KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj --verify-no-changes --no-restore --include Render3D/CoverageReferenceEnclosureTests.cs
```

  Expected: zero warnings, every fixture's CPU output contained, every fixture bound finite, domain/conditioning/branch tests pass, format exit 0. These are acceptance gates, not proofs obtained during planning.

- [ ] Commit listed paths with `render3d(coverage): enclose reference poses for device equivalence`.

