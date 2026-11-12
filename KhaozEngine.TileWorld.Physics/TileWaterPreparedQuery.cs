using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using I = KhaozEngine.TileWorld.Physics.TileWaterArithmetic;
using V = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Vector;
using R = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Range;
using B = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Basis;

namespace KhaozEngine.TileWorld.Physics;

/// <summary>One shape/query's original expressions, filled only when the ordered evaluator reaches them.</summary>
internal ref struct TileWaterPreparedQuery
{
    internal const int MaxBases = 176, MaxPlanes = 10;
    [InlineArray(3)] internal struct Multipliers { I _element0; }
    internal struct BasisState
    {
        internal uint Mask;
        internal ushort ClipMask;
        internal bool Projected;
        internal Multipliers Lambda, Velocity;
        internal V Q, V, Offset, Motion;
        internal I A, B, Vertex, Lower, Upper;
        internal double Width, Length;
    }
    internal struct ClipTerm
    {
        internal I Constant, Slope, Crossing;
        internal bool Apply(ref double enter, ref double exit)
        {
            if (Slope.Low == 0) return Constant.Low <= 0;
            if (Slope.Low > 0) exit = Math.Min(exit, Crossing.High);
            else enter = Math.Max(enter, Crossing.Low);
            return enter <= exit;
        }
    }
    internal struct Header
    {
        internal V Start, Drive;
        internal I Core, RadiusSquared;
        internal float Radius;
        internal double HeightError, HorizontalError, InitialError;
        internal ulong ControlToken;
        internal bool HasControlToken;
    }

    readonly TileWaterConvexShape _shape;
    readonly Span<BasisState> _states;
    readonly Span<ClipTerm> _clips;
    readonly Header _header;
    int Stride => _shape.Planes.Length + 3;

    internal TileWaterPreparedQuery(TileWaterConvexShape shape, Span<BasisState> states, Span<ClipTerm> clips,
        V start, V drive, I core, float radius, double heightError = 0, double horizontalError = 0)
    {
        _shape = shape;
        _states = states[..shape.Bases.Length];
        _clips = clips[..(shape.Bases.Length * (shape.Planes.Length + 3))];
        _states.Clear();
        _header = new()
        {
            Start = start, Drive = drive, Core = core, Radius = radius,
            HeightError = heightError, HorizontalError = horizontalError,
            InitialError = start.Width + drive.Width, RadiusSquared = I.Point(radius).Square(),
            HasControlToken = TileWaterFloatingPointState.TryRead(out ulong token), ControlToken = token
        };
        if (TileWaterQueryWork.Enabled) TileWaterQueryWork.ShapePreparations++;
    }

    internal R Evaluate(double enter = 0, double exit = 1)
    {
        V start = _header.Start, drive = _header.Drive;
        I core = _header.Core;
        float radius = _header.Radius;
        double heightError = _header.HeightError, horizontalError = _header.HorizontalError;
        // A preparation cannot be reused under different controls, or an unknown control domain.
        if (!_header.HasControlToken || !TileWaterFloatingPointState.TryRead(out ulong token) || token != _header.ControlToken)
            return _shape.Intersect(start, drive, core, radius, enter, exit, heightError, horizontalError);
#if WATER_PRISM_CENSUS
        using var measurement = TileWaterCensus.Observe(_shape, start, drive, core, radius, enter, exit, heightError, horizontalError);
#endif
        if (TileWaterQueryWork.Enabled) TileWaterQueryWork.ConvexIntersections++;
        double first = double.PositiveInfinity, last = double.NegativeInfinity, nearest = double.PositiveInfinity;
        double error = _header.InitialError;
        V witnessStart = default, witnessDrive = default;
        I radiusSquared = _header.RadiusSquared;
        for (int ordinal = 0; ordinal < _shape.Bases.Length; ordinal++)
        {
            B basis = _shape.Bases[ordinal];
            ref BasisState state = ref _states[ordinal];
#if WATER_PRISM_CENSUS
            TileWaterCensus.Add("bases");
            TileWaterCensus.Add(basis.A < 0 ? "basis/0" : basis.B < 0 ? "basis/1" : basis.C < 0 ? "basis/2" : "basis/3");
#endif
            if ((state.Mask & 1) == 0)
            {
#if WATER_PRISM_CENSUS
                TileWaterCensus.Projecting = true;
#endif
                state.Projected = _shape.Project(basis, start, drive, core, heightError, horizontalError,
                    state.Lambda, state.Velocity, out state.Q, out state.V);
#if WATER_PRISM_CENSUS
                TileWaterCensus.Projecting = false;
#endif
                state.Mask |= 1;
            }
            if (!state.Projected) return new(false, false, 0, 0, 0, double.PositiveInfinity, default, default);
            V q = state.Q, v = state.V;
            double low = enter, high = exit;
            int active = basis.A < 0 ? 0 : basis.B < 0 ? 1 : basis.C < 0 ? 2 : 3;
            bool feasible = true;
            for (int i = 0; i < active && feasible; i++)
                feasible = ActiveClip(ordinal, i, ref state, ref low, ref high);
            for (int i = 0; i < _shape.Planes.Length && feasible; i++)
                feasible = PlaneClip(ordinal, i, ref state, ref low, ref high);
            if (!feasible) continue;
            if ((state.Mask & 2) == 0)
            {
                state.Width = q.Width + v.Width;
                state.Offset = start - q;
                state.Motion = drive - v;
                state.A = state.Motion.SquaredLength;
                state.B = V.Dot(state.Offset, state.Motion);
                state.Mask |= 2;
                Filled();
            }
            error = Math.Max(error, state.Width);
            V offset = state.Offset, motion = state.Motion;
            I a = state.A, b = state.B;
            I vertex;
            if (a.Low > 0)
            {
                if ((state.Mask & 4) == 0)
                {
                    state.Vertex = (I.Point(0) - b) / a;
                    state.Mask |= 4;
                    Filled();
                }
                vertex = state.Vertex;
            }
            else vertex = new I(low, high);
            I closest = new(Math.Clamp(vertex.Low, low, high), Math.Clamp(vertex.High, low, high));
            I minimum = (offset + motion * closest).SquaredLength;
            nearest = Math.Min(nearest, Math.Max(0, minimum.Low));
            if (minimum.Low > radiusSquared.High) continue;
            I whole = (offset + motion * new I(low, high)).SquaredLength;
            if (whole.High <= radiusSquared.Low)
            {
                if (low < first) { first = low; witnessStart = q; witnessDrive = v; }
                last = Math.Max(last, high);
                continue;
            }
            if (a.Low <= 0)
            {
                I bound = (offset + motion * new I(low, high)).SquaredLength;
                nearest = Math.Min(nearest, Math.Max(0, bound.Low));
                if (bound.Low > radiusSquared.High) continue;
                error = Math.Max(error, Math.Sqrt(a.High) * (high - low));
            }
            else
            {
                if (!(a.Low > 0)) return new(false, false, 0, 0, 0, double.PositiveInfinity, default, default);
                if ((state.Mask & 8) == 0)
                {
                    V atVertex = offset + motion * vertex;
                    I remaining = radiusSquared - atVertex.SquaredLength;
                    if (remaining.High < 0) { state.Mask |= 16; }
                    else
                    {
                        I reach = (remaining / a).Sqrt();
                        state.Lower = vertex - reach;
                        state.Upper = vertex + reach;
                        state.Length = Math.Sqrt(V.Dot(drive, drive).High);
                    }
                    state.Mask |= 8;
                    Filled();
                }
                if ((state.Mask & 16) != 0) continue;
                I lower = state.Lower, upper = state.Upper;
                double length = state.Length;
                var roots = TileWaterRootUncertainty.Clip(lower, upper, low, high);
                low = Math.Max(low, lower.Low);
                high = Math.Min(high, upper.High);
                double rootError = roots.Error(length);
                if (rootError > KhaozEngine.Locomotion.MovementQueryLease.CoverageSkinMetres && _shape.BedSeam is not null)
                    rootError = roots.CertifiedError(_shape.BedSeam, basis.A, basis.B, basis.C, start, drive, core, radius, length);
                error = Math.Max(error, rootError);
                if (low > high) continue;
            }
            if (low < first) { first = low; witnessStart = q; witnessDrive = v; }
            last = Math.Max(last, high);
        }
        var result = new R(double.IsFinite(error), first <= last, first, last, nearest, error, witnessStart, witnessDrive);
#if WATER_PRISM_CENSUS
        TileWaterCensus.IntersectionResult?.Invoke(result);
#endif
        return result;
    }

    internal bool CanRetain => _header.HasControlToken;
    internal int StorageBytes => Unsafe.SizeOf<Header>() +
        _states.Length * Unsafe.SizeOf<BasisState>() + _clips.Length * Unsafe.SizeOf<ClipTerm>();

    internal void CopyTo(Span<byte> target)
    {
        int headerBytes = Unsafe.SizeOf<Header>();
        MemoryMarshal.Write(target, in _header);
        Span<byte> states = MemoryMarshal.AsBytes(_states);
        states.CopyTo(target[headerBytes..]);
        MemoryMarshal.AsBytes(_clips).CopyTo(target[(headerBytes + states.Length)..]);
    }

    internal static TileWaterPreparedQuery Restore(TileWaterConvexShape shape, Span<byte> source)
    {
        Header header = MemoryMarshal.Read<Header>(source);
        int headerBytes = Unsafe.SizeOf<Header>();
        int stateBytes = shape.Bases.Length * Unsafe.SizeOf<BasisState>();
        int clipBytes = shape.Bases.Length * (shape.Planes.Length + 3) * Unsafe.SizeOf<ClipTerm>();
        return new(shape, header,
            MemoryMarshal.Cast<byte, BasisState>(source.Slice(headerBytes, stateBytes)),
            MemoryMarshal.Cast<byte, ClipTerm>(source.Slice(headerBytes + stateBytes, clipBytes)));
    }

    TileWaterPreparedQuery(TileWaterConvexShape shape, Header header, Span<BasisState> states, Span<ClipTerm> clips)
    {
        _shape = shape;
        _header = header;
        _states = states;
        _clips = clips;
    }

    bool ActiveClip(int ordinal, int i, ref BasisState state, ref double enter, ref double exit)
    {
        ref ClipTerm term = ref _clips[ordinal * Stride + i];
        if ((state.ClipMask & (1 << i)) == 0)
        {
            Fill(ref term, I.Point(0) - state.Lambda[i], I.Point(0) - state.Velocity[i]);
            state.ClipMask |= (ushort)(1 << i);
        }
        return term.Apply(ref enter, ref exit);
    }

    bool PlaneClip(int ordinal, int i, ref BasisState state, ref double enter, ref double exit)
    {
        int slot = i + 3;
        ref ClipTerm term = ref _clips[ordinal * Stride + slot];
        if ((state.ClipMask & (1 << slot)) == 0)
        {
            var plane = _shape.Planes[i];
            Fill(ref term, V.Dot(plane.Normal, state.Q) - plane.Expanded(_header.Core, _header.HeightError, _header.HorizontalError),
                V.Dot(plane.Normal, state.V));
            state.ClipMask |= (ushort)(1 << slot);
        }
        return term.Apply(ref enter, ref exit);
    }

    static void Fill(ref ClipTerm term, I constant, I slope)
    {
        term.Constant = constant;
        term.Slope = slope;
        if (slope.Low != 0) term.Crossing = I.Point(-constant.Low) / I.Point(slope.Low);
        Filled();
    }

    static void Filled()
    {
        if (TileWaterQueryWork.Enabled) TileWaterQueryWork.LazyCoefficientFills++;
    }
}

/// <summary>One reusable candidate workspace. No candidate receives a maximum-size retained allocation.</summary>
internal sealed class TileWaterPreparationWorkspace
{
    readonly TileWaterPreparedQuery.BasisState[] _states = new TileWaterPreparedQuery.BasisState[TileWaterPreparedQuery.MaxBases];
    readonly TileWaterPreparedQuery.ClipTerm[] _clips = new TileWaterPreparedQuery.ClipTerm[
        TileWaterPreparedQuery.MaxBases * (TileWaterPreparedQuery.MaxPlanes + 3)];

    internal int PayloadBytes => _states.Length * Unsafe.SizeOf<TileWaterPreparedQuery.BasisState>() +
        _clips.Length * Unsafe.SizeOf<TileWaterPreparedQuery.ClipTerm>();

    internal TileWaterPreparedQuery Prepare(TileWaterConvexShape shape, V start, V drive, I core, float radius,
        double heightError = 0, double horizontalError = 0) =>
        new(shape, _states, _clips, start, drive, core, radius, heightError, horizontalError);
}
