using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;

namespace KhaozEngine.MapEdit;

public sealed partial class MutationService
{
    static MapFeature TranslateDuplicateFeature(MapFeature source, int index, string? documentId)
    {
        if (FeatureGeometry.Translated(source, DuplicateOffset, DuplicateOffset) is { } clone) return clone;
        string featureType = source.Type;
        throw new MapMutationException(MapDocumentRefusal.ToolFeatureTranslationUnsupported,
            $"feature at index {index} (type '{featureType}') cannot be duplicated: its type has no known offset.")
        { DocumentId = documentId, Index = index, FeatureType = featureType, Operation = "element_duplicate" };
    }

    // The session registry currently loads built-ins only. This value-taking seam reaches the same guard
    // with an unsupported feature without widening the registry or exposing mutable session state.
    internal static MapFeature TranslateUnsupportedFeatureForTests(MapFeature source, int index) =>
        TranslateDuplicateFeature(source, index, null);
}
