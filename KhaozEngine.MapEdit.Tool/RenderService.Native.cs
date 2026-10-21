using System;
using System.Collections.Generic;
using System.Text.Json;
using KhaozEngine.Imaging;
using KhaozEngine.MapDoc.Render3D;
using ModelContextProtocol.Protocol;

namespace KhaozEngine.MapEdit;

public sealed partial class RenderService
{
    public RenderService(MapEditSession session, IMapNativeRenderHost nativeHost) : this(session)
    { session.NativeRenderHost = nativeHost ?? throw new ArgumentNullException(nameof(nativeHost)); }
    public MapSnapshotResult RenderNative(string nativeRequestJson, MapCaptureRoute route = MapCaptureRoute.View)
    {
        var input = NativeRenderRequest.Read(nativeRequestJson);
        if (route == MapCaptureRoute.TopDown && !input.Framing.Orthographic) throw new ArgumentException("TopDownRequiresOrthographic");
        var context = session.ResolveNativeRender(input.StyleId);
        return NativeMapSnapshot.Capture(input.Resolve(context, route));
    }
    internal IEnumerable<ContentBlock>? NativeBlocks(string? json, MapCaptureRoute route)
    {
        bool native = session.UsesNativeRender;
        if (!native && json is null) return null;
        if (!native) throw new ArgumentException("NativeRequestOnAnalytic");
        if (json is null) throw new ArgumentException("NativeRequestRequired");
        var result = RenderNative(json, route);
        var blocks = new List<ContentBlock> { new TextContentBlock { Text = JsonSerializer.Serialize(NativeRenderWire.ToWire(result), NativeJson.Options) } };
        if (result.Image is { } image) blocks.Add(new ImageContentBlock { MimeType = "image/png", Data = PngWriter.Encode(image.Rgba, image.Width, image.Height) });
        return blocks;
    }
}
