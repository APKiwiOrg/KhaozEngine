#if CONTACT_SHORE_ATTRIBUTION
using System;
using System.Collections.Generic;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using H = KhaozEngine.TileWorld.Physics.TileWaterPreparedQuery.Header;
using I = KhaozEngine.TileWorld.Physics.TileWaterArithmetic;
using P = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Plane;
using V = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Vector;

namespace KhaozEngine.TileWorld.Physics;

internal enum ShoreWork
{
    CollectNodes, Columns, PieceSelection, FaceSelection, PieceAssembly, FaceAssembly, Append,
    PreparedEvaluations, DirectEvaluations, Projection, CopiedProjection, BasisWindowSkip,
    GeometryHit, GeometryMiss, Expansion, StartDot, DriveDot, TripleCentre, TripleVelocity,
    ActiveFill, PlaneFill, PostClipFill, VertexFill, RootFill, ClipApply, ParentClipNonzero, ParentClipZero,
    RetainedBytes, SmallStore, ResultStore, SmallEvict, ResultEvict, SmallReject, ResultReject
}

internal static class TileWaterShoreCounts
{
    internal sealed record Trace(string Label, int Key, int Pose, int Drive, int RadiusHeight,
        int SpansCapacity, int ContactsCapacity, bool Prism, string Outcome, int Spans, int Contacts,
        int DispatchNodes, int Nonuniform, int Convex, int Preparations, int Fills, int[] Work, int[] RepeatsInTrace,
        int[] RepeatsInStep, int[] RepeatsInCapture);

    [ThreadStatic] internal static TileWaterShoreCounts.Session? Current;

    internal static void Count(ShoreWork kind, int amount = 1)
    {
        if (Current is { Active: true } s) s.Work[(int)kind] += amount;
    }

    internal static void Outcome(string outcome)
    {
        if (Current is { Active: true } s) s.Outcome = outcome;
    }

    internal static void EndTrace(in TileWaterTraceMemo.Key key, bool controlled, bool prism,
        in MovementCoverageResult result)
    {
        if (Current is { Active: true } s) s.EndTrace(key, controlled, prism, result);
    }

    internal readonly record struct IntervalBits(long Low, long High)
    {
        internal IntervalBits(I x) : this(BitConverter.DoubleToInt64Bits(x.Low), BitConverter.DoubleToInt64Bits(x.High)) { }
    }
    internal readonly record struct VectorBits(IntervalBits X, IntervalBits Y, IntervalBits Z)
    {
        internal VectorBits(V x) : this(new(x.X), new(x.Y), new(x.Z)) { }
    }

    internal static void ObserveShape(TileWaterConvexShape shape, in H head)
    {
        if (Current is not { Active: true } s || !head.HasControlToken) return;
        s.ShapeUses.Observe((shape, new VectorBits(head.Start), new VectorBits(head.Drive),
            new IntervalBits(head.Core), BitConverter.SingleToInt32Bits(head.Radius),
            BitConverter.DoubleToInt64Bits(head.HeightError), BitConverter.DoubleToInt64Bits(head.HorizontalError),
            head.ControlToken), s, Shape);
        s.Shapes.Add(shape);
    }

    internal static void ObserveExpansion(P plane, in H head)
    {
        Count(ShoreWork.Expansion);
        if (Current is not { Active: true } s || !head.HasControlToken) return;
        s.Expansions.Observe((new VectorBits(plane.Normal), new IntervalBits(plane.Offset), plane.HeightSensitive,
            new IntervalBits(head.Core), BitConverter.DoubleToInt64Bits(plane.HeightSensitive ? head.HeightError : 0),
            BitConverter.DoubleToInt64Bits(head.HorizontalError), head.ControlToken), s, Expanded);
    }

    internal static void ObserveDot(V normal, in H head, bool drive)
    {
        Count(drive ? ShoreWork.DriveDot : ShoreWork.StartDot);
        if (Current is not { Active: true } s || !head.HasControlToken) return;
        var key = (new VectorBits(normal), new VectorBits(drive ? head.Drive : head.Start), head.ControlToken);
        if (drive) s.DriveDots.Observe(key, s, DriveDot);
        else s.StartDots.Observe(key, s, StartDot);
    }

    internal static void ObserveTriple(TileWaterConvexShape shape, int ordinal, in H head)
    {
        Count(ShoreWork.TripleCentre);
        Count(ShoreWork.TripleVelocity);
        if (Current is not { Active: true } s || !head.HasControlToken) return;
        s.Centres.Observe((shape, ordinal, new IntervalBits(head.Core), BitConverter.DoubleToInt64Bits(head.HeightError),
            BitConverter.DoubleToInt64Bits(head.HorizontalError), head.ControlToken), s, TripleCentre);
        s.Velocities.Observe((shape, ordinal, new VectorBits(head.Drive), head.ControlToken), s, TripleVelocity);
    }

    internal static void ObserveClip(TileWaterConvexShape shape, int ordinal, int slot, bool nonzero, bool borrowed)
    {
        if (Current is not { Active: true } s) return;
        if (shape.ProjectionParent is not { } parent)
        {
            s.ParentClips[(shape, ordinal, slot)] = nonzero;
            return;
        }
        if (!borrowed || slot >= parent.Planes.Length + 3) return;
        int mapped = shape.ProjectionBasisMap[ordinal];
        if (mapped < 0) return;
        if (s.ParentClips.TryGetValue((parent, mapped, slot), out bool crossing))
            Count(crossing ? ShoreWork.ParentClipNonzero : ShoreWork.ParentClipZero);
    }

    const int Shape = 0, Expanded = 1, StartDot = 2, DriveDot = 3, TripleCentre = 4, TripleVelocity = 5, Repeats = 6;
    internal static readonly string[] RepeatNames = ["shape", "expansion", "startDot", "driveDot", "tripleCentre", "tripleVelocity"];

    internal sealed class Session
    {
        readonly Dictionary<TileWaterTraceMemo.Key, int> _keys = new();
        readonly Dictionary<(int, int, int), int> _poses = new(), _drives = new();
        readonly Dictionary<(int, int), int> _sizes = new();
        readonly List<Trace> _traces = new();
        int _nodes, _nonuniform, _convex, _preparations, _fills, _stamp;

        internal readonly int[] Work = new int[Enum.GetValues<ShoreWork>().Length];
        internal readonly int[] InTrace = new int[Repeats], InStep = new int[Repeats], InCapture = new int[Repeats];
        internal readonly Dictionary<(TileWaterConvexShape, int, int), bool> ParentClips = new();
        internal readonly HashSet<TileWaterConvexShape> Shapes = new();
        internal readonly Seen<(TileWaterConvexShape, VectorBits, VectorBits, IntervalBits, int, long, long, ulong)> ShapeUses = new();
        internal readonly Seen<(VectorBits, IntervalBits, bool, IntervalBits, long, long, ulong)> Expansions = new();
        internal readonly Seen<(VectorBits, VectorBits, ulong)> StartDots = new(), DriveDots = new();
        internal readonly Seen<(TileWaterConvexShape, int, IntervalBits, long, long, ulong)> Centres = new();
        internal readonly Seen<(TileWaterConvexShape, int, VectorBits, ulong)> Velocities = new();

        internal bool Active { get; private set; }
        internal int Step { get; private set; }
        internal string Outcome = "unclassified";
        internal int UniqueShapes => Shapes.Count;

        internal void Start()
        {
            Active = true;
            Step++;
            _traces.Clear();
            _nodes = _nonuniform = _convex = _preparations = _fills = 0;
        }

        internal Trace[] Stop()
        {
            Active = false;
            return _traces.ToArray();
        }

        internal void EndTrace(in TileWaterTraceMemo.Key key, bool controlled, bool prism,
            in MovementCoverageResult result)
        {
            _traces.Add(new(ShoreStepProbe.TraceLabel ?? "other", controlled ? Intern(_keys, key) : 0,
                Intern(_poses, (key.X, key.Y, key.Z)), Intern(_drives, (key.Dx, key.Dy, key.Dz)),
                Intern(_sizes, (key.Radius, key.HalfHeight)), key.Spans, key.Contacts,
                prism, Outcome, result.SpansWritten, result.ContactsWritten, TileWaterQueryWork.IndexNodes - _nodes,
                TileWaterQueryWork.NonuniformVisits - _nonuniform, TileWaterQueryWork.ConvexIntersections - _convex,
                TileWaterQueryWork.ShapePreparations - _preparations, TileWaterQueryWork.LazyCoefficientFills - _fills,
                (int[])Work.Clone(), (int[])InTrace.Clone(), (int[])InStep.Clone(), (int[])InCapture.Clone()));
            _nodes = TileWaterQueryWork.IndexNodes;
            _nonuniform = TileWaterQueryWork.NonuniformVisits;
            _convex = TileWaterQueryWork.ConvexIntersections;
            _preparations = TileWaterQueryWork.ShapePreparations;
            _fills = TileWaterQueryWork.LazyCoefficientFills;
            _stamp++;
            Outcome = "unclassified";
            Array.Clear(Work);
            Array.Clear(InTrace);
            Array.Clear(InStep);
            Array.Clear(InCapture);
            ParentClips.Clear();
        }

        internal int Stamp => _stamp;

        static int Intern<T>(Dictionary<T, int> keys, T key) where T : notnull
        {
            if (!keys.TryGetValue(key, out int id)) keys.Add(key, id = keys.Count + 1);
            return id;
        }

        internal static object MemoMemory() => new
        {
            Small = new TileWaterTraceMemo.Table().OwnedBytes,
            Result = new TileWaterTraceResultMemo.Table().OwnedBytes,
            TileWaterTraceResultMemo.IdentifierByteLimit,
            GeometryValue = System.Runtime.CompilerServices.Unsafe.SizeOf<TileWaterGeometryTerms.Value>()
        };

    }

    internal sealed class Seen<T> where T : notnull
    {
        readonly Dictionary<T, (int Step, int Trace)> _seen = new();
        internal void Observe(T key, Session s, int index)
        {
            if (_seen.TryGetValue(key, out var previous))
            {
                s.InCapture[index]++;
                if (previous.Trace == s.Stamp) s.InTrace[index]++;
                if (previous.Step == s.Step) s.InStep[index]++;
            }
            _seen[key] = (s.Step, s.Stamp);
        }
    }
}
#endif
