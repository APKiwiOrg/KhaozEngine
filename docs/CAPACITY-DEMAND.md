# Evaluated capacity demand

`KhaozEngine.Diagnostics.CapacityDemandDiagnostics.Observe(Action<CapacityDemand>)` subscribes to
count-only evaluated demand. Dispose the returned subscription when the measurement finishes.
The callback receives a value record directly, without reflection or a logging sink. It runs on the
evaluating thread, including preparation workers, so a shared collector must synchronize its storage.
Keep a high water per key instead of retaining every event from a large world.

```csharp
using var observation = CapacityDemandDiagnostics.Observe(demand =>
{
    collector.Record(demand);
});
```

Each event names its site and limit and carries current demand, proposed increment, requested total,
allowance and refusal predicate. An allowance can be an effective remaining view or an original profile
allowance when the gate carries origin provenance. Numeric admission identities
are stable within the process for the lifetime of the counter owner. Operation, parent operation and root operation
identities distinguish nested acquisitions, cluster cells and windows. They are diagnostic identities,
not hashes, document keys or content. The stream never publishes authored identifiers, source bytes,
geometry, assets or mutable admission objects.

`FinalInventory` samples retained preparation inventory after successful publication.
`Inventory` counts an attempted retained set, `Transient` counts simultaneous live storage or a temporary peak,
and `Cumulative` counts work spent across the admission. A snapshot gate reports `0 + total`.
An incremental gate reports `current + increment`. A byte view reports its effective allowance,
including reused or already retained storage. Counts wider than decimal saturate at `decimal.MaxValue` and set `Saturated`, so they remain explicit lower bounds.
An inventory attempt is not a promise that the enclosing
operation completed or retained its output. Group by admission, site, effective limit and operation
when computing maxima. Summing all events double-counts probes, shared views and repeated checks.

Origin-aware shared gates publish one event for each originating profile field that contributes to
their effective allowance. Each origin carries its consumed baseline and original ceiling. For example, a membership
context with four prism vertices and six demanded faces publishes `MaxInspectedFaces` as `4 + 6`
against the original query ceiling. A min of sample limits publishes `MaxSamples` and
`MaxQuerySamples` separately, with each origin's own comparison. A refusal at one origin does not
mark a larger origin refused. At these gates, collectors grouping by `LimitName` recover full profile demand
without decoding a site name or adding a consumed remainder themselves. The effective admission
predicate remains unchanged. Selected-asset refusals use the selected-set allowance identity and
include already admitted parts/bytes plus the rejected piece's requested increment.

Origin provenance currently covers cold membership faces, query-derived patch ceilings, foliage
support and exceptional-owner contexts, instance sample minima and native chunk sample remainders,
paint target patch remainders, and selected-asset refusals. Foliage input query-cap intersections and
nested decode/retention byte views still report effective allowances. Those views are not complete
originating-profile measurements.

The diagnostic returns the original predicate unchanged. It does not reserve, refund, retry, continue,
raise an allowance or materialize rejected work. Observer exceptions are isolated from the engine.
Without an observer, checks and operation scopes allocate no extra storage. Observed calls may allocate
identities, scope frames and collector storage. Collect outside allocation measurements.

For profile sizing, first supply permissive explicit limits, then observe a complete representative
build. A refusal reports the evaluated attempted amount and stops at the same place as an unobserved
run. It cannot reveal work beyond that refusal. Queue and per-update limits also expose probes and
throttles, so an exceeded gate can mean deferred work or eviction rather than a failed scene.

## Limit sites

The same scalar can bound several different sets. These are separate sizing observations.
Query and foliage context sites report original profile allowances and consumed baselines.
The source contract below also lists helper expressions whose runtime limit names come from callers.

| Limit | Evaluated sites |
| --- | --- |
| `Array.MaxLength` | `MapRenderExtentValidator` |
| `MaxActiveWindows` | `MapRenderResidency` |
| `MaxAppliesPerUpdate` | `MapRenderResidency` |
| `MaxBindings` | `MapPrefabPreflight` |
| `MaxBoundCells` | `MapWaterWorkBudget` |
| `MaxBoundFaces` | `MapWaterWorkBudget` |
| `MaxCandidatePatches` | `MapBoundAcquisition`, `MapBoundPreparation (query-derived context allowance)`, `MapComposedSurfaceSource`, `MapDocumentSurfaceSource`, `MapEffectiveAcquisition`, `MapEffectiveSurfaceSource`, `MapFoliageCandidateSupport (via bound preparation)`, `MapScopedAcquisition`, `MapSpaceMembershipScratch`, `MapSurfaceQuery`, `MapSurfaceSessionState` |
| `MaxCandidates` | `MapFoliageDistribution`, `MapFoliageGenerationAdmission`, `MapFoliageInstanceDistribution` |
| `MaxCaptureLights` | `MapCaptureDependencies` |
| `MaxCaptureScopes` | `MapCaptureDependencies` |
| `MaxCells` | `MapWaterScopedCompiler`, `NativeMapWater` |
| `MaxChangeRows` | `MapFoliageDifferential` |
| `MaxChildren` | `MapPrefabPreflight` |
| `MaxChunkDescriptors` | `MapFoliageContextPreflight`, `MapFoliageWindowCapture`, `MapSceneAdmission` |
| `MaxComparisonPreparedChunks` | `MapFoliageDistribution` |
| `MaxComparisonRetainedBytes` | `MapFoliageDistribution` |
| `MaxContextFaces` | `MapBoundFaceContext`, `MapPartialPaintCompiler` |
| `MaxContextPatches` | `MapBoundPreparation`, `MapPartialPaintCompiler` |
| `MaxDecodedBytes` | `MapRenderAssetAllowance`, `MapRenderAssets` |
| `MaxDifferentialCandidates` | `MapFoliageDifferential` |
| `MaxDisposalsPerUpdate` | `MapRenderResidency` |
| `MaxEmittedVertices` | `MapWaterResourceCell`, `MapWaterScopedCompiler`, `NativeMapWater` |
| `MaxFaces` | `MapBoundFaceContext (foliage origin)`, `MapFoliageSupportBuilder` |
| `MaxFacesPerPatch` | `MapBoundFaceContext`, `MapSpaceMembershipScratch`, `MapSurfaceCompiler` |
| `MaxFootprintCells` | `MapWaterWorkBudget` |
| `MaxGeometryElements` | `MapPrefabPreflight` |
| `MaxImageBufferPixels` | `GltfDecodeAdmission`, `ImageRgba`, `MapRenderMaterials` |
| `MaxImagePixels` | `GltfDecodeAdmission`, `ImageRgba`, `MapRenderMaterials` |
| `MaxImages` | `GltfDecodeAdmission`, `MapRenderMaterials` |
| `MaxInFlightUploadCpuBytes` | `MapRenderAdmission`, `MapRenderUploadPreflight` |
| `MaxInFlightUploadStagingGpuBytes` | `MapRenderAdmission`, `MapRenderUploadPreflight` |
| `MaxIndices` | `GltfDecodeAdmission` |
| `MaxInspectedFaces` | `MapBoundFaceContext (query-derived face allowance)`, `MapPrismGeometry`, `MapSpaceMembership`, `MapSpaceMembershipScratch` |
| `MaxInstances` | `MapPrefabPreflight` |
| `MaxLods` | `MapRenderAssets` |
| `MaxMaskCandidates` | `MapFoliageClearance` |
| `MaxMaterials` | `NativeMapTerrain` |
| `MaxMemberships` | `MapSceneMetadataReader` |
| `MaxMembershipsPerResource` | `MapSceneMetadataReader` |
| `MaxMetadataBytes` | `MapSceneMetadataReader`, `MapSceneSource` |
| `MaxMetadataRows` | `MapSceneMetadataReader`, `MapSceneSource` |
| `MaxOverrides` | `MapPrefabPreflight` |
| `MaxPageMetadataChecks` | `MapComposedSurfaceSource.Admission`, `MapPageBudget` |
| `MaxPageRangeOperations` | `MapComposedSurfaceSource.Admission`, `MapPageBudget` |
| `MaxPageReads` | `MapComposedSurfaceSource.Admission`, `MapPageBudget` |
| `MaxPaintTargets` | `MapFoliageContextPreflight`, `MapSceneAdmission` |
| `MaxPairChecks` | `MapEffectiveGeometry`, `MapPartialPaintCompiler`, `MapWaterWorkBudget` |
| `MaxParts` | `MapRenderAssetAllowance`, `MapRenderAssets` |
| `MaxPatchScopes` | `MapSceneAdmission` |
| `MaxPatches` | `MapPrefabPreflight` |
| `MaxPayloadBytes` | `MapPrefabPreflight` |
| `MaxPeakBytes` | `GltfDecodeAdmission`, `GltfDecodeBudget`, `ImageRgba` |
| `MaxPieceVertices` | `MapWaterWorkBudget` |
| `MaxPieces` | `MapWaterWorkBudget` |
| `MaxPlacements` | `MapFoliageContextPreflight`, `MapSceneAdmission` |
| `MaxPlanBytes` | `MapPreparationLedger.Demand` |
| `MaxPrepareScopes` | `MapFoliageOwners`, `MapFoliageSupport` |
| `MaxPreparedBytes` | `MapPreparationLedger.Demand`, `MapRoofEvaluationBuffer`, `MapSceneAdmission` |
| `MaxPreparedFaces` | `MapRoofEvaluationBuffer`, `MapSceneAdmission` |
| `MaxPreparedSides` | `MapFoliageDependents` |
| `MaxProofBytes` | `MapRenderExtentValidator` |
| `MaxQueryChunks` | `MapFoliageDistribution` |
| `MaxQueryRetainedBytes` | `MapFoliageColumnBudget`, `MapFoliageGenerationAdmission`, `MapFoliageRenderCache`, `MapFoliageRetentionBudget` |
| `MaxQuerySamples` | `MapFoliageInstanceBuilder`, `MapFoliageRetentionBudget`, `NativeMapFoliage` |
| `MaxQueuedBytes` | `MapPreparationLedger.Demand`, `MapRenderBuildQueue` |
| `MaxQueuedPlans` | `MapRenderBuildQueue` |
| `MaxRecordDepth` | `MapEffectiveAcquisition`, `MapMembershipPointKernel`, `MapOccupiedPartitions`, `MapScopedAcquisition` |
| `MaxRecordReads` | `MapEffectiveAcquisition`, `MapEffectiveSurfaceSource`, `MapOccupiedPartitions`, `MapScopedAcquisition`, `MapSpaceMembership`, `MapSpaceMembershipScratch` |
| `MaxRecords` | `MapPrefabPreflight` |
| `MaxReferenceEdges` | `MapPrefabPreflight` |
| `MaxRefinementFaces` | `MapEffectiveGeometry`, `MapOccupiedPartitions`, `MapPartialPaintCompiler` |
| `MaxRefinementVertices` | `MapEffectiveGeometry`, `MapOccupiedPartitions`, `MapPartialPaintCompiler` |
| `MaxResidentCpuBytes` | `MapRenderAdmission`, `MapRenderUploadPreflight` |
| `MaxResidentGpuBytes` | `MapRenderAdmission`, `MapRenderUploadPreflight` |
| `MaxRuleCandidates` | `MapFoliageClearance` |
| `MaxSamples` | `MapFoliageDistribution`, `MapFoliageInstanceBuilder` |
| `MaxScannedBatches` | `MapFoliageDependents` |
| `MaxScannedBytes` | `MapSceneMetadataReader` |
| `MaxSelectedResources` | `MapCaptureDependencies`, `MapSceneAcquisition`, `MapSceneAdmission`, `MapSceneSpatialIndex`, `NativeMapFoliage` |
| `MaxSourceFacesPerBound` | `MapBoundPreparation`, `MapEffectiveGeometry`, `MapPartialPaintCompiler`, `MapWaterBoundReads` |
| `MaxSourceUtf8Bytes` | `MapFoliageContextPreflight`, `MapSceneAdmission` |
| `MaxSupportIntersections` | `MapSupportSelection` |
| `MaxSurfacePatches` | `MapFoliageContextPreflight`, `MapSceneAdmission` |
| `MaxTiles` | `MapSceneSource` |
| `MaxTransfers` | `MapPrefabPreflight` |
| `MaxTriangles` | `MapWaterWorkBudget` |
| `MaxUploadBytesPerUpdate` | `MapRenderResidency` |
| `MaxVertices` | `GltfDecodeAdmission` |
| `MaxWorkers` | `MapRenderBuildQueue` |
| `SupportCandidateBuffer` | `MapSupportQuery` |

The support grid derived from `MaxFaces` also evaluates its cell ceiling `4 * MaxFaces + 64`.
`MaxPageMetadataChecks` and `MaxPageRangeOperations` derive from `1 + 4 * 256 * MaxPageReads`.
Context gates report their effective local context allowance, including the remaining query or foliage
allowance passed to them. Their sites are listed under both the local and originating profile limit.

## Source gate contract

The following exact source expressions are checked by `CapacityDemandSourceContractTests` against
all native-scene `Check` and typed-origin `Report` call sites. Dynamic names are intentional for ledger windows, typed refusal
translation, decode accessor kinds and shared admission helpers. Their caller profile and originating
identity choose the runtime limit. The `origin.LimitName` marker denotes an originating-profile
report whose consumed baseline and original allowance travel with its typed origin. The guard checks those expressions too, while behavioral tests
assert the resolved limit and baseline. This table is an inventory of evaluated gates, including
representation and throttle gates. It does not claim every site is reached by every build.

| Source | Site expression | Limit expression |
| --- | --- | --- |
| KhaozEngine.MapDoc.Render3D/MapCaptureDependencies.cs | `"MapCaptureDependencies.AddScope"` | `"MaxCaptureScopes"` |
| KhaozEngine.MapDoc.Render3D/MapCaptureDependencies.cs | `"MapCaptureDependencies.MaxCaptureLights"` | `"MaxCaptureLights"` |
| KhaozEngine.MapDoc.Render3D/MapCaptureDependencies.cs | `"MapCaptureDependencies.MaxSelectedResources"` | `"MaxSelectedResources"` |
| KhaozEngine.MapDoc.Render3D/MapCaptureDependencies.cs | `"MapCaptureDependencies.ScopeGrid"` | `"MaxCaptureScopes"` |
| KhaozEngine.MapDoc.Render3D/MapFoliageGenerationAdmission.cs | `"MapFoliageGenerationAdmission.MaxCandidates.1"` | `"MaxCandidates"` |
| KhaozEngine.MapDoc.Render3D/MapFoliageGenerationAdmission.cs | `"MapFoliageGenerationAdmission.MaxQueryRetainedBytes.1"` | `"MaxQueryRetainedBytes"` |
| KhaozEngine.MapDoc.Render3D/MapFoliageGenerationAdmission.cs | `"MapFoliageGenerationAdmission.MaxQueryRetainedBytes.2"` | `"MaxQueryRetainedBytes"` |
| KhaozEngine.MapDoc.Render3D/MapFoliageRenderCache.cs | `"MapFoliageRenderCache.MaxQueryRetainedBytes.1"` | `"MaxQueryRetainedBytes"` |
| KhaozEngine.MapDoc.Render3D/MapFoliageRenderCache.cs | `"MapFoliageRenderCache.MaxQueryRetainedBytes.2"` | `"MaxQueryRetainedBytes"` |
| KhaozEngine.MapDoc.Render3D/MapRenderAdmission.cs | `"MapRenderAdmission.MaxInFlightUploadCpuBytes.1"` | `"MaxInFlightUploadCpuBytes"` |
| KhaozEngine.MapDoc.Render3D/MapRenderAdmission.cs | `"MapRenderAdmission.MaxInFlightUploadStagingGpuBytes.1"` | `"MaxInFlightUploadStagingGpuBytes"` |
| KhaozEngine.MapDoc.Render3D/MapRenderAdmission.cs | `"MapRenderAdmission.MaxResidentCpuBytes.1"` | `"MaxResidentCpuBytes"` |
| KhaozEngine.MapDoc.Render3D/MapRenderAdmission.cs | `"MapRenderAdmission.MaxResidentCpuBytes.2"` | `"MaxResidentCpuBytes"` |
| KhaozEngine.MapDoc.Render3D/MapRenderAdmission.cs | `"MapRenderAdmission.MaxResidentGpuBytes.1"` | `"MaxResidentGpuBytes"` |
| KhaozEngine.MapDoc.Render3D/MapRenderAssetAllowance.cs | `"MapRenderAssetAllowance.MaxDecodedBytes.1"` | `"MaxDecodedBytes"` |
| KhaozEngine.MapDoc.Render3D/MapRenderAssetAllowance.cs | `"MapRenderAssetAllowance.MaxParts.1"` | `"MaxParts"` |
| KhaozEngine.MapDoc.Render3D/MapRenderAssetAllowance.cs | `"MapRenderAssetAllowance.Refuse"` | `capacity.LimitName` |
| KhaozEngine.MapDoc.Render3D/MapRenderAssets.cs | `"MapRenderAssets.MaxDecodedBytes.1"` | `"MaxDecodedBytes"` |
| KhaozEngine.MapDoc.Render3D/MapRenderAssets.cs | `"MapRenderAssets.MaxLods.1"` | `"MaxLods"` |
| KhaozEngine.MapDoc.Render3D/MapRenderAssets.cs | `"MapRenderAssets.MaxParts.1"` | `"MaxParts"` |
| KhaozEngine.MapDoc.Render3D/MapRenderBuildQueue.cs | `"MapRenderBuildQueue.Bytes"` | `nameof(MapRenderBudgets.MaxQueuedBytes)` |
| KhaozEngine.MapDoc.Render3D/MapRenderBuildQueue.cs | `"MapRenderBuildQueue.Plans"` | `nameof(MapRenderBudgets.MaxQueuedPlans)` |
| KhaozEngine.MapDoc.Render3D/MapRenderBuildQueue.cs | `"MapRenderBuildQueue.Workers"` | `nameof(MapRenderBudgets.MaxWorkers)` |
| KhaozEngine.MapDoc.Render3D/MapRenderExtentValidator.cs | `"MapRenderExtentValidator.Array.MaxLength.1"` | `"Array.MaxLength"` |
| KhaozEngine.MapDoc.Render3D/MapRenderExtentValidator.cs | `"MapRenderExtentValidator.MaxProofBytes.1"` | `"MaxProofBytes"` |
| KhaozEngine.MapDoc.Render3D/MapRenderExtentValidator.cs | `"MapRenderExtentValidator.MaxProofBytes.2"` | `"MaxProofBytes"` |
| KhaozEngine.MapDoc.Render3D/MapRenderMaterials.cs | `"MapRenderMaterials.MaxImageBufferPixels.1"` | `"MaxImageBufferPixels"` |
| KhaozEngine.MapDoc.Render3D/MapRenderMaterials.cs | `"MapRenderMaterials.MaxImagePixels.1"` | `"MaxImagePixels"` |
| KhaozEngine.MapDoc.Render3D/MapRenderMaterials.cs | `"MapRenderMaterials.MaxImages.1"` | `"MaxImages"` |
| KhaozEngine.MapDoc.Render3D/MapRenderResidency.cs | `"MapRenderResidency.Applies"` | `nameof(MapRenderBudgets.MaxAppliesPerUpdate)` |
| KhaozEngine.MapDoc.Render3D/MapRenderResidency.cs | `"MapRenderResidency.Disposals"` | `nameof(MapRenderBudgets.MaxDisposalsPerUpdate)` |
| KhaozEngine.MapDoc.Render3D/MapRenderResidency.cs | `"MapRenderResidency.MaxActiveWindows.1"` | `"MaxActiveWindows"` |
| KhaozEngine.MapDoc.Render3D/MapRenderResidency.cs | `"MapRenderResidency.UploadBytes"` | `nameof(MapRenderBudgets.MaxUploadBytesPerUpdate)` |
| KhaozEngine.MapDoc.Render3D/MapRenderUploadPreflight.cs | `"MapRenderUploadPreflight.3288.1"` | `name` |
| KhaozEngine.MapDoc.Render3D/MapRenderUploadPreflight.cs | `"MapRenderUploadPreflight.ResidentCapacity.1"` | `name` |
| KhaozEngine.MapDoc.Render3D/NativeMapFoliage.cs | `"NativeMapFoliage.MaxQuerySamples.1"` | `"MaxQuerySamples"` |
| KhaozEngine.MapDoc.Render3D/NativeMapFoliage.cs | `"NativeMapFoliage.MaxSelectedResources.1"` | `"MaxSelectedResources"` |
| KhaozEngine.MapDoc.Render3D/NativeMapTerrain.cs | `"NativeMapTerrain.MaterialUnion"` | `"MaxMaterials"` |
| KhaozEngine.MapDoc.Render3D/NativeMapTerrain.cs | `"NativeMapTerrain.MaxMaterials"` | `"MaxMaterials"` |
| KhaozEngine.MapDoc.Render3D/NativeMapWater.cs | `"NativeMapWater.MaxCells.1"` | `"MaxCells"` |
| KhaozEngine.MapDoc.Render3D/NativeMapWater.cs | `"NativeMapWater.MaxEmittedVertices.1"` | `"MaxEmittedVertices"` |
| KhaozEngine.MapDoc.Render3D/NativeMapWater.cs | `"NativeMapWater.MaxEmittedVertices.2"` | `"MaxEmittedVertices"` |
| KhaozEngine.MapDoc/Foliage/MapFoliageClearance.cs | `"MapFoliageClearance.MaxMaskCandidates.1"` | `"MaxMaskCandidates"` |
| KhaozEngine.MapDoc/Foliage/MapFoliageClearance.cs | `"MapFoliageClearance.MaxRuleCandidates.1"` | `"MaxRuleCandidates"` |
| KhaozEngine.MapDoc/Foliage/MapFoliageClearance.cs | `"MapFoliageClearance.MaxRuleCandidates.2"` | `"MaxRuleCandidates"` |
| KhaozEngine.MapDoc/Foliage/MapFoliageColumnBudget.cs | `"MapFoliageColumnBudget.MaxQueryRetainedBytes.1"` | `"MaxQueryRetainedBytes"` |
| KhaozEngine.MapDoc/Foliage/MapFoliageContextPreflight.cs | `"MapFoliageContextPreflight.MaxChunkDescriptors.1"` | `"MaxChunkDescriptors"` |
| KhaozEngine.MapDoc/Foliage/MapFoliageContextPreflight.cs | `"MapFoliageContextPreflight.MaxPaintTargets.1"` | `"MaxPaintTargets"` |
| KhaozEngine.MapDoc/Foliage/MapFoliageContextPreflight.cs | `"MapFoliageContextPreflight.MaxPlacements.1"` | `"MaxPlacements"` |
| KhaozEngine.MapDoc/Foliage/MapFoliageContextPreflight.cs | `"MapFoliageContextPreflight.MaxSurfacePatches.1"` | `"MaxSurfacePatches"` |
| KhaozEngine.MapDoc/Foliage/MapFoliageContextPreflight.cs | `"MapFoliageContextPreflight.SourceBytes"` | `nameof(MapFoliageContextLimits.MaxSourceUtf8Bytes)` |
| KhaozEngine.MapDoc/Foliage/MapFoliageContextPreflight.cs | `"MapFoliageContextPreflight.SourceCharge"` | `nameof(MapFoliageContextLimits.MaxSourceUtf8Bytes)` |
| KhaozEngine.MapDoc/Foliage/MapFoliageDependents.cs | `"MapFoliageDependents.MaxPreparedSides"` | `"MaxPreparedSides"` |
| KhaozEngine.MapDoc/Foliage/MapFoliageDependents.cs | `"MapFoliageDependents.MaxScannedBatches"` | `"MaxScannedBatches"` |
| KhaozEngine.MapDoc/Foliage/MapFoliageDifferential.cs | `"MapFoliageDifferential.MaxChangeRows.1"` | `"MaxChangeRows"` |
| KhaozEngine.MapDoc/Foliage/MapFoliageDifferential.cs | `"MapFoliageDifferential.MaxChangeRows.2"` | `"MaxChangeRows"` |
| KhaozEngine.MapDoc/Foliage/MapFoliageDifferential.cs | `"MapFoliageDifferential.MaxChangeRows.3"` | `"MaxChangeRows"` |
| KhaozEngine.MapDoc/Foliage/MapFoliageDifferential.cs | `"MapFoliageDifferential.MaxDifferentialCandidates.1"` | `"MaxDifferentialCandidates"` |
| KhaozEngine.MapDoc/Foliage/MapFoliageDifferential.cs | `"MapFoliageDifferential.MaxDifferentialCandidates.2"` | `"MaxDifferentialCandidates"` |
| KhaozEngine.MapDoc/Foliage/MapFoliageDistribution.cs | `"MapFoliageDistribution.MaxCandidates.1"` | `"MaxCandidates"` |
| KhaozEngine.MapDoc/Foliage/MapFoliageDistribution.cs | `"MapFoliageDistribution.MaxComparisonPreparedChunks.1"` | `"MaxComparisonPreparedChunks"` |
| KhaozEngine.MapDoc/Foliage/MapFoliageDistribution.cs | `"MapFoliageDistribution.MaxComparisonPreparedChunks.2"` | `"MaxComparisonPreparedChunks"` |
| KhaozEngine.MapDoc/Foliage/MapFoliageDistribution.cs | `"MapFoliageDistribution.MaxComparisonRetainedBytes.1"` | `"MaxComparisonRetainedBytes"` |
| KhaozEngine.MapDoc/Foliage/MapFoliageDistribution.cs | `"MapFoliageDistribution.MaxQueryChunks.1"` | `"MaxQueryChunks"` |
| KhaozEngine.MapDoc/Foliage/MapFoliageDistribution.cs | `"MapFoliageDistribution.MaxQueryChunks.2"` | `"MaxQueryChunks"` |
| KhaozEngine.MapDoc/Foliage/MapFoliageDistribution.cs | `"MapFoliageDistribution.MaxSamples.1"` | `"MaxSamples"` |
| KhaozEngine.MapDoc/Foliage/MapFoliageInstanceBuilder.cs | `"MapFoliageInstanceBuilder.MaxQuerySamples.1"` | `origin.LimitName` |
| KhaozEngine.MapDoc/Foliage/MapFoliageInstanceBuilder.cs | `"MapFoliageInstanceBuilder.MaxSamples"` | `"MaxSamples"` |
| KhaozEngine.MapDoc/Foliage/MapFoliageInstanceDistribution.cs | `"MapFoliageInstanceDistribution.MaxCandidates.1"` | `"MaxCandidates"` |
| KhaozEngine.MapDoc/Foliage/MapFoliageOwners.cs | `"MapFoliageOwners.MaxPrepareScopes.1"` | `"MaxPrepareScopes"` |
| KhaozEngine.MapDoc/Foliage/MapFoliageRetentionBudget.cs | `"MapFoliageRetentionBudget.MaxQueryRetainedBytes.1"` | `"MaxQueryRetainedBytes"` |
| KhaozEngine.MapDoc/Foliage/MapFoliageRetentionBudget.cs | `"MapFoliageRetentionBudget.MaxQueryRetainedBytes.2"` | `"MaxQueryRetainedBytes"` |
| KhaozEngine.MapDoc/Foliage/MapFoliageRetentionBudget.cs | `"MapFoliageRetentionBudget.MaxQuerySamples.1"` | `"MaxQuerySamples"` |
| KhaozEngine.MapDoc/Foliage/MapFoliageSupport.cs | `"MapFoliageSupport.MaxPrepareScopes.1"` | `"MaxPrepareScopes"` |
| KhaozEngine.MapDoc/Foliage/MapFoliageSupportBuilder.cs | `"MapFoliageSupportBuilder.MaxFaces.1"` | `"MaxFaces"` |
| KhaozEngine.MapDoc/Foliage/MapFoliageSupportBuilder.cs | `"MapFoliageSupportBuilder.MaxFaces.2"` | `origin.LimitName` |
| KhaozEngine.MapDoc/Foliage/MapFoliageWindowCapture.cs | `"MapFoliageWindowCapture.MaxChunkDescriptors.1"` | `"MaxChunkDescriptors"` |
| KhaozEngine.MapDoc/Interiors/MapRoofEvaluationBuffer.cs | `"MapRoofEvaluationBuffer.Bytes"` | `"MaxPreparedBytes"` |
| KhaozEngine.MapDoc/Interiors/MapRoofEvaluationBuffer.cs | `"MapRoofEvaluationBuffer.Faces"` | `"MaxPreparedFaces"` |
| KhaozEngine.MapDoc/Prefabs/MapPrefabPreflight.cs | `"MapPrefabPreflight.MaxBindings.1"` | `"MaxBindings"` |
| KhaozEngine.MapDoc/Prefabs/MapPrefabPreflight.cs | `"MapPrefabPreflight.MaxChildren.1"` | `"MaxChildren"` |
| KhaozEngine.MapDoc/Prefabs/MapPrefabPreflight.cs | `"MapPrefabPreflight.MaxGeometryElements.1"` | `"MaxGeometryElements"` |
| KhaozEngine.MapDoc/Prefabs/MapPrefabPreflight.cs | `"MapPrefabPreflight.MaxInstances.1"` | `"MaxInstances"` |
| KhaozEngine.MapDoc/Prefabs/MapPrefabPreflight.cs | `"MapPrefabPreflight.MaxOverrides.1"` | `"MaxOverrides"` |
| KhaozEngine.MapDoc/Prefabs/MapPrefabPreflight.cs | `"MapPrefabPreflight.MaxPatches.1"` | `"MaxPatches"` |
| KhaozEngine.MapDoc/Prefabs/MapPrefabPreflight.cs | `"MapPrefabPreflight.MaxPayloadBytes.1"` | `"MaxPayloadBytes"` |
| KhaozEngine.MapDoc/Prefabs/MapPrefabPreflight.cs | `"MapPrefabPreflight.MaxRecords.1"` | `"MaxRecords"` |
| KhaozEngine.MapDoc/Prefabs/MapPrefabPreflight.cs | `"MapPrefabPreflight.MaxReferenceEdges.1"` | `"MaxReferenceEdges"` |
| KhaozEngine.MapDoc/Prefabs/MapPrefabPreflight.cs | `"MapPrefabPreflight.MaxTransfers.1"` | `"MaxTransfers"` |
| KhaozEngine.MapDoc/Scene/MapPreparationLedger.Demand.cs | `"MapPreparationLedger."+site` | `limitName` |
| KhaozEngine.MapDoc/Scene/MapPreparationLedger.Demand.cs | `"MapPreparationLedger.FinalRetained"` | `limitName` |
| KhaozEngine.MapDoc/Scene/MapPreparationLedger.Demand.cs | `"MapPreparationLedger.Window."+site` | `window.LimitName` |
| KhaozEngine.MapDoc/Scene/MapPreparationWindowAttribution.cs | `"MapPreparationWindowAttribution.Acquire"` | `binding.LimitName` |
| KhaozEngine.MapDoc/Scene/MapSceneAcquisition.cs | `"MapSceneAcquisition.MaxSelectedResources.1"` | `"MaxSelectedResources"` |
| KhaozEngine.MapDoc/Scene/MapSceneAcquisition.cs | `"MapSceneAcquisition.MaxSelectedResources.2"` | `"MaxSelectedResources"` |
| KhaozEngine.MapDoc/Scene/MapSceneAcquisition.cs | `"MapSceneAcquisition.MaxSelectedResources.3"` | `"MaxSelectedResources"` |
| KhaozEngine.MapDoc/Scene/MapSceneAdmission.cs | `"MapSceneAdmission.CanReservePreparation.Faces"` | `nameof(MapSceneLimits.MaxPreparedFaces)` |
| KhaozEngine.MapDoc/Scene/MapSceneAdmission.cs | `"MapSceneAdmission.MaxChunkDescriptors.1"` | `"MaxChunkDescriptors"` |
| KhaozEngine.MapDoc/Scene/MapSceneAdmission.cs | `"MapSceneAdmission.MaxPaintTargets.1"` | `"MaxPaintTargets"` |
| KhaozEngine.MapDoc/Scene/MapSceneAdmission.cs | `"MapSceneAdmission.MaxPatchScopes.1"` | `"MaxPatchScopes"` |
| KhaozEngine.MapDoc/Scene/MapSceneAdmission.cs | `"MapSceneAdmission.MaxPlacements.1"` | `"MaxPlacements"` |
| KhaozEngine.MapDoc/Scene/MapSceneAdmission.cs | `"MapSceneAdmission.MaxPreparedFaces.1"` | `"MaxPreparedFaces"` |
| KhaozEngine.MapDoc/Scene/MapSceneAdmission.cs | `"MapSceneAdmission.MaxSelectedResources.1"` | `"MaxSelectedResources"` |
| KhaozEngine.MapDoc/Scene/MapSceneAdmission.cs | `"MapSceneAdmission.MaxSourceUtf8Bytes.1"` | `"MaxSourceUtf8Bytes"` |
| KhaozEngine.MapDoc/Scene/MapSceneAdmission.cs | `"MapSceneAdmission.MaxSurfacePatches.1"` | `"MaxSurfacePatches"` |
| KhaozEngine.MapDoc/Scene/MapSceneMetadataReader.cs | `"MapSceneMetadataReader.MaxMemberships.1"` | `"MaxMemberships"` |
| KhaozEngine.MapDoc/Scene/MapSceneMetadataReader.cs | `"MapSceneMetadataReader.MaxMembershipsPerResource.1"` | `"MaxMembershipsPerResource"` |
| KhaozEngine.MapDoc/Scene/MapSceneMetadataReader.cs | `"MapSceneMetadataReader.MaxMetadataBytes.1"` | `"MaxMetadataBytes"` |
| KhaozEngine.MapDoc/Scene/MapSceneMetadataReader.cs | `"MapSceneMetadataReader.MaxMetadataBytes.2"` | `"MaxMetadataBytes"` |
| KhaozEngine.MapDoc/Scene/MapSceneMetadataReader.cs | `"MapSceneMetadataReader.MaxMetadataBytes.3"` | `"MaxMetadataBytes"` |
| KhaozEngine.MapDoc/Scene/MapSceneMetadataReader.cs | `"MapSceneMetadataReader.MaxMetadataBytes.4"` | `"MaxMetadataBytes"` |
| KhaozEngine.MapDoc/Scene/MapSceneMetadataReader.cs | `"MapSceneMetadataReader.MaxMetadataRows.1"` | `"MaxMetadataRows"` |
| KhaozEngine.MapDoc/Scene/MapSceneMetadataReader.cs | `"MapSceneMetadataReader.MaxMetadataRows.2"` | `"MaxMetadataRows"` |
| KhaozEngine.MapDoc/Scene/MapSceneMetadataReader.cs | `"MapSceneMetadataReader.MaxScannedBytes.1"` | `"MaxScannedBytes"` |
| KhaozEngine.MapDoc/Scene/MapSceneSource.cs | `"MapSceneSource.MaxMetadataBytes.1"` | `"MaxMetadataBytes"` |
| KhaozEngine.MapDoc/Scene/MapSceneSource.cs | `"MapSceneSource.MaxMetadataRows.1"` | `"MaxMetadataRows"` |
| KhaozEngine.MapDoc/Scene/MapSceneSource.cs | `"MapSceneSource.MaxTiles.1"` | `"MaxTiles"` |
| KhaozEngine.MapDoc/Scene/MapSceneSource.cs | `"MapSceneSource.OwnerRows"` | `"MaxMetadataRows"` |
| KhaozEngine.MapDoc/Scene/MapSceneSpatialIndex.cs | `"MapSceneSpatialIndex.MaxSelectedResources.1"` | `"MaxSelectedResources"` |
| KhaozEngine.MapDoc/Spaces/MapBoundFaceContext.cs | `"MapBoundFaceContext.MaxContextFaces.1"` | `"MaxContextFaces"` |
| KhaozEngine.MapDoc/Spaces/MapBoundFaceContext.cs | `"MapBoundFaceContext.MaxContextFaces.1"` | `origin.LimitName` |
| KhaozEngine.MapDoc/Spaces/MapBoundFaceContext.cs | `"MapBoundFaceContext.MaxFacesPerPatch"` | `"MaxFacesPerPatch"` |
| KhaozEngine.MapDoc/Spaces/MapBoundPreparation.cs | `"MapBoundPreparation.MaxContextPatches.1"` | `"MaxContextPatches"` |
| KhaozEngine.MapDoc/Spaces/MapBoundPreparation.cs | `"MapBoundPreparation.MaxContextPatches.1"` | `origin.LimitName` |
| KhaozEngine.MapDoc/Spaces/MapBoundPreparation.cs | `"MapBoundPreparation.MaxSourceFacesPerBound.1"` | `"MaxSourceFacesPerBound"` |
| KhaozEngine.MapDoc/Spaces/MapBoundPreparation.cs | `"MapBoundPreparation.MaxSourceFacesPerBound.2"` | `"MaxSourceFacesPerBound"` |
| KhaozEngine.MapDoc/Spaces/MapBoundPreparation.cs | `"MapBoundPreparation.MaxSourceFacesPerBound.3"` | `"MaxSourceFacesPerBound"` |
| KhaozEngine.MapDoc/Spaces/MapCapacityDemandOrigin.cs | `site` | `LimitName` |
| KhaozEngine.MapDoc/Spaces/MapMembershipPointKernel.cs | `"MapMembershipPointKernel.MaxRecordDepth.1"` | `"MaxRecordDepth"` |
| KhaozEngine.MapDoc/Spaces/MapOccupiedPartitions.cs | `"MapOccupiedPartitions.MaxRecordDepth.1"` | `"MaxRecordDepth"` |
| KhaozEngine.MapDoc/Spaces/MapOccupiedPartitions.cs | `"MapOccupiedPartitions.MaxRecordDepth.2"` | `"MaxRecordDepth"` |
| KhaozEngine.MapDoc/Spaces/MapOccupiedPartitions.cs | `"MapOccupiedPartitions.MaxRecordReads.1"` | `"MaxRecordReads"` |
| KhaozEngine.MapDoc/Spaces/MapOccupiedPartitions.cs | `"MapOccupiedPartitions.MaxRecordReads.2"` | `"MaxRecordReads"` |
| KhaozEngine.MapDoc/Spaces/MapOccupiedPartitions.cs | `"MapOccupiedPartitions.MaxRefinementFaces.1"` | `"MaxRefinementFaces"` |
| KhaozEngine.MapDoc/Spaces/MapOccupiedPartitions.cs | `"MapOccupiedPartitions.MaxRefinementVertices.1"` | `"MaxRefinementVertices"` |
| KhaozEngine.MapDoc/Spaces/MapOccupiedPartitions.cs | `"MapOccupiedPartitions.PrismVertices.1"` | `"MaxRefinementVertices"` |
| KhaozEngine.MapDoc/Spaces/MapPrismGeometry.cs | `"MapPrismGeometry.MaxInspectedFaces.1"` | `"MaxInspectedFaces"` |
| KhaozEngine.MapDoc/Spaces/MapSpaceMembership.cs | `"MapSpaceMembership.MaxInspectedFaces.1"` | `"MaxInspectedFaces"` |
| KhaozEngine.MapDoc/Spaces/MapSpaceMembership.cs | `"MapSpaceMembership.MaxInspectedFaces.2"` | `"MaxInspectedFaces"` |
| KhaozEngine.MapDoc/Spaces/MapSpaceMembership.cs | `"MapSpaceMembership.MaxRecordReads.1"` | `"MaxRecordReads"` |
| KhaozEngine.MapDoc/Spaces/MapSpaceMembershipScratch.cs | `"MapSpaceMembershipScratch.MaxCandidatePatches.1"` | `"MaxCandidatePatches"` |
| KhaozEngine.MapDoc/Spaces/MapSpaceMembershipScratch.cs | `"MapSpaceMembershipScratch.MaxFacesPerPatch"` | `"MaxFacesPerPatch"` |
| KhaozEngine.MapDoc/Spaces/MapSpaceMembershipScratch.cs | `"MapSpaceMembershipScratch.MaxInspectedFaces.1"` | `"MaxInspectedFaces"` |
| KhaozEngine.MapDoc/Spaces/MapSpaceMembershipScratch.cs | `"MapSpaceMembershipScratch.MaxInspectedFaces.2"` | `"MaxInspectedFaces"` |
| KhaozEngine.MapDoc/Spaces/MapSpaceMembershipScratch.cs | `"MapSpaceMembershipScratch.MaxRecordReads.1"` | `"MaxRecordReads"` |
| KhaozEngine.MapDoc/Storage/MapBoundAcquisition.cs | `"MapBoundAcquisition.MaxCandidatePatches.1"` | `"MaxCandidatePatches"` |
| KhaozEngine.MapDoc/Storage/MapComposedSurfaceSource.Admission.cs | `"MapComposedSurfaceSource.Admission.MaxPageMetadataChecks.1"` | `"MaxPageMetadataChecks"` |
| KhaozEngine.MapDoc/Storage/MapComposedSurfaceSource.Admission.cs | `"MapComposedSurfaceSource.Admission.MaxPageRangeOperations.1"` | `"MaxPageRangeOperations"` |
| KhaozEngine.MapDoc/Storage/MapComposedSurfaceSource.Admission.cs | `"MapComposedSurfaceSource.Admission.MaxPageReads.1"` | `"MaxPageReads"` |
| KhaozEngine.MapDoc/Storage/MapComposedSurfaceSource.cs | `"MapComposedSurfaceSource.MaxCandidatePatches.1"` | `"MaxCandidatePatches"` |
| KhaozEngine.MapDoc/Storage/MapDocumentSurfaceSource.cs | `"MapDocumentSurfaceSource.MaxCandidatePatches.1"` | `"MaxCandidatePatches"` |
| KhaozEngine.MapDoc/Storage/MapEffectiveAcquisition.cs | `"MapEffectiveAcquisition.MaxCandidatePatches.1"` | `"MaxCandidatePatches"` |
| KhaozEngine.MapDoc/Storage/MapEffectiveAcquisition.cs | `"MapEffectiveAcquisition.MaxCandidatePatches.2"` | `"MaxCandidatePatches"` |
| KhaozEngine.MapDoc/Storage/MapEffectiveAcquisition.cs | `"MapEffectiveAcquisition.MaxCandidatePatches.3"` | `"MaxCandidatePatches"` |
| KhaozEngine.MapDoc/Storage/MapEffectiveAcquisition.cs | `"MapEffectiveAcquisition.MaxCandidatePatches.4"` | `"MaxCandidatePatches"` |
| KhaozEngine.MapDoc/Storage/MapEffectiveAcquisition.cs | `"MapEffectiveAcquisition.MaxRecordDepth.1"` | `"MaxRecordDepth"` |
| KhaozEngine.MapDoc/Storage/MapEffectiveAcquisition.cs | `"MapEffectiveAcquisition.MaxRecordReads.1"` | `"MaxRecordReads"` |
| KhaozEngine.MapDoc/Storage/MapEffectiveSurfaceSource.cs | `"MapEffectiveSurfaceSource.MaxCandidatePatches.1"` | `"MaxCandidatePatches"` |
| KhaozEngine.MapDoc/Storage/MapEffectiveSurfaceSource.cs | `"MapEffectiveSurfaceSource.MaxRecordReads.1"` | `"MaxRecordReads"` |
| KhaozEngine.MapDoc/Storage/MapPageBudget.cs | `"MapPageBudget.MaxPageMetadataChecks.1"` | `"MaxPageMetadataChecks"` |
| KhaozEngine.MapDoc/Storage/MapPageBudget.cs | `"MapPageBudget.MaxPageRangeOperations.1"` | `"MaxPageRangeOperations"` |
| KhaozEngine.MapDoc/Storage/MapPageBudget.cs | `"MapPageBudget.MaxPageReads.1"` | `"MaxPageReads"` |
| KhaozEngine.MapDoc/Storage/MapPageBudget.cs | `"MapPageBudget.MaxPageReads.2"` | `"MaxPageReads"` |
| KhaozEngine.MapDoc/Storage/MapScopedAcquisition.cs | `"MapScopedAcquisition.MaxCandidatePatches.1"` | `"MaxCandidatePatches"` |
| KhaozEngine.MapDoc/Storage/MapScopedAcquisition.cs | `"MapScopedAcquisition.MaxCandidatePatches.2"` | `"MaxCandidatePatches"` |
| KhaozEngine.MapDoc/Storage/MapScopedAcquisition.cs | `"MapScopedAcquisition.MaxRecordDepth.1"` | `"MaxRecordDepth"` |
| KhaozEngine.MapDoc/Storage/MapScopedAcquisition.cs | `"MapScopedAcquisition.MaxRecordReads.1"` | `"MaxRecordReads"` |
| KhaozEngine.MapDoc/Storage/MapSurfaceQuery.cs | `"MapSurfaceQuery.MaxCandidatePatches.1"` | `"MaxCandidatePatches"` |
| KhaozEngine.MapDoc/Storage/MapSurfaceSessionState.cs | `"MapSurfaceSessionState.MaxCandidatePatches.1"` | `"MaxCandidatePatches"` |
| KhaozEngine.MapDoc/Support/MapSupportQuery.cs | `"MapSupportQuery.SupportCandidateBuffer.1"` | `"SupportCandidateBuffer"` |
| KhaozEngine.MapDoc/Support/MapSupportSelection.cs | `"MapSupportSelection.MaxSupportIntersections.1"` | `"MaxSupportIntersections"` |
| KhaozEngine.MapDoc/Surfaces/MapEffectiveGeometry.cs | `"MapEffectiveGeometry.MaxPairChecks.1"` | `"MaxPairChecks"` |
| KhaozEngine.MapDoc/Surfaces/MapEffectiveGeometry.cs | `"MapEffectiveGeometry.MaxRefinementFaces.1"` | `"MaxRefinementFaces"` |
| KhaozEngine.MapDoc/Surfaces/MapEffectiveGeometry.cs | `"MapEffectiveGeometry.MaxRefinementVertices.1"` | `"MaxRefinementVertices"` |
| KhaozEngine.MapDoc/Surfaces/MapEffectiveGeometry.cs | `"MapEffectiveGeometry.MaxSourceFacesPerBound.1"` | `"MaxSourceFacesPerBound"` |
| KhaozEngine.MapDoc/Surfaces/MapEffectiveGeometry.cs | `"MapEffectiveGeometry.MaxSourceFacesPerBound.2"` | `"MaxSourceFacesPerBound"` |
| KhaozEngine.MapDoc/Surfaces/MapPartialPaintCompiler.cs | `"MapPartialPaintCompiler."+caller` | `name` |
| KhaozEngine.MapDoc/Surfaces/MapPartialPaintCompiler.cs | `"MapPartialPaintCompiler.MaxContextPatches.1"` | `"MaxContextPatches"` |
| KhaozEngine.MapDoc/Surfaces/MapPartialPaintCompiler.cs | `"MapPartialPaintCompiler.MaxContextPatches.1"` | `origin.LimitName` |
| KhaozEngine.MapDoc/Surfaces/MapSurfaceCompiler.cs | `"MapSurfaceCompiler.MaxFacesPerPatch.1"` | `"MaxFacesPerPatch"` |
| KhaozEngine.MapDoc/Water/MapWaterBoundReads.cs | `"MapWaterBoundReads.CellFaces"` | `"MaxSourceFacesPerBound"` |
| KhaozEngine.MapDoc/Water/MapWaterBoundReads.cs | `"MapWaterBoundReads.ChunkCells"` | `"MaxSourceFacesPerBound"` |
| KhaozEngine.MapDoc/Water/MapWaterBoundReads.cs | `"MapWaterBoundReads.ChunkFaces"` | `"MaxSourceFacesPerBound"` |
| KhaozEngine.MapDoc/Water/MapWaterBoundReads.cs | `"MapWaterBoundReads.RowCells"` | `"MaxSourceFacesPerBound"` |
| KhaozEngine.MapDoc/Water/MapWaterBoundReads.cs | `"MapWaterBoundReads.RowFaces"` | `"MaxSourceFacesPerBound"` |
| KhaozEngine.MapDoc/Water/MapWaterBoundReads.cs | `"MapWaterBoundReads.RunCells"` | `"MaxSourceFacesPerBound"` |
| KhaozEngine.MapDoc/Water/MapWaterBoundReads.cs | `"MapWaterBoundReads.RunFaces"` | `"MaxSourceFacesPerBound"` |
| KhaozEngine.MapDoc/Water/MapWaterResourceCell.cs | `"MapWaterResourceCell.MaxEmittedVertices"` | `"MaxEmittedVertices"` |
| KhaozEngine.MapDoc/Water/MapWaterScopedCompiler.cs | `"MapWaterScopedCompiler.MaxCells.1"` | `"MaxCells"` |
| KhaozEngine.MapDoc/Water/MapWaterScopedCompiler.cs | `"MapWaterScopedCompiler.MaxCells.2"` | `"MaxCells"` |
| KhaozEngine.MapDoc/Water/MapWaterScopedCompiler.cs | `"MapWaterScopedCompiler.MaxEmittedVertices.1"` | `"MaxEmittedVertices"` |
| KhaozEngine.MapDoc/Water/MapWaterWorkBudget.cs | `"MapWaterWorkBudget.MaxBoundFaces.1"` | `"MaxBoundFaces"` |
| KhaozEngine.MapDoc/Water/MapWaterWorkBudget.cs | `"MapWaterWorkBudget.MaxPairChecks.1"` | `"MaxPairChecks"` |
| KhaozEngine.MapDoc/Water/MapWaterWorkBudget.cs | `"MapWaterWorkBudget.MaxPieceVertices.1"` | `"MaxPieceVertices"` |
| KhaozEngine.MapDoc/Water/MapWaterWorkBudget.cs | `"MapWaterWorkBudget.MaxPieces.1"` | `"MaxPieces"` |
| KhaozEngine.MapDoc/Water/MapWaterWorkBudget.cs | `"MapWaterWorkBudget.name.1"` | `name` |
| KhaozEngine.MapDoc/Water/MapWaterWorkBudget.cs | `"MapWaterWorkMeter.Traversal"` | `nameof(MapWaterCompileLimits.MaxBoundCells)` |
| KhaozEngine.Render2D/ImageRgba.cs | `"ImageRgba.MaxImageBufferPixels.1"` | `"MaxImageBufferPixels"` |
| KhaozEngine.Render2D/ImageRgba.cs | `"ImageRgba.MaxImagePixels.1"` | `"MaxImagePixels"` |
| KhaozEngine.Render2D/ImageRgba.cs | `"ImageRgba.MaxPeakBytes.1"` | `"MaxPeakBytes"` |
| KhaozEngine.Render3D/Models/GltfDecodeAdmission.cs | `"GltfDecodeAdmission.5999.1"` | `budget.Limits.MaxVertices>=budget.Limits.MaxIndices?"MaxVertices":"MaxIndices"` |
| KhaozEngine.Render3D/Models/GltfDecodeAdmission.cs | `"GltfDecodeAdmission.7136.1"` | `components==1?"MaxIndices":"MaxVertices"` |
| KhaozEngine.Render3D/Models/GltfDecodeAdmission.cs | `"GltfDecodeAdmission.MaxImageBufferPixels.1"` | `"MaxImageBufferPixels"` |
| KhaozEngine.Render3D/Models/GltfDecodeAdmission.cs | `"GltfDecodeAdmission.MaxImagePixels.1"` | `"MaxImagePixels"` |
| KhaozEngine.Render3D/Models/GltfDecodeAdmission.cs | `"GltfDecodeAdmission.MaxImages.1"` | `"MaxImages"` |
| KhaozEngine.Render3D/Models/GltfDecodeAdmission.cs | `"GltfDecodeAdmission.MaxIndices.1"` | `"MaxIndices"` |
| KhaozEngine.Render3D/Models/GltfDecodeAdmission.cs | `"GltfDecodeAdmission.MaxPeakBytes.1"` | `"MaxPeakBytes"` |
| KhaozEngine.Render3D/Models/GltfDecodeAdmission.cs | `"GltfDecodeAdmission.MaxPeakBytes.2"` | `"MaxPeakBytes"` |
| KhaozEngine.Render3D/Models/GltfDecodeAdmission.cs | `"GltfDecodeAdmission.MaxPeakBytes.3"` | `"MaxPeakBytes"` |
| KhaozEngine.Render3D/Models/GltfDecodeAdmission.cs | `"GltfDecodeAdmission.MaxVertices.1"` | `"MaxVertices"` |
| KhaozEngine.Render3D/Models/GltfDecodeAdmission.cs | `"GltfDecodeAdmission.MaxVertices.2"` | `"MaxVertices"` |
| KhaozEngine.Render3D/Models/GltfDecodeBudget.cs | `"GltfDecodeBudget.MaxPeakBytes.1"` | `"MaxPeakBytes"` |
