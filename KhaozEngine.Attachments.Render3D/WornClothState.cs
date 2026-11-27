using System;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

// A single solver and display history live together for the entire reservation.
internal sealed class WornClothState(BoundClothSurface profile, ClothRig rig, float scale) : IChainExternalStep
{
    private readonly ClothSimulation _solver = new(rig, ClothWorkLimits.LayeredContact);
    private readonly WornClothSupports _supports = new();
    private readonly WornClothDetail _detail = new(profile);
    private readonly Matrix4x4[] _frameGarmentPalette = new Matrix4x4[profile.JointCount];
    private readonly Matrix4x4[] _cheapGarmentPalette = new Matrix4x4[profile.JointCount];
    private readonly Vector3[] _displayed = new Vector3[rig.RestPositions.Length];
    private readonly Vector3[] _published = new Vector3[rig.RestPositions.Length];
    private readonly Vector3[] _target = new Vector3[rig.RestPositions.Length];
    private readonly Vector3[] _cheapTarget = profile.HasMaterialMapping ? new Vector3[rig.RestPositions.Length] : [];
    private readonly Vector3[] _projected = new Vector3[rig.RestPositions.Length];
    private readonly Vector3[] _framePins = new Vector3[rig.Pins.Length];
    private readonly Vector3[] _priorPins = new Vector3[rig.Pins.Length];
    private readonly Vector3[] _tickPins = new Vector3[rig.Pins.Length];
    private readonly Matrix4x4[] _displayedGarmentPalette = new Matrix4x4[SkinningMath.MaxBonesPerDraw];
    private ulong _displayRevision, _displayHistoryRevision;
    private WornMotion? _publishedOwner;
    private WornClothBinding? _publishedBinding;
    private Vector3 _publishedOrigin;

    private bool _hasDisplay;
    private bool _fresh;
    private bool _chainFittedReset;
    private bool _rebound;
    private bool _liveRequested;
    private bool _settledCloth;
    private bool _switchFrame;
    private bool _historyValid;
    private bool _sameKinematicPose;
    private float _elapsed;
    private Vector3 _origin;
    private Matrix4x4 _model;
    private ClothStepResult _solve;
    private ClothStepResult _projection;
    private ClothStepResult _detailProjection;
    private int _substeps;
    private ClothContactWork _solveWork;
    private long _factorProducts;

    internal WornMotion? Owner { get; private set; }
    internal WornClothBinding Binding { get; private set; } = null!;
    internal bool ResetForDroppedTime { get; private set; }
    internal float Scale { get; } = scale;
    internal bool Compatible(BoundClothSurface surface) => profile.CompatibleMaterial(surface);
    internal bool Live => _liveRequested;
    public ChainExclusion Excluded => _liveRequested ? Binding.Excluded : default;

    internal void Acquire(WornMotion owner, WornClothBinding binding)
    {
        Owner = owner;
        Binding = binding;
        _liveRequested = false;
        _elapsed = 0;
        _rebound = false;
        _settledCloth = false;
        _hasDisplay = false;
        _historyValid = false;
        _origin = default;
        _detail.Reset();
    }

    internal void Release()
    {
        ClearPublication();
        Owner = null;
        Binding = null!;
        _hasDisplay = false;
        _rebound = false;
        _historyValid = false;
    }

    internal void Rebind(WornMotion owner, WornClothBinding binding)
    {
        Owner = owner;
        Binding = binding;
        _rebound = true;
        _historyValid = false;
    }

    internal bool TryPresent(WornMotion wearer, int wornIndex, uint partId, out WornClothPresentation presentation)
    {
        if (ReferenceEquals(_publishedOwner, wearer) && _publishedBinding is { } binding &&
            binding.WornIndex == wornIndex && binding.Surface.PartId == partId)
        {
            presentation = new(wearer, _displayRevision, _displayHistoryRevision, wornIndex, binding.Surface,
                _published, _publishedOrigin, _model, _displayedGarmentPalette.AsSpan(0, binding.Surface.JointCount),
                _detail.Shown);
            return true;
        }
        presentation = default;
        return false;
    }

    private void ClearPublication()
    {
        _publishedOwner?.ForgetClothSnapshot(this);
        _publishedOwner = null;
        _publishedBinding = null;
        _detail.Reset();
    }

    internal void Begin(ChainSimulation chains, Skeleton skeleton, ReadOnlySpan<Matrix4x4> previousHost,
        ReadOnlySpan<Matrix4x4> host, in Matrix4x4 previousModel, in Matrix4x4 model, Vector3 origin, bool reset, bool live, bool discardTransfer)
    {
        _substeps = 0;
        _solveWork = default;
        _factorProducts = 0;
        ResetForDroppedTime = false;
        _sameKinematicPose = previousModel == model && previousHost.SequenceEqual(host);
        bool recoverFromRefusal = _hasDisplay && _solve.Status != ClothStepStatus.Accepted;
        bool resetTransfer = _rebound && (discardTransfer || _hasDisplay &&
            ChainMath.DistanceSquared(_model.Translation, model.Translation + origin) >
                (double)chains.Options.TeleportDistance * chains.Options.TeleportDistance);
        if (resetTransfer) _rebound = false;
        _solve = default;
        _projection = default;
        _detailProjection = default;
        _fresh = reset || _rebound || resetTransfer;
        _chainFittedReset = reset;
        _switchFrame = _liveRequested != live;
        if (_switchFrame)
        {
            _elapsed = 0;
            if (!live && !reset) chains.SeedFittedRange(Binding.Excluded);
        }
        _liveRequested = live;
        if (_historyValid && _origin != origin)
        {
            Vector3 change = _origin - origin;
            _supports.Rebase(change);
            Shift(_priorPins, change);
        }
        if (_hasDisplay && _origin != origin)
        {
            Vector3 change = _origin - origin;
            Shift(_displayed, change);
            if (!_rebound && _liveRequested && !_switchFrame) _solver.Rebase(change);
        }
        _origin = origin;

        if ((reset || resetTransfer) && !_rebound)
        {
            ClearPublication();
            _hasDisplay = false;
            _settledCloth = false;
        }
        if (!_hasDisplay && !_fresh && !previousHost.IsEmpty)
        {
            try
            {
                Binding.CopyFallback(Owner!, skeleton, previousHost, previousModel, _displayed, fitted: false);
                Binding.Surface.CopyPins(previousHost, previousModel, _priorPins);
                Pin(_displayed, _priorPins);
                _supports.Begin(Binding, previousHost, previousModel, chains, fresh: true);
                _historyValid = true;
                _hasDisplay = _solver.CanSeed(_displayed);
                if (_hasDisplay && Binding.Surface.HasMaterialMapping)
                {
                    CopyCheapPalette(skeleton, previousHost);
                    Binding.CopySkinPalette(skeleton, previousHost, _frameGarmentPalette);
                    _hasDisplay = _detail.Seed(Binding.Surface, _displayed, previousModel, _origin,
                        _frameGarmentPalette, _cheapGarmentPalette);
                }
            }
            catch (ClothSurfaceDataException error) when (error.Reason == ClothSurfaceRefusal.FittedTriangle)
            {
                _hasDisplay = false;
            }
        }
        if (_liveRequested && _hasDisplay && (_switchFrame || _rebound || recoverFromRefusal))
        {
            _solver.Reset(_displayed);
            if (_historyValid) _supports.ResetTickToDisplay();
        }
        _fresh |= !_hasDisplay;
        _supports.Begin(Binding, host, model, chains, fresh: !_historyValid);
        Binding.Surface.CopyPins(host, model, _framePins);
    }

    public void OnDroppedTime()
    {
        if (_sameKinematicPose) return;
        ResetForDroppedTime = true;
        ClearPublication();
        _fresh = true;
        _hasDisplay = false;
        _settledCloth = false;
    }

    public void OnSubstep(ChainSimulation chains, ChainParticles? particles, float fraction)
    {
        if (!_liveRequested || _fresh || _solve.Status != ClothStepStatus.Accepted) return;
        for (int i = 0; i < _tickPins.Length; i++)
            _tickPins[i] = Vector3.Lerp(_priorPins[i], _framePins[i], fraction);
        ClothSubstep step = _supports.Tick(chains, particles, fraction, _tickPins);
        _solve = _solver.Advance(step);
        _solveWork = new(_solveWork.BroadPhaseVisits + _solve.ContactChecks,
            _solveWork.NarrowPhaseCandidates + _solve.ContactCandidates, _solveWork.SweepRefinements + _solve.SweepRefinements);
        _factorProducts += _solve.MaterialFactorProducts;
        _substeps++;
        _supports.EndTick();
    }

    internal WornClothStatus Finish(ChainSimulation chains, Skeleton skeleton, ReadOnlySpan<Matrix4x4> host,
        in Matrix4x4 model, in Matrix4x4 absoluteModel, float seconds, MotionLevel level)
    {
        WornClothTier cheap = level == MotionLevel.Held ? WornClothTier.Held : WornClothTier.Simulated;
        WornClothFallback failure = WornClothFallback.None;
        float duration = chains.Options.HoldBlendSeconds;
        float progress = _switchFrame ? 0 : seconds;
        float amount = duration <= _elapsed ? 1 : MathF.Min(1, progress / (duration - _elapsed));
        var pose = _supports.Display(chains, _framePins, _fresh);
        bool live = _liveRequested && !_fresh && _solve.Status == ClothStepStatus.Accepted;
        if (live)
        {
            ReadOnlySpan<Vector3> solved = _solver.Positions;
            ReadOnlySpan<Vector3> velocity = _solver.Velocities;
            float remainder = chains.Interpolation * chains.Options.MaxStepSeconds;
            // The last accepted state already belongs to the newest tick. Its physical velocity
            // omits the contact position bias. Sweep the estimate at the current support pose.
            for (int i = 0; i < _target.Length; i++)
                _target[i] = solved[i] + remainder * velocity[i];
        }
        else if (_hasDisplay && (_rebound || _solve.Status != ClothStepStatus.Accepted))
            _displayed.CopyTo(_target, 0);
        else
        {
            try
            {
                Binding.CopyFallback(Owner!, skeleton, host, model, _target, fitted: _liveRequested && _fresh && !_chainFittedReset);
            }
            catch (ClothSurfaceDataException error) when (error.Reason is ClothSurfaceRefusal.FittedTriangle)
            {
                _projection = new(ClothStepStatus.CollapsedMaterial, 0, 0, 0, 0);
                FinishFrame();
                return Result(WornClothFallback.TransferRefused);
            }
        }

        Pin(_target, _framePins);
        bool blend = _hasDisplay && !_fresh && (_settledCloth != _liveRequested || _elapsed < duration);
        if (!_liveRequested && blend && amount < 1 && Binding.Surface.HasMaterialMapping)
            _target.CopyTo(_cheapTarget, 0);
        if (blend && amount < 1)
            for (int i = 0; i < _target.Length; i++)
                _target[i] = Vector3.Lerp(_displayed[i], _target[i], amount);
        Pin(_target, _framePins);

        _projection = _fresh ? _solver.ProjectDisplay(_target, _projected, pose) :
            _solver.ProjectDisplay(_displayed, _target, _projected, pose);
        bool endpoint = !_fresh && blend &&
            (amount == 0 && _supports.IsStationary && _framePins.AsSpan().SequenceEqual(_priorPins) || (!_liveRequested && amount == 1));
        bool admissible = _projection.Status == ClothStepStatus.Accepted && (!endpoint || Matches(_target, _projected));
        if (admissible)
        {
            Binding.CopySkinPalette(skeleton, host, _frameGarmentPalette);
            bool liveDetail = _liveRequested && (!_fresh || _rebound);
            if (Binding.Surface.HasMaterialMapping && !liveDetail) CopyCheapPalette(skeleton, host);
            bool sameMaterialPose = _displayed.AsSpan().SequenceEqual(_projected) &&
                (_publishedOwner is null ? _sameKinematicPose :
                    _frameGarmentPalette.AsSpan().SequenceEqual(_displayedGarmentPalette.AsSpan(0, Binding.Surface.JointCount)));
            _detailProjection = _detail.Stage(Binding.Surface, _projected, _cheapTarget, model, _origin,
                _frameGarmentPalette, _cheapGarmentPalette, liveDetail, blend ? amount : 1, sameMaterialPose, pose, _projection);
            admissible = _detailProjection.Status == ClothStepStatus.Accepted;
            if (admissible) _detail.Publish(model, _origin);
        }
        if (admissible)
        {
            _projected.CopyTo(_displayed, 0);
            _hasDisplay = true;
            Publish(absoluteModel);
            _rebound = false;
            _supports.SaveDisplay();
            if (_fresh || _solve.Status != ClothStepStatus.Accepted)
            {
                _solver.Reset(_displayed);
                _supports.ResetTickToDisplay();
                _elapsed = 0;
            }
            else if (blend)
                _elapsed = MathF.Min(duration, _elapsed + progress);
            if (amount == 1 && (!_fresh || duration == 0))
                _settledCloth = _liveRequested;
            if (!_liveRequested && !_settledCloth && amount == 1)
                _hasDisplay = false;
        }
        else
        {
            failure = WornClothFallback.TransferRefused;
            if (!_rebound && _switchFrame && _liveRequested && !_settledCloth &&
                !ReferenceEquals(_publishedOwner, Owner))
            {
                _liveRequested = false;
                _hasDisplay = false;
            }
        }

        if (_solve.Status != ClothStepStatus.Accepted)
            failure = WornClothFallback.SolveRefused;
        FinishFrame();
        return Result(failure);

        WornClothStatus Result(WornClothFallback refusal) =>
            new(_settledCloth && ReferenceEquals(_publishedOwner, Owner) ? WornClothTier.Cloth : cheap,
                refusal, Binding.Surface.PartId, _substeps, _solve, _projection)
            {
                DetailedProjection = _detailProjection,
                IsBlending = _hasDisplay && (_settledCloth != _liveRequested || _elapsed < duration),
                SolveWork = _solveWork,
                MaterialFactorProducts = _factorProducts,
                LowerSegments = Binding.Segments.Length,
                RigidSupports = Binding.Capsules.Length,
                Hulls = Binding.Hulls.Length,
            };
    }

    private void CopyCheapPalette(Skeleton skeleton, ReadOnlySpan<Matrix4x4> host)
    {
        Binding.CopySkinPalette(skeleton, host, _cheapGarmentPalette);
        Owner!.CopyChainPalette(Binding.WornIndex, Binding.Surface.PartId, host, _cheapGarmentPalette, useClothFallback: true);
    }

    private void Publish(in Matrix4x4 absoluteModel)
    {
        _frameGarmentPalette.CopyTo(_displayedGarmentPalette, 0);
        _displayed.CopyTo(_published, 0);
        if (!ReferenceEquals(_publishedOwner, Owner))
            _publishedOwner?.ForgetClothSnapshot(this);
        _publishedOwner = Owner;
        _publishedBinding = Binding;
        _publishedOrigin = _origin;
        _model = absoluteModel;
        _displayRevision = Owner!.PoseRevision;
        _displayHistoryRevision = Owner.HistoryRevision;
    }

    private void FinishFrame()
    {
        _framePins.CopyTo(_priorPins, 0);
        _supports.End();
        _historyValid = true;
    }

    internal bool CanRelease => !_liveRequested && !_hasDisplay;

    private void Pin(Span<Vector3> nodes, ReadOnlySpan<Vector3> pins)
    {
        for (int i = 0; i < pins.Length; i++)
            nodes[rig.Pins[i]] = pins[i];
    }

    private static void Shift(Span<Vector3> points, Vector3 change)
    {
        for (int i = 0; i < points.Length; i++) points[i] += change;
    }

    private static bool Matches(ReadOnlySpan<Vector3> target, ReadOnlySpan<Vector3> result)
    {
        const float tolerance = .0005f;
        for (int node = 0; node < target.Length; node++)
            if (Vector3.DistanceSquared(target[node], result[node]) > tolerance * tolerance)
                return false;
        return true;
    }
}
