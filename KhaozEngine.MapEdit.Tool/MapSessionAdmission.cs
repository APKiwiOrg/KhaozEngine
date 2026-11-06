using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;

namespace KhaozEngine.MapEdit;

/// <summary>Value-based admission for session bindings, before cloning or changing session state.</summary>
internal static class MapSessionAdmission
{
    internal static void RequireNativeAssets(MapDocument document, MapAssetClosure? assets, string? storagePath)
    {
        bool native = NativeDocumentService.IsNative(document);
        if (!native || assets is null || storagePath is null)
            throw new MapDocumentException(!native ? MapDocumentRefusal.ToolNativeDocumentRequired : assets is null
                    ? MapDocumentRefusal.AssetClosureRequired : MapDocumentRefusal.SchemaValueRequired,
                "Native asset editing requires an open native document with a bound asset closure.")
            {
                DocumentId = document.Id, Operation = "WithNativeAssets", Path = storagePath,
                FieldPath = !native ? "ResolverIdentity" : assets is null ? "NativeAssets" : "DocumentPath",
            };
    }

    internal static void RequireNativeMutation(MapAssetClosure? assets, bool nested, string? documentId)
    {
        if (assets is null) throw new MapDocumentException(MapDocumentRefusal.AssetClosureRequired,
            "Native editing requires an explicit asset closure binding.") { DocumentId = documentId, Operation = "MutateNative" };
        if (nested) throw new MapDocumentException(MapDocumentRefusal.ToolNestedMutationUnsupported,
            "Nested native mutations are not supported.") { DocumentId = documentId, Operation = "MutateNative" };
    }

    /// <summary>Injects missing binding values into the production guard without replacing session state.</summary>
    internal static void RequireNativeAssetsForTests(MapDocument document, MapAssetClosure? assets, string? storagePath) =>
        RequireNativeAssets(document, assets, storagePath);

    /// <summary>Injects a missing closure into the production mutation guard before any clone or callback.</summary>
    internal static void RequireNativeMutationForTests(MapAssetClosure? assets, bool nested, string? documentId) =>
        RequireNativeMutation(assets, nested, documentId);
}
