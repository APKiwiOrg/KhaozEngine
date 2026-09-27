using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Internal;

/// <summary>
/// THE RESOLVE'S TARGETS ON THE HISTORY OWNER (docs/design/TEMPORAL-RESOLVE-UPSCALING-DESIGN-2026-09-24.md, section 3
/// and plan amendments 2, 3 and 6). Two display-resolution colour histories and two confidence and stability targets,
/// which the temporal resolve reads and writes alternately, and two internal-resolution previous depths for its
/// disocclusion test, alternating with them. Formats are <see cref="TemporalFormats"/>'s. Created while the resolve
/// runs, reused while both sizes hold, recreated when either changes, released when the resolve stops. A reset
/// (<see cref="Invalidate"/>) keeps them, since <see cref="IsValid"/> already stops every read.
/// <para><b>A NEW GENERATION IS A RESET.</b> Every creation bumps <see cref="TargetGeneration"/>, a recreation at the
/// same sizes after <see cref="ReleaseTargets"/> included, and so does every release of allocated targets, so a consumer
/// holding sets over released targets sees the change even when nothing replaces them. New targets hold nothing, so a
/// consumer that sees the generation change invalidates the history before the resolve reads it, and rebuilds any
/// resource set it cached over the old targets. This type never invalidates on its own. <see cref="EnsureTargets"/>
/// returns true on a creation and leaves the reason to its caller.</para>
/// <para><b>THE PAIR FLIPS ONCE PER FRAME INDEX.</b> The resolve reads <see cref="ReadIndex"/> and writes
/// <see cref="WriteIndex"/>. A repeated index keeps the pair, so history advances once per frame as the foundations
/// design requires. The scene resolves on a frame's first render only. A later render inside the frame, such as an
/// offscreen capture, presents its internal frame unresolved and leaves the pair and its contents alone.</para>
/// <para><b>OLD TARGETS ARE RETIRED, NOT FREED IN PLACE.</b> A frame the device has not finished may still read them, so
/// a recreation or a release hands them to the caller's <see cref="GpuRetireQueue"/>, which frees them at a frame
/// boundary once the GPU is done. With no queue the release drains the device and frees them at once, which suits a
/// caller that has none, such as a test, and teardown after the device has drained.</para>
/// </summary>
internal sealed partial class TemporalHistory
{
    readonly IGpuTexture?[] _historyColor = new IGpuTexture?[2];
    readonly IGpuTexture?[] _historyConfidence = new IGpuTexture?[2];
    readonly IGpuFramebuffer?[] _resolveFramebuffers = new IGpuFramebuffer?[2];
    readonly IGpuTexture?[] _previousDepth = new IGpuTexture?[2];
    readonly IGpuFramebuffer?[] _previousDepthFramebuffers = new IGpuFramebuffer?[2];
    IGpuDevice? _targetDevice;
    int _readIndex;
    int _writeIndex = 1;
    long _resolveFrame = -1;

    /// <summary>Whether the targets exist.</summary>
    public bool TargetsAllocated => _historyColor[0] is not null;
    public int DisplayWidth { get; private set; }
    public int DisplayHeight { get; private set; }
    public int InternalWidth { get; private set; }
    public int InternalHeight { get; private set; }

    /// <summary>Bumped on every creation of the targets, a recreation at the same sizes after
    /// <see cref="ReleaseTargets"/> included, and on every release of allocated targets. New targets hold no history,
    /// so a consumer that sees this change invalidates the history and rebuilds any resource set it cached over the old
    /// targets. A release with nothing allocated leaves it alone.</summary>
    public int TargetGeneration { get; private set; }

    /// <summary>The pair the resolve reads this frame: last frame's output.</summary>
    public int ReadIndex => _readIndex;

    /// <summary>The pair the resolve writes this frame, which the display post chain then reads.</summary>
    public int WriteIndex => _writeIndex;

    public IGpuTexture Color(int index) => _historyColor[index] ?? throw NotAllocated();
    public IGpuTexture Confidence(int index) => _historyConfidence[index] ?? throw NotAllocated();
    public IGpuFramebuffer ResolveFramebuffer(int index) => _resolveFramebuffers[index] ?? throw NotAllocated();
    public IGpuTexture PreviousDepth(int index) => _previousDepth[index] ?? throw NotAllocated();
    public IGpuFramebuffer PreviousDepthFramebuffer(int index) => _previousDepthFramebuffers[index] ?? throw NotAllocated();

    /// <summary>Create the targets for a display and an internal size, or keep them when both already match on the same
    /// device. True when they were created by this call, which is when their content is undefined, the generation has
    /// moved and the caller must not read history. Targets this replaces go to <paramref name="retired"/>, or on a
    /// device change, where the new device's queue says nothing about the old one, through the old device's drain.</summary>
    public bool EnsureTargets(IGpuDevice gd, int displayWidth, int displayHeight, int internalWidth, int internalHeight,
        GpuRetireQueue? retired = null)
    {
        int dw = Math.Max(1, displayWidth), dh = Math.Max(1, displayHeight);
        int iw = Math.Max(1, internalWidth), ih = Math.Max(1, internalHeight);
        if (TargetsAllocated && ReferenceEquals(gd, _targetDevice) && dw == DisplayWidth && dh == DisplayHeight
            && iw == InternalWidth && ih == InternalHeight)
            return false;

        FreeTargets(ReferenceEquals(gd, _targetDevice) ? retired : null);   // the creation below bumps the generation once
        // Recorded before the first creation, so a creation that throws part way leaves what it made releasable.
        _targetDevice = gd;
        IGpuResourceFactory f = gd.Factory;
        const GpuTextureUsage usage = GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled;
        for (int i = 0; i < 2; i++)
        {
            IGpuTexture color = f.CreateTexture(GpuTextureDescription.Texture2D((uint)dw, (uint)dh, TemporalFormats.HistoryColor, usage));
            _historyColor[i] = color;
            IGpuTexture confidence = f.CreateTexture(GpuTextureDescription.Texture2D((uint)dw, (uint)dh, TemporalFormats.HistoryConfidence, usage));
            _historyConfidence[i] = confidence;
            IGpuTexture depth = f.CreateTexture(GpuTextureDescription.Texture2D((uint)iw, (uint)ih, TemporalFormats.PreviousDepth, usage));
            _previousDepth[i] = depth;
            _resolveFramebuffers[i] = f.CreateFramebuffer(null, color, confidence);
            _previousDepthFramebuffers[i] = f.CreateFramebuffer(null, depth);
        }
        DisplayWidth = dw;
        DisplayHeight = dh;
        InternalWidth = iw;
        InternalHeight = ih;
        TargetGeneration++;
        return true;
    }

    /// <summary>Choose this frame's pair: the first resolve of a new frame index reads what the last frame wrote and
    /// writes the other target. A repeated index keeps the pair.</summary>
    public void BeginResolve(long frameIndex)
    {
        if (frameIndex == _resolveFrame) return;
        if (_resolveFrame >= 0)
        {
            _readIndex = _writeIndex;
            _writeIndex = 1 - _readIndex;
        }
        _resolveFrame = frameIndex;
    }

    /// <summary>Let go of every target, restart the pair and bump <see cref="TargetGeneration"/>. With
    /// <paramref name="retired"/> the targets are retired into it, costing no drain, and the queue frees them once the
    /// GPU is done with the frames that read them. Without one the device is drained and they are freed at once, which
    /// is right only where no open recording references them. At teardown release before the queue is disposed, so its
    /// flush frees them, or release with no queue after the device drained. Nothing allocated means nothing to drain
    /// for and no new generation.</summary>
    public void ReleaseTargets(GpuRetireQueue? retired = null)
    {
        if (FreeTargets(retired)) TargetGeneration++;
    }

    // Free every target and restart the pair. True when anything was allocated.
    bool FreeTargets(GpuRetireQueue? retired)
    {
        _readIndex = 0;
        _writeIndex = 1;
        _resolveFrame = -1;
        if (_targetDevice is null) return false;
        if (retired is null) _targetDevice.WaitForIdle();
        for (int i = 0; i < 2; i++)
        {
            Free(ref _resolveFramebuffers[i], retired);   // the framebuffers first, since they name the textures
            Free(ref _previousDepthFramebuffers[i], retired);
            Free(ref _historyColor[i], retired);
            Free(ref _historyConfidence[i], retired);
            Free(ref _previousDepth[i], retired);
        }
        _targetDevice = null;
        DisplayWidth = DisplayHeight = InternalWidth = InternalHeight = 0;
        return true;
    }

    static void Free<T>(ref T? resource, GpuRetireQueue? retired) where T : class, IDisposable
    {
        if (retired is not null) retired.Retire(resource);
        else resource?.Dispose();
        resource = null;
    }

    static InvalidOperationException NotAllocated() => new(
        "The temporal history targets are not allocated. They exist only while the temporal resolve runs, after "
        + "TemporalHistory.EnsureTargets.");
}
