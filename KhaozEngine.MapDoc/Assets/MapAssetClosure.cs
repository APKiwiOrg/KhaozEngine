using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace KhaozEngine.MapDoc.Assets;

/// <summary>A complete, digest-verified, acyclic resource graph and its immutable asset descriptors.</summary>
public sealed class MapAssetClosure
{
    readonly Dictionary<string, MapResolvedAsset> _assets;
    readonly Dictionary<string, MapResolvedResource> _resources;
    public IReadOnlyList<MapResolvedAsset> Assets { get; }
    public string Hash { get; }
    /// <summary>The exact input roots, snapshotted before source reads and published in ordinal ID order.</summary>
    public IReadOnlyList<MapAssetRef> Roots { get; }

    MapAssetClosure(Dictionary<string, MapAssetDoc> assets, Dictionary<string, MapResourceDoc> resources,
        Dictionary<string, byte[]> snapshots, MapAssetRef[] roots)
    {
        _assets = assets.ToDictionary(pair => pair.Key, pair => new MapResolvedAsset(pair.Value), StringComparer.Ordinal);
        _resources = resources.ToDictionary(pair => pair.Key,
            pair => new MapResolvedResource(pair.Value.Reference, pair.Value.Kind, pair.Value.Dependencies, snapshots[pair.Key]),
            StringComparer.Ordinal);
        Assets = Array.AsReadOnly(_assets.Values.OrderBy(asset => asset.Id, StringComparer.Ordinal).ToArray());
        Roots = Array.AsReadOnly(roots);
        Hash = ComputeHash(resources);
    }

    public MapResolvedAsset GetAsset(string assetId) => _assets.TryGetValue(assetId, out var asset)
        ? asset : throw new MapDocumentException($"Native asset '{assetId}' is not in the verified closure.");

    public MapResolvedResource GetResource(string resourceId) => _resources.TryGetValue(resourceId, out var resource)
        ? resource : throw new MapDocumentException($"Native resource '{resourceId}' is not in the verified closure.");

    /// <summary>Loads all roots and declared resources. Nothing is published until every check succeeds.</summary>
    public static MapAssetClosure Load(IReadOnlyList<MapAssetRef> roots, IMapAssetSource source)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(source);
        var resources = new Dictionary<string, MapResourceDoc>(StringComparer.Ordinal);
        var assets = new Dictionary<string, MapAssetDoc>(StringComparer.Ordinal);
        var snapshots = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var edges = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var pending = new SortedSet<string>(StringComparer.Ordinal);

        void Register(MapResourceDoc resource)
        {
            MapAssetManifestReader.ValidateResource(resource);
            string id = resource.Reference.Id;
            if (!resources.TryAdd(id, resource)) throw new MapDocumentException($"Duplicate native resource ID '{id}'.");
            edges.Add(id, new HashSet<string>(resource.Dependencies, StringComparer.Ordinal));
            pending.Add(id);
        }

        // MapAssetRef is immutable. Copy the caller's collection before reading any source buffers.
        MapAssetRef[] rootSnapshot = roots.ToArray();
        foreach (MapAssetRef root in rootSnapshot)
            Register(new MapResourceDoc { Reference = root, Kind = MapResourceKind.Manifest });

        while (pending.Count != 0)
        {
            string id = pending.Min!;
            pending.Remove(id);
            MapResourceDoc resource = resources[id];
            byte[] bytes = Snapshot(source, resource.Reference);
            string digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!StringComparer.Ordinal.Equals(digest, resource.Reference.Sha256))
                throw new MapDocumentException($"SHA-256 mismatch for native resource '{id}'.");
            snapshots.Add(id, bytes);
            if (resource.Kind != MapResourceKind.Manifest) continue;
            MapAssetManifestDoc manifest = MapAssetManifestReader.Read(bytes, id);
            foreach (MapAssetDoc asset in manifest.Assets)
            {
                if (!assets.TryAdd(asset.Id, asset)) throw new MapDocumentException($"Duplicate native asset ID '{asset.Id}'.");
                edges[id].UnionWith(MapAssetManifestReader.ResourceReferences(asset).Select(reference => reference.Id));
            }
            foreach (MapResourceDoc child in manifest.Resources)
            {
                Register(child);
                // Manifest containment is a graph edge too, so dependencies cannot point back to an ancestor.
                edges[id].Add(child.Reference.Id);
            }
        }

        foreach (MapAssetDoc asset in assets.Values) MapAssetManifestReader.ValidateAssetResources(asset, resources);
        ValidateGraph(edges);
        Array.Sort(rootSnapshot, static (a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
        return new MapAssetClosure(assets, resources, snapshots, rootSnapshot);
    }

    static byte[] Snapshot(IMapAssetSource source, MapAssetRef reference)
    {
        try
        {
            // Verify this copy and publish copies of this same buffer. Never read the source a second time.
            return source.Read(reference).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or KeyNotFoundException)
        {
            throw new MapDocumentException($"Cannot read native resource '{reference.Id}'.", ex);
        }
    }

    static void ValidateGraph(Dictionary<string, HashSet<string>> edges)
    {
        var incoming = edges.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        foreach (var pair in edges)
            foreach (string dependency in pair.Value)
            {
                if (!incoming.ContainsKey(dependency))
                    throw new MapDocumentException($"Resource '{pair.Key}' requires missing resource '{dependency}'.");
                incoming[dependency]++;
            }
        var ready = new SortedSet<string>(incoming.Where(pair => pair.Value == 0).Select(pair => pair.Key), StringComparer.Ordinal);
        int visited = 0;
        while (ready.Count != 0)
        {
            string id = ready.Min!;
            ready.Remove(id);
            visited++;
            foreach (string dependency in edges[id])
                if (--incoming[dependency] == 0) ready.Add(dependency);
        }
        if (visited != edges.Count) throw new MapDocumentException("Native resource dependency cycle.");
    }

    static string ComputeHash(Dictionary<string, MapResourceDoc> resources)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("closureVersion", 1);
            writer.WriteStartArray("resources");
            foreach (MapResourceDoc resource in resources.Values.OrderBy(r => r.Reference.Id, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("id", resource.Reference.Id);
                writer.WriteString("path", resource.Reference.Path);
                writer.WriteString("sha256", resource.Reference.Sha256);
                writer.WriteNumber("payloadVersion", resource.Reference.PayloadVersion);
                writer.WriteString("kind", resource.Kind.ToString());
                writer.WriteStartArray("dependencies");
                foreach (string dependency in resource.Dependencies) writer.WriteStringValue(dependency);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }
}
