using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Identity;

/// <summary>The shared semantic projection, independent of physical storage and acquisition bookkeeping.</summary>
internal static class MapAuthoredIdentityProjection
{
    internal static string Compute(string rootDigest, IEnumerable<KeyValuePair<MapPatchKey, string>> patches,
        MapAssetClosure assets, MapResolveOptions options) => MapCanonical.HashHex(w =>
    {
        w.WriteStartObject();
        w.WriteString("domain", "kemap/native-authored/2");
        w.WriteString("rootDigest", rootDigest);
        w.WriteStartArray("patches");
        foreach (var patch in patches.OrderBy(p => p.Key))
        {
            w.WriteStartArray();
            w.WriteStringValue(patch.Key.SurfaceId);
            w.WriteNumberValue(patch.Key.SlotX);
            w.WriteNumberValue(patch.Key.SlotZ);
            w.WriteStringValue(patch.Value);
            w.WriteEndArray();
            w.Flush();
        }
        w.WriteEndArray();
        w.WriteString("closure", assets.Hash);
        w.WriteString("builderId", options.BuilderId);
        w.WriteNumber("builderVersion", options.BuilderVersion);
        w.WriteString("optionsHash", options.OptionsHash);
        w.WriteNumber("resolverVersion", options.ResolverVersion);
        w.WriteEndObject();
    });
}
