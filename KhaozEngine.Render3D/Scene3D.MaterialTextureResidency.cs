using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D;

/// <summary>
/// Material texture residency (issue #1776): one GPU texture per image identity per scene, reference counted.
/// <para>
/// WHO HOLDS A REFERENCE. A <see cref="SurfaceMaps"/> bundle from <see cref="LoadSurfaceMaps"/> holds one per present
/// slot until <see cref="UnloadSurfaceMaps"/>. A mesh from a turn-key glTF upload (<see cref="LoadProp"/>,
/// <see cref="LoadPropMeshes"/>, <see cref="LoadMesh(GltfMesh,GltfMaterialMaps)"/> and
/// <see cref="LoadSkinnedMesh(SkinnedGltfMesh,GltfMaterialMaps)"/>) takes over its bundle's references and releases
/// them when it unloads, which is what stops a streamed or respawned glTF from leaking its textures until the scene
/// is disposed. An admitted upload (<see cref="PrepareResourceUpload(SceneUploadData,ISceneUploadReservation)"/>, the
/// path MapDoc takes) holds one per map from preparation until it is cancelled or its resource is released. The last
/// release retires the texture through the scene's retire queue, so it is destroyed once the GPU has finished with
/// it. An image without identity (one the caller built) is tracked the same way but never shared. Scene defaults are
/// the renderer's own textures and never enter the table.
/// </para>
/// <para>
/// ONE TEXTURE, WHICHEVER PATH BUILDS IT. A convenience load writes level 0 and generates the chain at once. An
/// admitted upload that is first to name an image creates the texture and leaves a <see cref="MaterialTextureFill"/>
/// on it, which every upload naming that image advances within its byte allowance and the first to finish generates,
/// the same way. A convenience load that meets a pending fill completes it before it draws anything with it. The
/// creating upload's reservation stays charged until the texture is destroyed (<see cref="SceneUploadLease"/>),
/// because its claim paid for that allocation even when another owner outlives the upload.
/// </para>
/// <para>
/// A MATERIAL WITH A GLOW owns a small parameter buffer of its own, recorded beside its references and retired with
/// the mesh. Every other material binds the renderer's shared zero block.
/// </para>
/// </summary>
public sealed partial class Scene3D
{
    sealed class ResidentTexture
    {
        internal ResidentTexture(TextureHandle handle, string? identity, long bytes)
        {
            Handle = handle;
            Identity = identity;
            Bytes = bytes;
        }

        internal TextureHandle Handle { get; }
        internal string? Identity { get; }
        internal long Bytes { get; }
        internal int References;

        /// <summary>The writes an admitted upload still owes this texture, or null once it can be sampled.</summary>
        internal MaterialTextureFill? Fill;

        /// <summary>The settlement of the admitted upload whose claim created this texture, or null.</summary>
        internal SceneUploadLease? Lease;
    }

    // What one mesh took over from the residency, released when it unloads.
    readonly record struct OwnedMaterial(TextureHandle[] Textures, IGpuBuffer? Params);

    readonly Dictionary<string, ResidentTexture> _residentByIdentity = new(System.StringComparer.Ordinal);
    readonly Dictionary<int, ResidentTexture> _residentByList = new();
    readonly Dictionary<(int Index, int Generation), OwnedMaterial> _meshMaterials = new();
    readonly Dictionary<(int Index, int Generation), OwnedMaterial> _skinnedMeshMaterials = new();
    long _residentMaterialBytes;

    /// <summary>How many material textures this scene holds for glTF maps and <see cref="LoadSurfaceMaps"/>
    /// bundles. An image shared by many parts, glbs or archetypes counts once, so a kit that shares a dozen
    /// tileable sets across a hundred pieces reads as its distinct images. Textures from a plain
    /// <see cref="LoadTexture(byte[],int,int,TextureMipPolicy)"/> are the caller's and are not counted.</summary>
    public int ResidentMaterialTextureCount => _residentByList.Count;

    /// <summary>The RGBA8 bytes of every mip level of every texture <see cref="ResidentMaterialTextureCount"/>
    /// counts, excluding driver padding and alignment.</summary>
    public long ResidentMaterialTextureBytes => _residentMaterialBytes;

    /// <summary>The texture for <paramref name="image"/>: the resident one when its identity is already uploaded,
    /// otherwise a new upload. Either way the caller now holds one reference. A failed upload leaves nothing
    /// behind, because the upload frees its own texture before throwing and nothing was recorded yet.</summary>
    TextureHandle AcquireMaterialTexture(in DecodedImage image)
    {
        string? identity = image.Identity;
        if (identity is not null && _residentByIdentity.TryGetValue(identity, out ResidentTexture? resident))
        {
            CompleteFill(resident);
            resident.References++;
            return resident.Handle;
        }

        TextureHandle handle = LoadTexture(image.Rgba, image.Width, image.Height);
        image.Source?.ReleaseAfterUpload();
        IGpuTexture texture = _textures[handle.ListIndex]!;
        var entry = new ResidentTexture(handle, identity, ChainBytes(texture.Width, texture.Height, texture.MipLevels))
        {
            References = 1,
        };
        if (identity is not null) _residentByIdentity.Add(identity, entry);
        _residentByList.Add(handle.ListIndex, entry);
        _residentMaterialBytes += entry.Bytes;
        return handle;
    }

    /// <summary>The texture an admitted upload binds for <paramref name="image"/>: the resident one when its identity
    /// is already in the scene, written or still pending, otherwise a new one whose level-0 writes and chain the
    /// returned fill owes. Either way the upload now holds one reference. A texture this creates owes
    /// <paramref name="lease"/> a settlement when it is destroyed, and <c>Created</c> says so.</summary>
    internal (TextureHandle Handle, MaterialTextureFill? Fill, bool Created) AcquireAdmittedTexture(in DecodedImage image,
        SceneUploadLease lease)
    {
        string? identity = image.Identity;
        if (identity is not null && _residentByIdentity.TryGetValue(identity, out ResidentTexture? resident))
        {
            resident.References++;
            if (resident.Fill is { Complete: true }) resident.Fill = null;
            return (resident.Handle, resident.Fill, false);
        }

        // The description LoadTexture builds for the full chain, so a shared texture is the same whichever path made it.
        uint mips = TextureMipPolicy.Full.LevelsFor(image.Width, image.Height);
        IGpuTexture texture = _gd.Factory.CreateTexture(new GpuTextureDescription((uint)image.Width, (uint)image.Height,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled | (mips > 1 ? GpuTextureUsage.GenerateMipmaps : 0), mips));
        MaterialTextureFill fill;
        try
        {
            fill = new MaterialTextureFill(texture, image);
            _textures.Add(texture);
        }
        catch
        {
            texture.Dispose();
            throw;
        }
        var handle = new TextureHandle(_textures.Count - 1);
        var entry = new ResidentTexture(handle, identity, ChainBytes(texture.Width, texture.Height, texture.MipLevels))
        {
            References = 1,
            Fill = fill,
            Lease = lease,
        };
        lease.Owe();
        if (identity is not null) _residentByIdentity.Add(identity, entry);
        _residentByList.Add(handle.ListIndex, entry);
        _residentMaterialBytes += entry.Bytes;
        return (handle, fill, true);
    }

    /// <summary>The GPU texture behind a resident handle, borrowed for binding.</summary>
    internal IGpuTexture? MaterialTextureOf(TextureHandle handle) => handle.IsValid ? _textures[handle.ListIndex] : null;

    // A convenience load is about to draw with a texture an admitted upload is still writing. It cannot wait for the
    // uploads in flight, so it writes the remaining rows and generates the chain now, as LoadTexture would have.
    void CompleteFill(ResidentTexture entry)
    {
        if (entry.Fill is not { } fill) return;
        if (!fill.Complete)
        {
            fill.WriteAll(_gd);
            if (!fill.ChainGenerated)
            {
                TextureUploads.GenerateChain(_gd, fill.Texture, "Scene3D.LoadSurfaceMaps");
                fill.MarkGenerated();
            }
        }
        entry.Fill = null;
    }

    /// <summary>Release one reference to a resident texture, retiring it with the last. False when the residency
    /// does not track <paramref name="handle"/>, which leaves the caller's own texture to the caller.</summary>
    internal bool ReleaseMaterialTexture(TextureHandle handle)
    {
        if (!handle.IsValid || !_residentByList.TryGetValue(handle.ListIndex, out ResidentTexture? entry)) return false;
        if (--entry.References > 0) return true;
        _residentByList.Remove(handle.ListIndex);
        if (entry.Identity is not null) _residentByIdentity.Remove(entry.Identity);
        _residentMaterialBytes -= entry.Bytes;
        entry.Fill = null;
        RetireTexture(handle, entry.Lease);
        return true;
    }

    // Record what a freshly published mesh owns. Nothing is recorded for a mesh that borrows its maps and binds the
    // shared zero block, which is every mesh outside the turn-key glTF uploads that has no glow.
    void RecordMeshMaterial(MeshHandle handle, TextureHandle[]? textures, IGpuBuffer? materialParams)
    {
        if (textures is not { Length: > 0 } && materialParams is null) return;
        _meshMaterials[(handle.Index, handle.Generation)] = new OwnedMaterial(textures ?? [], materialParams);
    }

    void RecordSkinnedMaterial(SkinnedMeshHandle handle, TextureHandle[]? textures, IGpuBuffer? materialParams)
    {
        if (textures is not { Length: > 0 } && materialParams is null) return;
        _skinnedMeshMaterials[(handle.Index, handle.Generation)] = new OwnedMaterial(textures ?? [], materialParams);
    }

    // Called by UnloadMesh after the mesh's own sets are retired, so the params buffer leaves in the same batch.
    void ReleaseMeshMaterial(int index, int generation) =>
        ReleaseOwned(_meshMaterials, (index, generation));

    void ReleaseSkinnedMaterial(int index, int generation) =>
        ReleaseOwned(_skinnedMeshMaterials, (index, generation));

    void ReleaseOwned(Dictionary<(int, int), OwnedMaterial> table, (int, int) key)
    {
        if (!table.Remove(key, out OwnedMaterial owned)) return;
        foreach (TextureHandle texture in owned.Textures) ReleaseMaterialTexture(texture);
        _retired.Retire(owned.Params);
    }

    // Free the owned params buffers at scene disposal, after every material set naming them. The textures go with
    // the scene's texture list, so the leases of the resident ones an admitted upload created are returned for the
    // caller to settle once that list is destroyed.
    List<SceneUploadLease> DisposeMaterialResidency(ResourceDisposal disposal)
    {
        foreach (OwnedMaterial owned in _meshMaterials.Values)
            disposal.Release("meshMaterialParams", () => owned.Params?.Dispose());
        foreach (OwnedMaterial owned in _skinnedMeshMaterials.Values)
            disposal.Release("skinnedMaterialParams", () => owned.Params?.Dispose());
        var leases = new List<SceneUploadLease>();
        foreach (ResidentTexture entry in _residentByList.Values)
            if (entry.Lease is { } lease) leases.Add(lease);
        _meshMaterials.Clear();
        _skinnedMeshMaterials.Clear();
        _residentByIdentity.Clear();
        _residentByList.Clear();
        _residentMaterialBytes = 0;
        return leases;
    }

    /// <summary>A retired texture that settles its creating upload's lease once it is destroyed.</summary>
    sealed class LeasedTexture(IGpuTexture texture, SceneUploadLease lease) : System.IDisposable
    {
        public void Dispose()
        {
            try { texture.Dispose(); }
            finally { lease.Settle(); }
        }
    }

    // RGBA8 bytes of a texture and its whole mip chain.
    static long ChainBytes(uint width, uint height, uint mipLevels)
    {
        long bytes = 0;
        for (uint level = 0; level < mipLevels; level++)
            bytes += 4L * System.Math.Max(1u, width >> (int)level) * System.Math.Max(1u, height >> (int)level);
        return bytes;
    }

    /// <summary>The model material set a mesh draws with, or null when it binds the renderer default. Borrowed for
    /// observation: never dispose or mutate it.</summary>
    internal IGpuResourceSet? MeshMaterialSetForTests(MeshHandle handle) => _meshes[handle.Index]?.MaterialSet;

    /// <summary>A skinned mesh's two material sets: the model-layout one the CPU-skinned path binds and the skinned
    /// fragment one the GPU-skinned path binds. Borrowed for observation: never dispose or mutate them.</summary>
    internal (IGpuResourceSet? Model, IGpuResourceSet? Skinned) SkinnedMaterialSetsForTests(SkinnedMeshHandle handle) =>
        _skinnedMeshes[handle.Index] is { } entry ? (entry.MaterialSet, entry.SkinnedMaterialSet) : (null, null);
}
