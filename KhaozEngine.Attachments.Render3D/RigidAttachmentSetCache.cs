using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>Owns verified source snapshots and shared uploads for one scene.</summary>
/// <remarks>Use on the owning render thread. Dispose before the scene, after stopping use of its views.</remarks>
public sealed class RigidAttachmentSetCache : IDisposable
{
    readonly IAttachmentAssetSource _source;
    readonly Dictionary<AttachmentMeshRef, CachedDefinition> _definitions = [];
    readonly Dictionary<AttachmentMeshRef, CachedMesh> _meshes = [];
    bool _disposed;

    public RigidAttachmentSetCache(Scene3D scene, IAttachmentAssetSource source)
        : this(new Scene3DAttachmentScene(scene), source) { }

    public RigidAttachmentSetCache(IRigidAttachmentScene scene, IAttachmentAssetSource source)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(source);
        Scene = scene;
        _source = source;
    }

    internal IRigidAttachmentScene Scene { get; }

    public LoadedRigidAttachmentSet Get(string key, string expectedSha256)
    {
        ThrowIfDisposed();
        ValidateRequest(key, expectedSha256);
        var identity = new AttachmentMeshRef(key, expectedSha256);
        if (_definitions.TryGetValue(identity, out CachedDefinition? cached)) return cached.View;

        byte[] definitionBytes = ReadVerified(identity, key);
        RigidAttachmentSetDefinition definition = RigidAttachmentSetJson.Parse(definitionBytes, key);
        var pending = new List<PendingMesh>();
        foreach (AttachmentMeshRef mesh in definition.Meshes)
        {
            if (_meshes.ContainsKey(mesh)) continue;
            uint id = definition.Parts.First(part => part.Mesh == mesh).Id;
            string context = $"{key}: attachment part {id}, mesh '{mesh.Key}'";
            byte[] bytes = ReadVerified(mesh, context);
            AttachmentGlb.ValidateRigidEmbedded(bytes, context);
            pending.Add(new PendingMesh(mesh, bytes, Decode(bytes, context)));
        }

        var staged = new Dictionary<AttachmentMeshRef, CachedMesh>();
        try
        {
            foreach (PendingMesh mesh in pending)
                staged.Add(mesh.Reference, new CachedMesh(mesh.Bytes, Scene.UploadAttachment(mesh.Parts)));
            var handles = new MeshHandle[definition.Parts.Count][];
            for (int i = 0; i < handles.Length; i++)
            {
                AttachmentMeshRef mesh = definition.Parts[i].Mesh;
                CachedMesh upload = _meshes.TryGetValue(mesh, out CachedMesh? existing) ? existing : staged[mesh];
                handles[i] = upload.Lease.Handles.ToArray();
            }
            var loaded = new LoadedRigidAttachmentSet(this, definition, expectedSha256, handles);
            foreach (KeyValuePair<AttachmentMeshRef, CachedMesh> mesh in staged) _meshes.Add(mesh.Key, mesh.Value);
            _definitions.Add(identity, new CachedDefinition(definitionBytes, loaded));
            return loaded;
        }
        catch
        {
            Release(staged.Values);
            throw;
        }
    }

    byte[] ReadVerified(AttachmentMeshRef reference, string context)
    {
        byte[] bytes;
        try { bytes = _source.Read(reference.Key).ToArray(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or KeyNotFoundException)
        {
            throw new InvalidDataException($"{context}: cannot read '{reference.Key}'.", error);
        }
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != reference.Sha256)
            throw new InvalidDataException($"{context}: '{reference.Key}' SHA-256 differs from the declared bytes.");
        return bytes;
    }

    static IReadOnlyList<GltfMeshPart> Decode(byte[] bytes, string context)
    {
        try { return GltfLoader.LoadPartsWithMaterials(bytes, context); }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or FormatException or NotSupportedException)
        {
            throw new InvalidDataException($"{context}: rigid geometry cannot be decoded.", error);
        }
    }

    static void ValidateRequest(string key, string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (Path.IsPathRooted(key) || key.Contains('\\') || key.Contains(':') || key.Any(char.IsControl)
            || key.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".."))
            throw new ArgumentException("An attachment key must be a relative forward-slash path without dot or empty segments.", nameof(key));
        if (hash is not { Length: 64 } || hash.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("An attachment revision must be a lowercase SHA-256.", nameof(hash));
    }

    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Release(_meshes.Values);
        _meshes.Clear();
        _definitions.Clear();
    }

    static void Release(IEnumerable<CachedMesh> meshes)
    {
        foreach (CachedMesh mesh in meshes)
            if (mesh.Lease is AttachmentUpload upload) upload.UnloadMeshes();
        foreach (CachedMesh mesh in meshes) mesh.Lease.Dispose();
    }

    sealed record PendingMesh(AttachmentMeshRef Reference, byte[] Bytes, IReadOnlyList<GltfMeshPart> Parts);
    sealed record CachedMesh(byte[] Bytes, IAttachmentMeshLease Lease);
    sealed record CachedDefinition(byte[] Bytes, LoadedRigidAttachmentSet View);
}
