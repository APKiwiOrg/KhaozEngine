using KhaozEngine.MapDoc.Render3D;

namespace KhaozEngine.Tests.MapDocOracle;

internal static partial class ShippedSceneComparison
{
    internal static void CheckResource(PrivateOracleReport report, string category, bool ok, object detail, MapRenderKey key, string field)
    {
        if (ok) return;
        Check(report, category, false, detail, SceneFailureClassification.Resource(key, field));
    }

    internal static void CheckResource(PrivateOracleReport report, string category, bool ok, object detail, MapRenderKind kind, string keyClass, string field)
    {
        if (ok) return;
        Check(report, category, false, detail, new SceneFailureClassification(kind.ToString(), keyClass, field));
    }
}
