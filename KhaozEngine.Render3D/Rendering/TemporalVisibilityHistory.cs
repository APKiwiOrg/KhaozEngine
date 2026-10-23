using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using static KhaozEngine.Render3D.Internal.FragmentPassResources;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>Internal-resolution R8 visibility following the colour owner's indices and generation. A completed
/// copy seeds only its write index. The read texture stays intact for resolve re-evaluation.</summary>
internal sealed class TemporalVisibilityHistory
{
    readonly IGpuTexture?[] _textures = new IGpuTexture?[2];
    readonly bool[] _fullyLit = new bool[2];
    IGpuTexture? _fallback;
    IGpuDevice? _device;
    TemporalHistory? _owner;
    int _generation = -1, _seededIndex = -1;
    long _storedFrame = -1;

    internal bool Allocated => _textures[0] is not null && _textures[1] is not null;
    internal bool Ready => _owner is { IsValid: true } owner && _generation == owner.TargetGeneration
        && _seededIndex == owner.ReadIndex;
    internal bool ReadFullyLit => Ready && _fullyLit[_owner!.ReadIndex];
    internal bool StoredThisFrame => _owner is { } owner && _storedFrame >= 0
        && _storedFrame == owner.ResolveFrameIndex;

    internal bool Matches(IGpuDevice device, TemporalHistory owner) => Allocated
        && ReferenceEquals(device, _device) && ReferenceEquals(owner, _owner)
        && _generation == owner.TargetGeneration && _textures[0]!.Width == owner.InternalWidth
        && _textures[0]!.Height == owner.InternalHeight;

    internal bool Ensure(IGpuDevice device, TemporalHistory owner, GpuRetireQueue? retired)
    {
        if (!owner.TargetsAllocated) throw new InvalidOperationException("Colour history must be allocated first.");
        if (Matches(device, owner)) return false;
        Release(ReferenceEquals(device, _device) ? retired : null);
        _device = device;
        _owner = owner;
        // Keep the device before allocation so a partial creation remains releasable, but publish the generation last.
        for (int i = 0; i < 2; i++) _textures[i] = CreateTexture();
        _generation = owner.TargetGeneration;
        return true;
    }

    IGpuTexture CreateTexture() => _device!.Factory.CreateTexture(GpuTextureDescription.Texture2D(
        (uint)_owner!.InternalWidth, (uint)_owner.InternalHeight, ShadowVisibilityTargets.Format,
        GpuTextureUsage.Sampled | GpuTextureUsage.RenderTarget));

    internal IGpuTexture FullyLit()
    {
        if (_fallback is not null) return _fallback;
        IGpuTexture texture = CreateTexture();
        _fallback = texture;
        var bytes = new byte[checked((int)(texture.Width * texture.Height))];
        Array.Fill(bytes, byte.MaxValue);
        _device!.UpdateTexture(texture, bytes, 0, 0, texture.Width, texture.Height);
        return texture;
    }

    internal IGpuTexture Texture(int index) => _textures[index]
        ?? throw new InvalidOperationException("Visibility history is not allocated.");

    internal void Store(IGpuCommandList commands, IGpuTexture source, int writeIndex, bool fullyLit = false)
    {
        if (StoredThisFrame) return;
        if (_owner is null || _owner.ResolveFrameIndex < 0 || writeIndex != _owner.WriteIndex)
            throw new InvalidOperationException("Visibility must store the colour owner's active write index.");
        commands.CopyTexture(source, Texture(writeIndex));
        _fullyLit[writeIndex] = fullyLit;
        _seededIndex = writeIndex;
        _storedFrame = _owner.ResolveFrameIndex;
    }

    internal void Release(GpuRetireQueue? retired)
    {
        if (_device is not null && retired is null)
        {
            if (GpuRecording.OpenOwner(_device) is { } owner)
                throw new InvalidOperationException($"Visibility history cannot drain while {owner} is recording. Pass the retire queue.");
            _device.WaitForIdle();
        }
        for (int i = 0; i < 2; i++) Free(ref _textures[i], retired);
        Free(ref _fallback, retired);
        _device = null;
        _owner = null;
        _generation = _seededIndex = -1;
        _storedFrame = -1;
        Array.Clear(_fullyLit);
    }
}
