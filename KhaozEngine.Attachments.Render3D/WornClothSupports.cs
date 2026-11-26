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
    private readonly ClothHeldEdge[] _framePath = new ClothHeldEdge[ClothWorkLimits.LowerSegmentLimit];
    private readonly ClothHeldEdge[] _displayedPath = new ClothHeldEdge[ClothWorkLimits.LowerSegmentLimit];
    private ClothHeldEdge[] _tickOpeningPath = new ClothHeldEdge[ClothWorkLimits.LowerSegmentLimit];
    private ClothHeldEdge[] _tickClosingPath = new ClothHeldEdge[ClothWorkLimits.LowerSegmentLimit];

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

    private ClothSupport ShownLower(ChainSimulation chains, in WornClothSegment lower, int index) => chains.IsHeld
        ? Held(chains.ShownHeldSegment(lower.Segment), lower, index, _framePath)
        : Lower(chains.ShownSegment(lower.Segment), lower.Identity);

    private static ClothSupport Held(in HeldChainSegment held, in WornClothSegment lower, int index, ClothHeldEdge[] path)
    {
        ClothHeldEdge edge = ClothHeldEdge.From(held);
        path[index] = edge;
        int upstream = lower.Segment.Index;
        return ClothSupport.HeldLowerSegment(held, edge, lower.Identity, index - upstream, upstream);
    }

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
            _frame[Rigid + i] = ShownLower(chains, lower, i);
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
            _tickClosing[i] = _priorFrame[i].InterpolateRigid(_frame[i], fraction);
        for (int i = 0; i < _binding.Segments.Length; i++)
        {
            var lower = _binding.Segments[i];
            float radius = chains.Radius(lower.Segment.Chain);
            int index = Rigid + i;
            if (particles is null)
            {
                _tickClosing[index] = Held(chains.HeldSegment(lower.Segment, fraction), lower, i, _tickClosingPath);
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
            chains.Options.MaxStepSeconds, chains.Options.Gravity, _hulls.AsSpan(0, Hulls),
            new ClothHeldPaths(_tickOpeningPath, _tickClosingPath));
    }

    internal void ResetTickToDisplay()
    {
        CopySupports(_displayed, _displayedPath, _tickOpening, _tickOpeningPath);
        _displayedHulls.AsSpan(0, Hulls).CopyTo(_tickHulls);
    }

    internal void EndTick()
    {
        (_tickOpening, _tickClosing) = (_tickClosing, _tickOpening);
        (_tickOpeningPath, _tickClosingPath) = (_tickClosingPath, _tickOpeningPath);
    }

    internal ClothSubstep Display(ChainSimulation chains, ReadOnlySpan<Vector3> pins, bool fresh)
    {
        for (int i = 0; i < _binding.Segments.Length; i++)
        {
            var lower = _binding.Segments[i];
            _frame[Rigid + i] = ShownLower(chains, lower, i);
        }
        for (int i = 0; i < Hulls; i++)
        {
            var binding = _binding.Hulls[i];
            Matrix4x4 closing = _frameHulls[i].Matrix;
            _hulls[i] = new(binding.Identity, binding.Hull.Hull, fresh ? closing : _displayedHulls[i], closing);
        }
        ReadOnlySpan<ClothSupport> opening = fresh ? _frame : _displayed;
        return new(pins, opening[..Count], _frame.AsSpan(0, Count),
            chains.Options.MaxStepSeconds, chains.Options.Gravity, _hulls.AsSpan(0, Hulls),
            new ClothHeldPaths(fresh ? _framePath : _displayedPath, _framePath));
    }

    internal bool IsStationary
    {
        get
        {
            for (int i = 0; i < Count; i++)
                if (!_frame[i].SamePose(_displayed[i], new ClothHeldPaths(_framePath, _displayedPath))) return false;
            for (int i = 0; i < Hulls; i++)
                if (_frameHulls[i].Matrix != _displayedHulls[i]) return false;
            return true;
        }
    }

    internal void SaveDisplay()
    {
        CopySupports(_frame, _framePath, _displayed, _displayedPath);
        for (int i = 0; i < Hulls; i++) _displayedHulls[i] = _frameHulls[i].Matrix;
    }

    internal void End()
    {
        _frame.AsSpan(0, Rigid).CopyTo(_priorFrame);
        _frameHulls.AsSpan(0, Hulls).CopyTo(_priorFrameHulls);
    }

    private void CopySupports(ClothSupport[] source, ClothHeldEdge[] sourcePath, ClothSupport[] destination,
        ClothHeldEdge[] destinationPath)
    {
        source.AsSpan(0, Count).CopyTo(destination);
        sourcePath.AsSpan(0, _binding.Segments.Length).CopyTo(destinationPath);
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
