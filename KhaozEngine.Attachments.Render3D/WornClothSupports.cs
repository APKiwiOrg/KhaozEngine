using System;
using System.Numerics;

namespace KhaozEngine.Attachments.Render3D;

// Slot storage for the previous displayed sweep and the opening and closing fixed-tick shapes.
internal sealed class WornClothSupports
{
    private const int Capacity = ClothWorkLimits.RigidSupportLimit + ClothWorkLimits.LowerSegmentLimit;
    private readonly ClothSupport[] _frame = new ClothSupport[Capacity];
    private readonly ClothSupport[] _priorFrame = new ClothSupport[Capacity];
    private readonly ClothSupport[] _displayed = new ClothSupport[Capacity];
    private ClothSupport[] _tickOpening = new ClothSupport[Capacity];
    private ClothSupport[] _tickClosing = new ClothSupport[Capacity];

    private readonly ChainPose[] _frameHulls = new ChainPose[ClothWorkLimits.HullLimit];
    private readonly ChainPose[] _priorFrameHulls = new ChainPose[ClothWorkLimits.HullLimit];
    private readonly Matrix4x4[] _tickHulls = new Matrix4x4[ClothWorkLimits.HullLimit];
    private readonly Matrix4x4[] _displayedHulls = new Matrix4x4[ClothWorkLimits.HullLimit];
    private readonly ClothHullSupport[] _hulls = new ClothHullSupport[ClothWorkLimits.HullLimit];

    private WornClothBinding _binding = null!;
    private int Rigid => _binding.Capsules.Length;
    private int Count => Rigid + _binding.Segments.Length;
    private int Hulls => _binding.Hulls.Length;

    private static ClothSupport Lower(in ColliderCapsule segment, ClothSupportIdentity identity) =>
        ClothSupport.LowerSegment(segment with { Radius = MathF.Max(1e-10f, segment.Radius) }, identity);

    internal void Begin(WornClothBinding binding, ReadOnlySpan<Matrix4x4> host, in Matrix4x4 model,
        ChainSimulation chains, bool fresh)
    {
        _binding = binding;
        for (int i = 0; i < Rigid; i++)
            _frame[i] = binding.Capsules[i].Pose(host, model);
        for (int i = 0; i < Hulls; i++)
            _frameHulls[i] = new(binding.Hulls[i].Pose(host, model));
        for (int i = 0; i < binding.Segments.Length; i++)
        {
            var lower = binding.Segments[i];
            _frame[Rigid + i] = Lower(chains.ShownSegment(lower.Segment), lower.Identity);
        }

        if (fresh)
        {
            SaveDisplay();
            End();
            ResetTickToDisplay();
        }
    }

    internal ClothSubstep Tick(ChainSimulation chains, ChainParticles? particles, float fraction,
        ReadOnlySpan<Vector3> pins)
    {
        for (int i = 0; i < Rigid; i++)
            _tickClosing[i] = _priorFrame[i].Interpolate(_frame[i], fraction);
        for (int i = 0; i < _binding.Segments.Length; i++)
        {
            var lower = _binding.Segments[i];
            float radius = chains.Radius(lower.Segment.Chain);
            int index = Rigid + i;
            if (particles is null)
            {
                var held = Lower(chains.HeldSegment(lower.Segment), lower.Identity);
                _tickClosing[index] = _priorFrame[index].Interpolate(held, fraction);
            }
            else
            {
                var oldRadius = _tickOpening[index].LocalCapsule.Radius;
                _tickOpening[index] = Lower(lower.Segment.Sample(particles, oldRadius, opening: true), lower.Identity);
                _tickClosing[index] = Lower(lower.Segment.Sample(particles, radius, opening: false), lower.Identity);
            }
        }
        for (int i = 0; i < Hulls; i++)
        {
            var binding = _binding.Hulls[i];
            Matrix4x4 pose = _priorFrameHulls[i].Interpolate(_frameHulls[i], fraction);
            _hulls[i] = new(binding.Identity, binding.Hull.Hull, _tickHulls[i], pose);
            _tickHulls[i] = pose;
        }
        return new(pins, _tickOpening.AsSpan(0, Count), _tickClosing.AsSpan(0, Count),
            chains.Options.MaxStepSeconds, chains.Options.Gravity, _hulls.AsSpan(0, Hulls));
    }

    internal void ResetTickToDisplay()
    {
        _displayed.AsSpan(0, Count).CopyTo(_tickOpening);
        _displayedHulls.AsSpan(0, Hulls).CopyTo(_tickHulls);
    }

    internal void EndTick() => (_tickOpening, _tickClosing) = (_tickClosing, _tickOpening);

    internal ClothSubstep Display(ChainSimulation chains, ReadOnlySpan<Vector3> pins, bool fresh)
    {
        for (int i = 0; i < _binding.Segments.Length; i++)
        {
            var lower = _binding.Segments[i];
            _frame[Rigid + i] = Lower(chains.ShownSegment(lower.Segment), lower.Identity);
        }
        for (int i = 0; i < Hulls; i++)
        {
            var binding = _binding.Hulls[i];
            Matrix4x4 closing = _frameHulls[i].Matrix;
            _hulls[i] = new(binding.Identity, binding.Hull.Hull, fresh ? closing : _displayedHulls[i], closing);
        }
        ReadOnlySpan<ClothSupport> opening = fresh ? _frame : _displayed;
        return new(pins, opening[..Count], _frame.AsSpan(0, Count),
            chains.Options.MaxStepSeconds, chains.Options.Gravity, _hulls.AsSpan(0, Hulls));
    }

    internal bool IsStationary
    {
        get
        {
            for (int i = 0; i < Count; i++)
                if (!_frame[i].SamePose(_displayed[i])) return false;
            for (int i = 0; i < Hulls; i++)
                if (_frameHulls[i].Matrix != _displayedHulls[i]) return false;
            return true;
        }
    }

    internal void SaveDisplay()
    {
        _frame.AsSpan(0, Count).CopyTo(_displayed);
        for (int i = 0; i < Hulls; i++) _displayedHulls[i] = _frameHulls[i].Matrix;
    }

    internal void End()
    {
        _frame.AsSpan(0, Count).CopyTo(_priorFrame);
        _frameHulls.AsSpan(0, Hulls).CopyTo(_priorFrameHulls);
    }

    internal void Rebase(Vector3 change)
    {
        for (int i = 0; i < Count; i++)
        {
            _priorFrame[i] = _priorFrame[i].Rebased(change);
            _displayed[i] = _displayed[i].Rebased(change);
            _tickOpening[i] = _tickOpening[i].Rebased(change);
        }
        for (int i = 0; i < Hulls; i++)
        {
            var prior = _priorFrameHulls[i].Matrix;
            prior.Translation += change;
            _priorFrameHulls[i] = new(prior);
            _tickHulls[i].Translation += change;
            _displayedHulls[i].Translation += change;
        }
    }
}
