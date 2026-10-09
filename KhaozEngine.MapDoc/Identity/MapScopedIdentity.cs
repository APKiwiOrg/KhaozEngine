using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;

namespace KhaozEngine.MapDoc.Identity;

/// <summary>Factory-only semantic identity for exactly the acquired scope and its captured facts.</summary>
public sealed class MapScopedIdentity
{
    internal MapScopedIdentity(string rootSha256, string scopeDigest, WorldFrame frame,
        IEnumerable<KeyValuePair<MapPatchKey, string>> patches, IEnumerable<MapRecordRef> records,
        IEnumerable<MapCoveredRange> knownEmpty, IEnumerable<string> assetSha256,
        int queryPolicyVersion, int buildPolicyVersion, bool complete)
    {
        RootSha256 = rootSha256;
        ScopeDigest = scopeDigest;
        Frame = frame;
        Patches = Array.AsReadOnly(patches.ToArray());
        Records = Array.AsReadOnly(records.ToArray());
        KnownEmpty = Array.AsReadOnly(knownEmpty.ToArray());
        AssetSha256 = Array.AsReadOnly(assetSha256.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToArray());
        QueryPolicyVersion = queryPolicyVersion;
        BuildPolicyVersion = buildPolicyVersion;
        Complete = complete;
        Digest = MapCanonical.HashHex(Write);
    }
    public string RootSha256 { get; }
    public string ScopeDigest { get; }
    public WorldFrame Frame { get; }
    public IReadOnlyList<KeyValuePair<MapPatchKey, string>> Patches { get; }
    public IReadOnlyList<MapRecordRef> Records { get; }
    public IReadOnlyList<MapCoveredRange> KnownEmpty { get; }
    public IReadOnlyList<string> AssetSha256 { get; }
    public int QueryPolicyVersion { get; }
    public int BuildPolicyVersion { get; }
    public bool Complete { get; }
    public string Digest { get; }

    public void RequireComplete()
    {
        if (!Complete) throw new MapDocumentException("incomplete scoped identity");
    }
    void Write(Utf8JsonWriter w)
    {
        w.WriteStartArray(); w.WriteStringValue("kemap/scoped/1");
        w.WriteStringValue(RootSha256); w.WriteStringValue(ScopeDigest);
        w.WriteNumberValue(Frame.X); w.WriteNumberValue(Frame.Z);
        w.WriteStartArray();
        foreach (var patch in Patches) { w.WriteStartArray(); Key(w, patch.Key); w.WriteStringValue(patch.Value); w.WriteEndArray(); }
        w.WriteEndArray(); w.WriteStartArray();
        foreach (MapRecordRef record in Records) { w.WriteStartArray(); Key(w, record.Anchor); w.WriteStringValue(record.Id); w.WriteEndArray(); }
        w.WriteEndArray(); w.WriteStartArray();
        foreach (MapCoveredRange range in KnownEmpty)
        {
            w.WriteStartArray(); w.WriteStringValue(range.SurfaceId);
            w.WriteNumberValue(range.Slots.MinX); w.WriteNumberValue(range.Slots.MinZ);
            w.WriteNumberValue(range.Slots.MaxXExclusive); w.WriteNumberValue(range.Slots.MaxZExclusive); w.WriteEndArray();
        }
        w.WriteEndArray(); w.WriteStartArray();
        foreach (string asset in AssetSha256) w.WriteStringValue(asset);
        w.WriteEndArray(); w.WriteNumberValue(QueryPolicyVersion); w.WriteNumberValue(BuildPolicyVersion);
        w.WriteBooleanValue(Complete); w.WriteEndArray();
    }
    static void Key(Utf8JsonWriter w, MapPatchKey key)
    {
        w.WriteStartArray(); w.WriteStringValue(key.SurfaceId); w.WriteNumberValue(key.SlotX); w.WriteNumberValue(key.SlotZ); w.WriteEndArray();
    }
}
