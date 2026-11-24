using System;
using System.Numerics;
using static KhaozEngine.Attachments.ChainCapsuleCollision;

namespace KhaozEngine.Attachments;

// Scratch is reused across the rows of one bound group. Large hand-built rigs
// keep the same solve through temporary entries when dense storage is unavailable.
internal sealed class ChainSheetWorkspace
{
    private readonly int _obstacles;
    private readonly int[] _indices;
    private readonly Candidate[] _candidates;
    private long _generation;

    internal ChainSheetWorkspace(int bands, int obstacles)
    {
        _obstacles = obstacles;
        Rows = new Row[bands];
        long slots = (long)bands * obstacles;
        if (slots <= Array.MaxLength)
        {
            try
            {
                _indices = new int[(int)slots];
                _candidates = new Candidate[(int)slots];
                return;
            }
            catch (OutOfMemoryException)
            {
                // Filtering and contact still run when the optional cache is too large.
            }
        }

        _indices = [];
        _candidates = [];
    }

    internal Row[] Rows { get; }
    internal bool CanStoreCandidates => _indices.Length != 0;

    internal void BeginDepth()
    {
        if (unchecked(++_generation) == 0)
        {
            Array.Clear(_candidates);
            _generation = 1;
        }
    }

    internal Span<int> Indices(int band) => _indices.AsSpan(band * _obstacles, _obstacles);

    internal ref Candidate ForObstacle(int band, int obstacle, ref Candidate temporary)
    {
        if (_candidates.Length == 0)
        {
            temporary = default;
            return ref temporary;
        }

        ref var entry = ref _candidates[band * _obstacles + obstacle];
        if (entry.Generation != _generation)
        {
            entry = default;
            entry.Generation = _generation;
        }
        return ref entry;
    }

    internal struct Row(Corners opening, Corners current, float radius, ChainPlacementCull.SweptSphere reach)
    {
        internal readonly Corners Opening = opening;
        private readonly ChainContactTriangle _openingFirst = opening.Triangle(false);
        private readonly ChainContactTriangle _openingSecond = opening.Triangle(true);
        internal readonly float Radius = radius;
        internal readonly ChainPlacementCull.SweptSphere Reach = reach;

        internal Corners Current = current;
        internal int Revision = 1;
        internal int CandidateCount;
        internal int LastNoOpRevision;

        private TightBound _firstPosition, _secondPosition, _firstVelocity, _secondVelocity;

        internal static ChainPlacementCull.SweptSphere TriangleReach(ref Row row, bool second, bool velocity)
        {
            ref var position = ref (second ? ref row._secondPosition : ref row._firstPosition);
            ref var current = ref (second ? ref row._secondVelocity : ref row._firstVelocity);
            ref var cached = ref (velocity ? ref current : ref position);
            if (cached.Revision != row.Revision)
            {
                cached.Sphere = ChainPlacementCull.TriangleReach(row.Current.Points(second), row.Opening.Points(second),
                    row.Radius + (float)TouchTolerance, includeOpening: !velocity);
                cached.Revision = row.Revision;
            }

            return cached.Sphere;
        }

        internal readonly ChainContactTriangle OpeningTriangle(bool second) => second ? _openingSecond : _openingFirst;

        internal void Refresh(Corners corners)
        {
            if (Current.SameLowerCorners(corners))
                return;

            Current = corners;
            Revision++;
        }
    }

    private struct TightBound
    {
        internal int Revision;
        internal ChainPlacementCull.SweptSphere Sphere;
    }

    internal readonly record struct Corners(Vector3 FirstUpper, Vector3 SecondUpper, Vector3 FirstLower, Vector3 SecondLower)
    {
        internal ChainContactTriangle Triangle(bool second) => second
            ? new(FirstUpper, SecondLower, SecondUpper)
            : new(FirstUpper, FirstLower, SecondLower);

        internal bool SameLowerCorners(Corners other) =>
            SameBits(FirstLower, other.FirstLower) && SameBits(SecondLower, other.SecondLower);

        internal (Vector3 A, Vector3 B, Vector3 C) Points(bool second) =>
            second ? (FirstUpper, SecondLower, SecondUpper) : (FirstUpper, FirstLower, SecondLower);

        private static bool SameBits(Vector3 a, Vector3 b) =>
            BitConverter.SingleToInt32Bits(a.X) == BitConverter.SingleToInt32Bits(b.X) &&
            BitConverter.SingleToInt32Bits(a.Y) == BitConverter.SingleToInt32Bits(b.Y) &&
            BitConverter.SingleToInt32Bits(a.Z) == BitConverter.SingleToInt32Bits(b.Z);
    }

    internal struct Candidate
    {
        internal long Generation;
        internal TriangleState First;
        internal TriangleState Second;
        internal ChainContactTriangle.Diagonal CurrentDiagonal;
        internal ChainContactTriangle.Diagonal OpeningDiagonal;

        internal static ref TriangleState Triangle(ref Candidate candidate, bool second) =>
            ref (second ? ref candidate.Second : ref candidate.First);
    }

    internal struct TriangleState
    {
        internal Sample? Opening;
        internal int Revision;
        internal Sample Current;
        internal double? FixedDeficit;
    }

    internal readonly record struct Sample(ChainContactTriangle.Contact Contact, Offset Normal, double Side);
}
