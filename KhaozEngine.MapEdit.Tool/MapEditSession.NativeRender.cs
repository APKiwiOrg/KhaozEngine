using System;
using KhaozEngine.MapDoc.Render3D;

namespace KhaozEngine.MapEdit;

public sealed partial class MapEditSession
{
    public IMapNativeRenderHost? NativeRenderHost { get; set; }
    public MapNativeRenderContext ResolveNativeRender(string styleId)
    {
        lock (_lock)
        {
            RequireDocumentLocked();
            if (!NativeDocumentService.IsNative(_doc!)) throw new ArgumentException("NativeRequestOnAnalytic");
            return (NativeRenderHost ?? throw new InvalidOperationException("NativeRenderHostRequired"))
                .Resolve(_doc!, _storagePath ?? _path!, styleId);
        }
    }
    internal bool UsesNativeRender => WithDocument((document, _) => NativeDocumentService.IsNative(document));
}
