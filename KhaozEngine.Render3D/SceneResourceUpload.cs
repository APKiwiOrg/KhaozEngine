using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D;

/// <summary>One admitted upload. Publication transfers ownership to the scene. Cancellation retires behind its
/// completion barrier and releases its remaining lease only after destruction.</summary>
public sealed class SceneResourceUpload : IDisposable
{
    readonly Scene3D _scene;
    readonly IGpuDevice _device;
    readonly ISceneUploadReservation _reservation;
    readonly long _meshOrder;
    SceneUploadData? _data;
    readonly List<Transfer> _transfers = new();
    readonly List<IDisposable> _owned = new();
    IGpuBuffer? _vertices, _normals, _indices, _clip, _foliage, _groundParams, _anchor;
    IGpuTexture? _albedo, _normal, _roughness, _groundAlbedo;
    IGpuFence? _completion;
    IGpuCommandList? _barrier;
    int _nextTransfer;
    bool _submitted, _published, _disposed, _retiring, _transferSettled;
    internal MeshHandle PublishedMesh { get; private set; }
    internal FoliageBatch? PublishedFoliage { get; private set; }
    public SceneUploadDemand Demand { get; }
    public ScenePreparedFoliage? PreparedFoliage { get; private set; }

    internal SceneResourceUpload(Scene3D scene, IGpuDevice device, SceneUploadData data,
        SceneUploadDemand demand, ISceneUploadReservation reservation)
    {
        _scene = scene; _device = device; _data = data; Demand = demand; _reservation = reservation;
        _meshOrder = data.BorrowMesh is null ? 0 : scene.ReserveMeshOrder();
    }

    internal void Prepare()
    {
        var data = _data!;
        if (data.BorrowMesh is { } mesh)
        {
            _vertices = Buffer(MemoryMarshal.AsBytes(mesh.Vertices.AsSpan()).ToArray(), GpuBufferUsage.VertexBuffer);
            _normals = Buffer(MemoryMarshal.AsBytes(OutlineNormalBuilder.Build(mesh.Vertices, mesh.Indices32).AsSpan()).ToArray(), GpuBufferUsage.VertexBuffer);
            _indices = mesh.IndexFormat == GpuIndexFormat.UInt32
                ? Buffer(MemoryMarshal.AsBytes(mesh.Indices32.AsSpan()).ToArray(), GpuBufferUsage.IndexBuffer)
                : Buffer(MemoryMarshal.AsBytes(MeshIndices.Narrow(mesh.Indices32).AsSpan()).ToArray(), GpuBufferUsage.IndexBuffer);
            if (data.BorrowMaps is { } maps)
            { _albedo = Image(maps.Albedo); _normal = Image(maps.Normal); _roughness = Image(maps.Roughness); }
            if (!data.BorrowClipData.IsEmpty) _clip = Buffer(data.BorrowClipData.ToArray(), GpuBufferUsage.StructuredBufferReadOnly, 16);
            if (data.GroundMaterial is { } ground)
            {
                _groundAlbedo = Own(_device.Factory.CreateTexture(GpuTextureDescription.Texture2DArray(
                    (uint)ground.Width, (uint)ground.Height, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled,
                    (uint)ground.BorrowLayers.Count, TileGroundMaterialConfig.MipLevelCount(ground.Width, ground.Height))));
                for (int i = 0; i < ground.BorrowLayers.Count; i++)
                    Mips(_groundAlbedo, ground.BorrowLayers[i].AlbedoRgba, ground.Width, ground.Height, (uint)i);
                _groundParams = Buffer(MemoryMarshal.AsBytes(TileGroundMaterialConfig.BuildParams(
                    ground.BorrowLayers, ground.BaseSpecularStrength).AsSpan()).ToArray(), GpuBufferUsage.UniformBuffer);
            }
        }
        if (data.Foliage is { } source)
        {
            PreparedFoliage = _scene.PrepareFoliageUpload(source, Demand);
            _anchor = Buffer(new byte[256], GpuBufferUsage.UniformBuffer);
            if (!PreparedFoliage.BorrowPackedInstances.IsEmpty)
                _foliage = Buffer(PreparedFoliage.BorrowPackedInstances.ToArray(), GpuBufferUsage.VertexBuffer);
        }
        if (_device.Capabilities.SupportsCompletionFences)
        {
            _completion = Own(_device.Factory.CreateFence());
            _barrier = Own(_device.Factory.CreateCommandList());
        }
        if (_transfers.Count == 0) CompleteTransfer();
    }

    T Own<T>(T resource) where T : IDisposable { _owned.Add(resource); return resource; }
    IGpuBuffer Buffer(byte[] bytes, GpuBufferUsage usage, uint stride = 0)
    {
        var buffer = Own(_device.Factory.CreateBuffer(new(checked((uint)Scene3D.Align(bytes.Length, 4)), usage, stride)));
        _transfers.Add(new(buffer, bytes));
        return buffer;
    }
    IGpuTexture? Image(DecodedImage? image)
    {
        if (image is not { } i) return null;
        var texture = Own(_device.Factory.CreateTexture(new((uint)i.Width, (uint)i.Height, GpuPixelFormat.R8G8B8A8UNorm,
            GpuTextureUsage.Sampled, TileGroundMaterialConfig.MipLevelCount(i.Width, i.Height))));
        Mips(texture, i.Rgba, i.Width, i.Height, 0);
        return texture;
    }
    void Mips(IGpuTexture texture, byte[] pixels, int width, int height, uint layer)
    {
        uint mip = 0;
        while (true)
        {
            _transfers.Add(new(texture, pixels, width, height, mip++, layer));
            if (width == 1 && height == 1) break;
            int w = Math.Max(1, width / 2), h = Math.Max(1, height / 2);
            pixels = SceneUploadMips.Downsample(pixels, width, height, w, h);
            (width, height) = (w, h);
        }
    }

    /// <summary>True only when every copy completed, including the final GPU completion fence.</summary>
    public bool Ready
    {
        get
        {
            if (_disposed || !_submitted || _completion is { Signaled: false }) return false;
            if (!_transferSettled)
            {
                // Completion machinery is transfer workspace too. Some backends retain command arrays here.
                if (_barrier is not null) { _barrier.Dispose(); _owned.Remove(_barrier); _barrier = null; }
                if (_completion is not null) { _completion.Dispose(); _owned.Remove(_completion); _completion = null; }
                _reservation.TransferCompleted(); _transferSettled = true;
            }
            return true;
        }
    }

    /// <summary>Copy at most allowance bytes. Texture writes progress within bounded rows and every mip.
    /// An allowance too small for one RGBA texel makes no progress. Admission is independent of this budget.</summary>
    public long Advance(long allowance)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (allowance < 0) throw new ArgumentOutOfRangeException(nameof(allowance));
        if (_published || _submitted) return 0;
        long copied = 0;
        try
        {
            while (_nextTransfer < _transfers.Count && copied < allowance)
            {
                var transfer = _transfers[_nextTransfer];
                long written = transfer.Advance(_device, allowance - copied);
                copied += written;
                if (transfer.Finished) _nextTransfer++;
                else if (written == 0) break;
            }
            if (_nextTransfer == _transfers.Count) CompleteTransfer();
            return copied;
        }
        catch { Dispose(); throw; }
    }
    void CompleteTransfer()
    {
        if (_submitted) return;
        if (_completion is not null)
        {
            using (GpuRecording.Open(_device, _barrier!, "Scene3D.ResourceUpload")) { }
            _device.Submit(_barrier!, _completion);
        }
        else if (Demand.TransferBytes > 0) _device.WaitForIdle();
        _submitted = true;
    }

    /// <summary>Publish once, after readiness. Draw submissions can only resolve this accepted handle.</summary>
    public SceneResourceHandle Publish()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_published || !Ready) throw new InvalidOperationException("Resource must be ready and unpublished.");
        SceneResourceHandle handle = default;
        try
        {
            var data = _data!;
            if (data.BorrowMesh is { } mesh)
                PublishedMesh = _scene.PublishUploadedMesh(mesh, _vertices!, _normals!, _indices!, _albedo, _normal, _roughness,
                    data.BorrowMaps?.AlphaCutoff ?? 0, data.GroundMaterial, _groundAlbedo, _groundParams, _owned, _meshOrder);
            if (_clip is not null) _scene.PublishWaterDomain(PublishedMesh, _clip);
            if (PreparedFoliage is { } prepared) PublishedFoliage = _scene.PublishPreparedFoliage(prepared, _foliage, _anchor!, _owned);
            handle = _scene.PublishResource(this);
            _reservation.Published(handle);
            ReleaseInputs();
            _owned.Capacity = _owned.Count;
            _reservation.InputsReleased(SceneResourceCpuInventory.Measure(_owned, PublishedMesh.Generation > 0, PreparedFoliage));
            _published = true;
            return handle;
        }
        catch
        {
            if (handle.Value != 0) _scene.ForgetFailedPublication(handle);
            Dispose(); throw;
        }
    }
    /// <summary>Cancel before publication. A published upload is owned by ReleaseResource instead.</summary>
    public void Dispose()
    {
        if (_disposed || _published) return;
        Retire();
    }
    internal void Retire()
    {
        if (_retiring) return;
        _retiring = true; _disposed = true;
        _reservation.Retiring();
        // Mesh and foliage entries borrow this upload's buffers. Remove their visibility before retiring the
        // complete ownership group, whose final callback releases admission after every object is destroyed.
        _scene.DetachUploadedResource(PublishedMesh, PublishedFoliage);
        PublishedFoliage = null; PreparedFoliage = null;
        _scene.RetireUpload(this, new Retirement(_owned, _reservation));
        ReleaseInputs();
        _vertices = _normals = _indices = _clip = _foliage = _groundParams = _anchor = null;
        _albedo = _normal = _roughness = _groundAlbedo = null;
        _completion = null; _barrier = null;
    }
    void ReleaseInputs() { _transfers.Clear(); _transfers.Capacity = 0; _data = null; }
    internal void RebindMaterialSets(Func<IGpuResourceSet, IGpuResourceSet> replacementFor)
    {
        for (int i = 0; i < _owned.Count; i++)
            if (_owned[i] is IGpuResourceSet set) _owned[i] = replacementFor(set);
    }
    sealed class Retirement(List<IDisposable> owned, ISceneUploadReservation reservation) : IDisposable
    {
        bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose(); }
            finally { owned.Clear(); owned.Capacity = 0; reservation.Released(); reservation.Dispose(); }
        }
    }
    sealed class Transfer
    {
        readonly IGpuBuffer? _buffer;
        readonly IGpuTexture? _texture;
        readonly byte[] _bytes;
        readonly int _width;
        readonly uint _mip, _layer;
        int _offset;
        internal bool Finished => _offset == _bytes.Length;
        internal Transfer(IGpuBuffer buffer, byte[] bytes) { _buffer = buffer; _bytes = bytes; }
        internal Transfer(IGpuTexture texture, byte[] bytes, int width, int height, uint mip, uint layer)
        { _texture = texture; _bytes = bytes; _width = width; _mip = mip; _layer = layer; }
        internal long Advance(IGpuDevice device, long allowance)
        {
            int count = (int)Math.Min(_bytes.Length - _offset, Math.Min(allowance, int.MaxValue));
            if (_buffer is not null)
            {
                // All production buffer formats and destination offsets have a two-byte minimum alignment.
                if (count < _bytes.Length - _offset) count -= count % 4;
                if (count == 0) return 0;
                device.UpdateBuffer(_buffer, (uint)_offset, (ReadOnlySpan<byte>)_bytes.AsSpan(_offset, count));
            }
            else
            {
                int rowBytes = checked(_width * 4), x = _offset % rowBytes;
                count = Math.Min(count, rowBytes - x); count -= count % 4;
                if (count == 0) return 0;
                byte[] row = _bytes.AsSpan(_offset, count).ToArray();
                device.UpdateTexture(_texture!, row, (uint)(x / 4), (uint)(_offset / rowBytes), (uint)(count / 4), 1, _mip, _layer);
            }
            _offset += count;
            return count;
        }
    }
}
