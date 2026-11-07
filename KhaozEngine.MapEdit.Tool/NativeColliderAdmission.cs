using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;

namespace KhaozEngine.MapEdit;

/// <summary>Checks computed and published collider values before the live session changes.</summary>
internal static class NativeColliderAdmission
{
    internal static void RequirePublishedPlan(string assetId, MapAssetRef expectedCollider, MapAssetRef expectedRoot,
        MapAssetRef collider, MapAssetRef root)
    {
        if (collider != expectedCollider || root != expectedRoot)
        {
            bool colliderMismatch = collider != expectedCollider;
            MapAssetRef expected = colliderMismatch ? expectedCollider : expectedRoot;
            MapAssetRef actual = colliderMismatch ? collider : root;
            throw new MapDocumentException(MapDocumentRefusal.ToolColliderPublicationMismatch,
                $"Collider edit for '{assetId}' wrote resources that differ from its plan.")
            { AssetId = assetId, ResourceId = expected.Id, ExpectedHash = expected.Sha256, ActualHash = actual.Sha256 };
        }
    }

    internal static void RequireFiniteResizedBox(string assetId, int index, float position, float halfHeight)
    {
        if (!float.IsFinite(position) || halfHeight is not (> 0f and < float.PositiveInfinity))
        {
            bool positionFailed = !float.IsFinite(position);
            float actual = positionFailed ? position : halfHeight;
            throw new MapDocumentException(!float.IsFinite(actual) ? MapDocumentRefusal.ValueNonfinite : MapDocumentRefusal.ValueOutsideRange,
                $"Collider heights for '{assetId}' leave a box without a finite positive height.")
            {
                AssetId = assetId,
                Index = index,
                FieldPath = positionFailed ? "Position.Y" : "HalfExtents.Y",
                ActualNumber = actual,
                MinimumNumber = positionFailed ? null : 0
            };
        }
    }

    // Value-taking fault seams call the production predicates without changing a session or its state machine.
    internal static void RequirePublishedColliderPlanForTests(string assetId, MapAssetRef expectedCollider, MapAssetRef expectedRoot,
        MapAssetRef collider, MapAssetRef root) => RequirePublishedPlan(assetId, expectedCollider, expectedRoot, collider, root);
    internal static void RequireFiniteResizedBoxForTests(string assetId, int index, float position, float halfHeight) =>
        RequireFiniteResizedBox(assetId, index, position, halfHeight);
}
