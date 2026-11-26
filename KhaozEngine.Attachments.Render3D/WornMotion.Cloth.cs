using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Determinism;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

public sealed partial class WornMotion
{
    private readonly Dictionary<uint, WornClothBinding?>?[] _cloth;
    private readonly Matrix4x4[] _lastHost;
    private Matrix4x4 _lastModel;
    private bool _hasLastHost;
    private Vector3 _origin, _preparedOrigin;
    private WornClothSlot? _activeCloth;
    private WornClothBinding? _preparedCloth;
    private WornClothFallback _preparedRefusal;
    private MotionLevel _lastLevel;

    /// <summary>Changes on each accepted Step or Reset.</summary>
    public ulong PoseRevision { get; private set; }

    /// <summary>Changes when reset, a teleport, a long gap or refitting breaks pose history.</summary>
    public ulong HistoryRevision { get; private set; }

    /// <summary>The current selected tier or the reason the requested tier is unavailable.</summary>
    public WornClothStatus ClothStatus { get; private set; }
    internal WornClothSlot? ActiveClothSlot => _activeCloth;

    /// <summary>Read the last admissible material sample for the named, visible fitted part.</summary>
    /// <remarks>Use the snapshot's pose and model together. Its display revision may predate
    /// <see cref="PoseRevision"/> after a refused sweep.</remarks>
    public bool TryGetClothPresentation(int wornIndex, uint partId, out WornClothPresentation presentation)
    {
        if (_activeCloth is { } slot)
            return slot.State.TryPresent(wornIndex, partId, out presentation);
        presentation = default;
        return false;
    }

    internal WornClothBinding? FindClothBinding(int wornIndex, uint partId) =>
        (uint)wornIndex < (uint)_cloth.Length ? _cloth[wornIndex]?.GetValueOrDefault(partId) : null;

    private WornClothBinding? GetClothBinding(int wornIndex, uint partId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(wornIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(wornIndex, _cloth.Length);
        if (_cloth[wornIndex] is { } parts && parts.TryGetValue(partId, out var binding))
            return binding;
        throw new AttachmentSubmissionArgumentException(AttachmentSubmissionRefusal.ClothPartUnavailable,
            "The selection must name a cloth part in this worn set.", "cloth") { WornIndex = wornIndex, PartId = partId };
    }

    internal void AttachCloth(WornClothSlot slot)
    {
        _activeCloth = slot;
        HistoryRevision++;
    }

    internal void DetachCloth(WornClothSlot slot)
    {
        if (!ReferenceEquals(slot, _activeCloth)) return;
        var part = slot.State.Binding.Part;
        _simulation.SeedRange(part.FirstChain, part.Binding.Chains.Length);
        _activeCloth = null;
        HistoryRevision++;
        ClothStatus = Cheap(_lastLevel, WornClothFallback.None, null);
    }

    private Matrix4x4 PrepareClothFrame(Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 model,
        WornClothSelection? selection)
    {
        _preparedOrigin = _origin;
        _preparedCloth = null;
        _preparedRefusal = WornClothFallback.None;
        if (selection is { } cloth)
        {
            ArgumentNullException.ThrowIfNull(cloth.Slot);
            _preparedCloth = GetClothBinding(cloth.WornIndex, cloth.PartId);
            _preparedRefusal = _preparedCloth is null ? WornClothFallback.Hidden :
                cloth.Slot.Admission(this, _preparedCloth, model, _simulation.Options);
        }
        if ((_preparedCloth is not null && _preparedRefusal is WornClothFallback.None or WornClothFallback.SlotOccupied ||
            _activeCloth is not null || _origin != Vector3.Zero) && Vector3.DistanceSquared(model.Translation, _origin) > 32 * 32)
            _preparedOrigin = model.Translation;

        Matrix4x4 relative = Relative(model, _preparedOrigin);
        if (_preparedCloth is { HasCapacity: true } candidate)
            candidate.ValidatePose(this, skeleton, palette, relative);
        if (_activeCloth is { } active && !ReferenceEquals(active.State.Binding, _preparedCloth))
            active.State.Binding.ValidatePose(this, skeleton, palette, relative);
        return relative;
    }

    private WornClothState? BeginCloth(ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 relative, in Matrix4x4 absolute,
        float seconds, MotionLevel level, WornClothSelection? selection)
    {
        _lastLevel = level;
        WornClothSlot? requested = selection?.Slot;
        var refusal = _preparedRefusal;
        if (_preparedCloth is { } candidate && requested is not null)
            refusal = requested.Admission(this, candidate, absolute, _simulation.Options);

        if (_activeCloth is { } existing &&
            existing.Admission(this, existing.State.Binding, absolute, _simulation.Options) == WornClothFallback.UnsupportedTransform)
            existing.Release();

        if (_activeCloth is null && _preparedCloth is { } binding && requested is not null && refusal == WornClothFallback.None)
        {
            requested.State.Acquire(this, binding);
            _activeCloth = requested;
        }
        ClothStatus = Cheap(level, refusal, selection?.PartId);
        if (_activeCloth is not { } active) return null;

        var state = active.State;
        bool samePart = ReferenceEquals(active, requested) && ReferenceEquals(state.Binding, _preparedCloth);
        if (selection is not null && !samePart && refusal == WornClothFallback.None)
            ClothStatus = ClothStatus with { Fallback = WornClothFallback.SlotOccupied };
        bool live = samePart && refusal == WornClothFallback.None;
        var previous = _hasLastHost ? Relative(_lastModel, _origin) : relative;
        state.Begin(_simulation, _skeleton, _hasLastHost ? _lastHost : [], palette, previous, relative, _origin,
            _simulation.NeedsReset(relative, seconds), live);
        return state;
    }

    private void EndCloth(WornClothState state, ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 relative,
        in Matrix4x4 absolute, float seconds, MotionLevel level)
    {
        if (state.ResetForDroppedTime) HistoryRevision++;
        var result = state.Finish(_simulation, _skeleton, palette, relative, absolute, seconds, level);
        if (state.CanRelease)
        {
            _activeCloth = null;
            state.Release();
        }
        var requestRefusal = ClothStatus.Fallback;
        ClothStatus = result with
        {
            RequestedPartId = ClothStatus.RequestedPartId,
            Fallback = result.Fallback == WornClothFallback.None ? requestRefusal : result.Fallback,
        };
    }

    private void ResetCloth()
    {
        PoseRevision++;
        HistoryRevision++;
        if (_activeCloth is not { } slot || !_hasLastHost) return;
        using var scope = DeterministicFpScope.Enter();
        var relative = Relative(_lastModel, _origin);
        var state = slot.State;
        state.Begin(_simulation, _skeleton, _lastHost, _lastHost, relative, relative, _origin,
            reset: true, live: state.Live);
        EndCloth(state, _lastHost, relative, _lastModel, 0, _lastLevel);
    }

    private static WornClothStatus Cheap(MotionLevel level, WornClothFallback reason, uint? partId) =>
        new(level == MotionLevel.Held ? WornClothTier.Held : WornClothTier.Simulated, reason, partId, 0, default, default)
        { RequestedPartId = partId };

    private static Matrix4x4 Relative(in Matrix4x4 model, Vector3 origin)
    {
        var relative = model;
        if (origin != Vector3.Zero)
            relative.Translation -= origin;
        return relative;
    }
}
