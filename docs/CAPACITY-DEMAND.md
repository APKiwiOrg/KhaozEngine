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

Each event names the site and effective limit and carries current demand, proposed increment,
requested total, effective allowance and the original refusal predicate. Numeric admission identities
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
Query-derived context allowances are reported at their bound preparation/context sites. A context
allowance can therefore be the remaining query or foliage allowance passed to that site.

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
| `MaxContextFaces` | `MapBoundFaceContext` |
| `MaxContextPatches` | `MapBoundPreparation`, `MapPartialPaintCompiler` |
| `MaxDecodedBytes` | `MapRenderAssetAllowance`, `MapRenderAssets` |
| `MaxDifferentialCandidates` | `MapFoliageDifferential` |
| `MaxDisposalsPerUpdate` | `MapRenderResidency` |
| `MaxEmittedVertices` | `MapWaterResourceCell`, `MapWaterScopedCompiler`, `NativeMapWater` |
| `MaxFaces` | `MapFoliageCandidateSupport (via bound context)`, `MapFoliageSupportBuilder` |
| `MaxFacesPerPatch` | `MapSurfaceCompiler` |
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
| `MaxPreparedBytes` | `MapPreparationLedger.Demand`, `MapSceneAdmission` |
| `MaxPreparedFaces` | `MapSceneAdmission` |
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
| `MaxResidentCpuBytes` | `GltfDecodeBudget`, `MapRenderAdmission`, `MapRenderUploadPreflight` |
| `MaxResidentGpuBytes` | `MapRenderAdmission`, `MapRenderUploadPreflight` |
| `MaxRuleCandidates` | `MapFoliageClearance` |
| `MaxSamples` | `MapFoliageDistribution` |
| `MaxScannedBatches` | `MapFoliageDependents` |
| `MaxScannedBytes` | `MapSceneMetadataReader` |
| `MaxSelectedResources` | `MapCaptureDependencies`, `MapSceneAcquisition`, `MapSceneAdmission`, `MapSceneSpatialIndex`, `NativeMapFoliage` |
| `MaxSourceFacesPerBound` | `MapBoundPreparation`, `MapEffectiveGeometry` |
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
