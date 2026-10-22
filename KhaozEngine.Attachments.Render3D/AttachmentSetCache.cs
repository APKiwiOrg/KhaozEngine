using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>Owns verified source snapshots and shared uploads for one scene.</summary>
/// <remarks>Use on the owning render thread. Dispose before the scene, after stopping use of its views.</remarks>
public sealed class AttachmentSetCache : IDisposable
{
    readonly IAttachmentAssetSource _source;
    readonly Dictionary<DefinitionIdentity, CachedDefinition> _definitions = [];
    readonly Dictionary<MeshIdentity, CachedMesh> _meshes = [];
    bool _disposed;

    public AttachmentSetCache(Scene3D scene, IAttachmentAssetSource source)
        : this(new Scene3DAttachmentScene(scene), source) { }

    public AttachmentSetCache(IAttachmentScene scene, IAttachmentAssetSource source)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(source);
        Scene = scene;
        _source = source;
    }

    internal IAttachmentScene Scene { get; }

    public LoadedAttachmentSet Get(string key, string expectedSha256) =>
        Get(key, expectedSha256, SkinningProfile.LinearBlend);

    /// <summary>Select one immutable profile for all garment material parts before importing or uploading.</summary>
    public LoadedAttachmentSet Get(string key, string expectedSha256, SkinningProfile profile)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(profile);
        ValidateRequest(key, expectedSha256);
        var reference = new AttachmentMeshRef(key, expectedSha256);
        var identity = new DefinitionIdentity(reference, profile);
        _definitions.TryGetValue(identity, out CachedDefinition? cached);
        byte[] definitionBytes = cached?.Bytes ?? ReadVerified(reference, key);
        AttachmentSetDefinition definition = cached?.View.Definition ?? AttachmentSetJson.Parse(definitionBytes, key);
        Dictionary<AttachmentMeshRef, MeshIdentity> requested = Classify(definition, key, profile);
        if (cached is not null) return cached.View;

        var pending = new List<PendingMesh>();
        foreach (AttachmentMeshRef mesh in definition.Meshes)
        {
            MeshIdentity meshIdentity = requested[mesh];
            if (_meshes.ContainsKey(meshIdentity)) continue;
            uint id = definition.Parts.First(part => part.Mesh == mesh).Id;
            string context = $"{key}: attachment part {id}, mesh '{mesh.Key}'";
            byte[] bytes = ReadVerified(mesh, context);
            pending.Add(Decode(meshIdentity, bytes, context));
        }

        var staged = new Dictionary<MeshIdentity, CachedMesh>();
        try
        {
            foreach (PendingMesh mesh in pending)
                staged.Add(mesh.Identity, Upload(mesh));
            var resources = new AttachmentPartResources[definition.Parts.Count];
            for (int i = 0; i < resources.Length; i++)
            {
                MeshIdentity mesh = requested[definition.Parts[i].Mesh];
                CachedMesh upload = _meshes.TryGetValue(mesh, out CachedMesh? existing) ? existing : staged[mesh];
                resources[i] = upload.Resources;
            }
            var loaded = new LoadedAttachmentSet(this, definition, expectedSha256, profile, resources);
            foreach (KeyValuePair<MeshIdentity, CachedMesh> mesh in staged) _meshes.Add(mesh.Key, mesh.Value);
            _definitions.Add(identity, new CachedDefinition(definitionBytes, loaded));
            return loaded;
        }
        catch
        {
            Release(staged.Values);
            throw;
        }
    }

    Dictionary<AttachmentMeshRef, MeshIdentity> Classify(AttachmentSetDefinition definition, string key, SkinningProfile profile)
    {
        var requested = new Dictionary<AttachmentMeshRef, MeshIdentity>();
        foreach (AttachmentPartDefinition part in definition.Parts)
        {
            bool skinned = part is SkinnedAttachmentPartDefinition;
            var identity = new MeshIdentity(part.Mesh, skinned, skinned ? profile : SkinningProfile.LinearBlend);
            if ((requested.TryGetValue(part.Mesh, out MeshIdentity previous) && previous != identity)
                || _meshes.Keys.Any(mesh => mesh.Reference == part.Mesh && mesh.Skinned != skinned))
                throw new InvalidDataException($"{key}: attachment part {part.Id}, mesh '{part.Mesh.Key}' cannot use both rigid and skinned policies.");
            requested[part.Mesh] = identity;
        }
        return requested;
    }

    CachedMesh Upload(PendingMesh mesh)
    {
        IDisposable? lease = null;
        try
        {
            AttachmentPartResources resources;
            if (mesh.SkinnedParts is { } skinned)
            {
                ISkinnedAttachmentMeshLease upload = Scene.UploadSkinnedAttachment(skinned);
                lease = upload;
                resources = new AttachmentPartResources(upload.Handles, skinned[0].Mesh);
            }
            else
            {
                IAttachmentMeshLease upload = Scene.UploadAttachment(mesh.RigidParts!);
                lease = upload;
                resources = new AttachmentPartResources(upload.Handles);
            }
            return new CachedMesh(mesh.Bytes, lease, resources);
        }
        catch
        {
            lease?.Dispose();
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

    static PendingMesh Decode(MeshIdentity identity, byte[] bytes, string context)
    {
        if (identity.Skinned) AttachmentGlb.ValidateSkinnedEmbedded(bytes, context);
        else AttachmentGlb.ValidateRigidEmbedded(bytes, context);
        try
        {
            return identity.Skinned
                ? new PendingMesh(identity, bytes, null, GltfLoader.LoadSkinnedPartsWithMaterials(bytes, context, identity.Profile))
                : new PendingMesh(identity, bytes, GltfLoader.LoadPartsWithMaterials(bytes, context), null);
        }
        catch (Exception error) when (error is InvalidDataException or IOException or InvalidOperationException
            or ArgumentException or FormatException or NotSupportedException)
        {
            string kind = identity.Skinned ? "skinned" : "rigid";
            throw new InvalidDataException($"{context}: {kind} geometry cannot be decoded.", error);
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
            if (mesh.Lease is IAttachmentUpload upload) upload.UnloadMeshes();
        foreach (CachedMesh mesh in meshes) mesh.Lease.Dispose();
    }

    readonly record struct DefinitionIdentity(AttachmentMeshRef Reference, SkinningProfile Profile);
    readonly record struct MeshIdentity(AttachmentMeshRef Reference, bool Skinned, SkinningProfile Profile);
    sealed record PendingMesh(MeshIdentity Identity, byte[] Bytes,
        IReadOnlyList<GltfMeshPart>? RigidParts, IReadOnlyList<GltfSkinnedMeshPart>? SkinnedParts);
    sealed record CachedMesh(byte[] Bytes, IDisposable Lease, AttachmentPartResources Resources);
    sealed record CachedDefinition(byte[] Bytes, LoadedAttachmentSet View);
}
