using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapEditor;

namespace KhaozEngine.MapEdit;

public interface IMapPrefabPublicationFiles
{
    void PublishNew(string path, ReadOnlyMemory<byte> bytes);
    void Checkpoint(string name);
}

/// <summary>Publishes immutable prefab and manifest bytes before one validated map and closure switch.</summary>
public sealed class PrefabAssetPublisher(MapEditSession session, IMapPrefabPublicationFiles? files = null)
{
    readonly IMapPrefabPublicationFiles _files = files ?? new ImmutableFiles();
    internal MapAssetClosure? PublishedAssets { get; private set; }
    public MapPrefabPublishResult Publish(MapPrefabAsset asset, string resourceId, string relativeDirectory, bool updateOpenMap)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeDirectory);
        return session.WithNativeAssets<MapPrefabPublishResult>(c =>
        {
            byte[] prefabBytes = MapPrefabFile.Write(asset);
            string sha = Convert.ToHexStringLower(SHA256.HashData(prefabBytes));
            string address = MapAssetVersionIdentity.Address("prefab", asset.Id, prefabBytes);
            var reference = new MapAssetRef(address, relativeDirectory.TrimEnd('/') + "/" + address, sha, 1);
            string prefabPath = session.RequireNativeResourcePath(reference.Path);
            MapAssetRef[] roots = c.Document.NativeAssets.ToArray();
            if (roots.Length == 0) throw new MapDocumentException("Prefab publication requires an asset manifest root.");
            int index = Array.FindIndex(roots, r => MapAssetManifestReader.Read(c.Assets.GetResource(r.Id).Bytes.ToArray(), r.Id)
                .Resources.Any(resource => resource.Reference.Id == resourceId));
            if (index < 0) index = 0;
            var manifest = MapAssetManifestReader.Read(c.Assets.GetResource(roots[index].Id).Bytes.ToArray(), roots[index].Id);
            if (!c.Assets.TryGetResource(reference.Id, out _))
                manifest.Resources.Add(new()
                {
                    Reference = reference,
                    Kind = MapResourceKind.Prefab,
                    Dependencies = c.Assets.CapturedResources.Where(r => r.Kind is not (MapResourceKind.Prefab or MapResourceKind.Manifest)).Select(r => r.Reference.Id).Order(StringComparer.Ordinal).ToList()
                });
            else reference = c.Assets.GetResource(reference.Id).Reference;
            byte[] manifestBytes = MapAssetFileWriter.Serialize(manifest, roots[index].Id);
            string manifestSha = Convert.ToHexStringLower(SHA256.HashData(manifestBytes));
            var root = roots[index] with { Path = relativeDirectory.TrimEnd('/') + "/manifest/" + manifestSha, Sha256 = manifestSha };
            string manifestPath = session.RequireNativeResourcePath(root.Path);
            roots[index] = root;
            // Every resource pin and existing declaration is retained. Old maps and unpacked rows keep their bytes.
            _files.Checkpoint("before-bytes");
            PublishVerified(prefabPath, prefabBytes);
            _files.Checkpoint("after-prefab");
            PublishVerified(manifestPath, manifestBytes);
            _files.Checkpoint("after-manifest");
            MapAssetClosure after = MapAssetClosure.Load(roots, c.Source);
            MapPrefabMutationResult? mutation = null;
            if (updateOpenMap)
            {
                _files.Checkpoint("before-root-switch");
                IEditorCommand command;
                if (c.Assets.TryGetResource(resourceId, out var previous) && previous!.Kind == MapResourceKind.Prefab)
                    command = new PrefabAssetEditCommand(new MapUpdatePrefabAsset(resourceId, previous.Reference.Sha256, reference.Id, reference.Sha256), c.Assets, after);
                else
                {
                    var candidate = NativeDocumentSnapshot.Clone(c.Document, c.Registry);
                    candidate.NativeAssets = roots.ToList();
                    command = new NativeSessionSnapshotCommand("Publish prefab asset", c.Document, candidate, c.Assets, after,
                        new(null, null, [], [], [], [], MapNativeInvalidation.Residency));
                }
                long events = session.NativeHistoryState.Events;
                var effects = session.ApplyNativeAssets(command, after, () => _files.Checkpoint("outer-validation"));
                if (session.NativeHistoryState.Events != events) mutation = new(new("prefab_save", "Published prefab asset", true), effects);
            }
            PublishedAssets = after;
            return new(reference, mutation);
        });
    }
    void PublishVerified(string path, byte[] bytes)
    {
        if (!File.Exists(path)) _files.PublishNew(path, bytes);
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            throw new MapDocumentException("An immutable prefab address holds different bytes or was not published.");
    }
    sealed class ImmutableFiles : IMapPrefabPublicationFiles
    {
        public void Checkpoint(string name) { }
        public void PublishNew(string path, ReadOnlyMemory<byte> bytes)
        {
            string staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes.Span); stream.Flush(true); }
                try { File.Move(staging, path, false); }
                catch (IOException) when (File.Exists(path)) { }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new MapDocumentException("Cannot publish immutable prefab bytes: " + ex.Message, ex); }
            finally { if (File.Exists(staging)) File.Delete(staging); }
        }
    }
}
