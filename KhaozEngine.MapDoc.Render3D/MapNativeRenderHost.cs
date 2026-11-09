using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Scene;

namespace KhaozEngine.MapDoc.Render3D;

public interface IMapNativeRenderHost
{
    MapNativeRenderContext Resolve(MapDocument document, string documentPath, string styleId);
}
public sealed record MapNativeRenderContext(MapAssetClosure Closure, MapSceneSource Source,
    MapAssetExtentProofSet Extents, MapRenderPolicy RenderPolicy, MapCaptureLighting Lighting, string StyleId)
{
    public int PreviewWindowLimit { get; init; } = 256;
}
