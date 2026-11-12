#if WATER_PRISM_CENSUS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld.Physics;
using I = KhaozEngine.TileWorld.Physics.TileWaterArithmetic;
using V = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Vector;

namespace KhaozEngine.Tests.WaterPrismCensus;

// Input hashes and counters are research data. No cache or alternative solve is installed.
internal sealed class WaterPrismMeasurement : IDisposable
{
    [ThreadStatic] internal static List<TraceRow>? Rows;
    [ThreadStatic] internal static string? Stage;
    [ThreadStatic] internal static int Tick;
    [ThreadStatic] static Dictionary<TileWaterConvexShape, int>? t_shapes;
    readonly TraceRow _row;
    readonly MovementMediumSweepQuery _query;
    readonly Vector3 _origin;
    readonly Dictionary<TileWaterConvexShape, bool> _rejected = [];

    internal sealed class TraceRow
    {
        public string Stage { get; set; } = "";
        public int Tick { get; set; }
        public string Caller { get; set; } = "";
        public string Path { get; set; } = "";
        public string QueryHash { get; set; } = "";
        public SortedDictionary<string, int> Work { get; } = new(StringComparer.Ordinal);
        public List<string> Inputs { get; } = [];
        public int RejectedPrisms { get; set; }
        public int RejectedFaces { get; set; }
        public int RejectedIntersections { get; set; }
        public int RejectedBases { get; set; }
        public int RejectedDots { get; set; }
        public int Spans { get; set; }
        public int Contacts { get; set; }
        public string Availability { get; set; } = "";
        public bool FullyDry { get; set; }
        public bool FullySubmerged { get; set; }
        public bool PrismKernel => Work.GetValueOrDefault("prism-traces") > 0;
    }

    bool _rejecting;
    bool _resultSeen;
    WaterPrismMeasurement(MovementMediumSweepQuery query, Vector3 origin)
    {
        _query = query;
        _origin = origin;
        string[] stack = new StackTrace(false).GetFrames().Select(f => f.GetMethod()!)
            .Where(m => m.DeclaringType?.Namespace?.StartsWith("KhaozEngine.Locomotion", StringComparison.Ordinal) == true)
            .Select(m => m.DeclaringType!.Name + "." + m.Name).ToArray();
        _row = new() { Stage = Stage!, Tick = Tick, Path = string.Join(" > ", stack),
            Caller = stack.FirstOrDefault(s => s.StartsWith("MovementWaterBoundary.", StringComparison.Ordinal) ||
                s.StartsWith("LeasedContactQueryContext.TraceMedium", StringComparison.Ordinal) ||
                s.StartsWith("MovementWaterPathProof.", StringComparison.Ordinal)) ?? string.Join(" > ", stack) };
        _row.QueryHash = Hash(writer =>
        {
            Write(writer, V.Point(query.Body.Centre.X, query.Body.Centre.Y, query.Body.Centre.Z));
            Write(writer, V.Point(query.Delta.X, query.Delta.Y, query.Delta.Z));
            writer.Write(query.Body.Radius); writer.Write(query.Body.HalfHeight);
            writer.Write(origin.X); writer.Write(origin.Y); writer.Write(origin.Z);
            writer.Write(query.Body.CurrentSpace.WorldId); writer.Write(query.Body.CurrentSpace.LocalId);
        });
        TileWaterCensus.Phase = "none";
        TileWaterCensus.Count = Add;
        TileWaterCensus.Intersection = Intersect;
        TileWaterCensus.IntersectionResult = range =>
        {
            _resultSeen = true;
            if (_rejecting && (!range.Resolved || range.Hit))
                throw new InvalidOperationException("A counted bound rejection must match a resolved exact miss.");
        };
        TileWaterCensus.IntersectionEnded = () =>
        {
            if (_rejecting && !_resultSeen)
                throw new InvalidOperationException("A counted bound rejection cannot hide an exact refusal.");
            _rejecting = false;
        };
        TileWaterCensus.Selected = Select;
        TileWaterQueryWork.Reset();
    }

    internal static void Reset(List<TraceRow> rows)
    {
        Rows = rows;
        t_shapes = [];
    }

    internal static WaterPrismMeasurement? Begin(MovementMediumSweepQuery query, Vector3 origin) =>
        Rows is null ? null : new(query, origin);

    void Add(string name)
    {
        _row.Work[name] = _row.Work.GetValueOrDefault(name) + 1;
        if (_rejecting && name == "bases") _row.RejectedBases++;
        if (_rejecting && name is "projection-dots" or "other-dots") _row.RejectedDots++;
    }

    void Select(TileWaterPrism piece, TileWaterConvexShape shape, int face)
    {
        Add(face < 0 ? "prisms-selected" : "faces-tested");
        bool rejected = WaterPrismBounds.Reject(piece, face, _query, _origin);
        _rejected[shape] = rejected;
        if (rejected)
        {
            if (face < 0) _row.RejectedPrisms++;
            else _row.RejectedFaces++;
        }
    }

    void Intersect(TileWaterConvexShape shape, V start, V drive, I core, float radius,
        double enter, double exit, double heightError, double horizontalError)
    {
        Add("intersection/" + TileWaterCensus.Phase);
        _rejecting = _rejected.GetValueOrDefault(shape);
        _resultSeen = false;
        if (_rejecting) _row.RejectedIntersections++;
        if (!t_shapes!.TryGetValue(shape, out int id)) t_shapes.Add(shape, id = t_shapes.Count);
        _row.Inputs.Add(Hash(writer =>
        {
            // Shape identity includes immutable planes, seam certificate and captured generation.
            writer.Write(id); Write(writer, start); Write(writer, drive); Write(writer, core);
            writer.Write(radius); writer.Write(enter); writer.Write(exit);
            writer.Write(heightError); writer.Write(horizontalError);
        }));
    }

    internal void Complete(MovementCoverageResult result)
    {
        _rejecting = false;
        _row.Spans = result.SpansWritten;
        _row.Contacts = result.ContactsWritten;
        _row.Availability = result.Availability.ToString();
        _row.Work["shape-preparations"] = TileWaterQueryWork.ShapePreparations;
        _row.Work["lazy-coefficient-fills"] = TileWaterQueryWork.LazyCoefficientFills;
        _row.Work["prism-traces"] = TileWaterQueryWork.PrismTraces;
        _row.Work["memo-hits"] = TileWaterQueryWork.MemoHits;
        _row.Work["memo-misses"] = TileWaterQueryWork.MemoMisses;
        _row.Work["convex-intersections"] = TileWaterQueryWork.ConvexIntersections;
        _row.Work["bed-seam-proofs"] = TileWaterQueryWork.BedSeamProofs;
        _row.Work["bed-seam-certificates"] = TileWaterQueryWork.BedSeamCertificates;
        _row.Work["index-nodes"] = TileWaterQueryWork.IndexNodes;
        if (Geometry is { } geometry)
            (_row.FullyDry, _row.FullySubmerged) = WaterPrismBounds.Classify(geometry, _query, _origin);
        Rows!.Add(_row);
    }

    [ThreadStatic] internal static TileWaterTraceTestSnapshot? Geometry;
    public void Dispose()
    {
        TileWaterCensus.Count = null;
        TileWaterCensus.Intersection = null;
        TileWaterCensus.IntersectionEnded = null;
        TileWaterCensus.IntersectionResult = null;
        TileWaterCensus.Selected = null;
        TileWaterCensus.Projecting = false;
        TileWaterQueryWork.Enabled = false;
    }

    static string Hash(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        write(writer);
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length)));
    }
    static void Write(BinaryWriter writer, V vector) { Write(writer, vector.X); Write(writer, vector.Y); Write(writer, vector.Z); }
    static void Write(BinaryWriter writer, I value) { writer.Write(value.Low); writer.Write(value.High); }
}
#endif
