using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Fixtures;

// Test-only single sloped boundary. Every other finite boundary must contain the entire sweep.
// No polygon clipping, native lookup or approximation of simultaneous boundary participation.
internal sealed class AnalyticSlopedMovementEnvironment : IDisposable
{
    const float Skin = 0.001f;
    public const float Error = 0.00001f;
    readonly bool _ceiling;
    readonly float _slope;
    readonly float _height;
    readonly double _length;
    readonly IPhysicsWorldQueryView _view;
    public BepuPhysicsWorld Physics { get; } = new(Vector3.Zero);
    public MovementQueryLease Lease { get; }
    public bool MissingDependency;
    public Vector3 WetNormal { get; }
    public static readonly MovementSpaceKey Room = new("world", "room");
    public static readonly MovementDomainKey Water = new("world", "water");
    public static readonly MovementQueryIdentity Identity = new("closure", 1u, "scope");

    public AnalyticSlopedMovementEnvironment(bool ceiling, float slope = 0.5f, float height = 0f)
    {
        if (!float.IsFinite(slope) || MathF.Abs(slope) > 0.5f || !float.IsFinite(height) || MathF.Abs(height) > 1f)
            throw new ArgumentOutOfRangeException(nameof(slope));
        _ceiling = ceiling;
        _slope = slope;
        _height = height;
        _length = Math.Sqrt(1d + (double)slope * slope);
        float sign = ceiling ? -1f : 1f;
        WetNormal = new Vector3((float)(-sign * slope / _length), (float)(sign / _length), 0f);
        Vector3 upward = Vector3.Normalize(new Vector3(-slope, 1f, 0f));
        Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.Atan(slope));
        Vector3 centre = Vector3.UnitY * height + upward * (ceiling ? 0.1f : -0.1f);
        Physics.AddStatic(new BoxShape(new Vector3(10f, 0.1f, 10f)), new Pose(centre, rotation));
        _view = Physics.CreateQueryViewExcludingStatics([]);
        var frame = new MovementFrameDescriptor(WorldFrame.Origin, Vector3.Zero, 1ul);
        var scope = new MovementQueryScope(new Vector3(-8f), new Vector3(8f), 0f, 0f, Room, Identity, frame);
        var environment = new EnvironmentAcquisitionFixture(_view, scope)
        {
            Resources = ["sloped-face", "finite-enclosure"],
            OnBodySample = Sample,
            OnCoverage = Trace
        };
        var acquired = environment.Acquire();
        Assert.Equal(MovementAvailability.Known, acquired.Status);
        Lease = Assert.IsType<MovementQueryLease>(acquired.Lease);
    }

    MovementWaterPoint Sample(in MovementBodyQuery body)
    {
        if (MissingDependency || body.CurrentSpace != Room || MathF.Abs(body.Feet.X) >= 6f || MathF.Abs(body.Feet.Z) >= 6f)
            return UnknownPoint();
        MovementWaterInterval interval = Column(body.Feet.X);
        if (body.Feet.Y < interval.LowerY || _ceiling && body.Feet.Y >= interval.UpperY)
            return UnknownPoint();
        bool wet = body.Feet.Y < interval.UpperY;
        return new(MovementAvailability.Known, Room, wet ? Water : null, wet, 1f, wet ? interval : null);
    }

    MovementCoverageResult Trace(in MovementMediumSweepQuery query, Span<MovementCoverageSpan> spans,
        Span<MovementDomainContact> contacts)
    {
        if (MissingDependency || !WithinFiniteCertificate(query)) return Refused();
        Vector3 bodyCentre = query.Body.Centre, delta = query.Delta;
        double sign = _ceiling ? -1d : 1d;
        double distance = sign * (query.Body.Centre.Y - (double)_slope * query.Body.Centre.X - _height) / _length;
        double change = sign * (query.Delta.Y - (double)_slope * query.Delta.X) / _length;
        double core = (double)query.Body.HalfHeight - query.Body.Radius;
        double radius = query.Body.Radius + (double)Skin;
        double reach = radius + core / _length;
        var cuts = new SortedSet<float> { 0f, 1f };
        if (change != 0d)
        {
            AddCut(cuts, (-reach - distance) / change);
            AddCut(cuts, (reach - distance) / change);
        }
        var outputSpans = new List<MovementCoverageSpan>();
        var outputContacts = new List<MovementDomainContact>();
        float[] boundaries = cuts.ToArray();
        for (int i = 0; i < boundaries.Length; i++)
        {
            Append(boundaries[i], boundaries[i]);
            if (i + 1 < boundaries.Length) Append(boundaries[i], boundaries[i + 1]);
        }
        if (outputSpans.Count > Math.Min(spans.Length, MovementQueryLease.MaxCoverageSpans) ||
            outputContacts.Count > Math.Min(contacts.Length, MovementQueryLease.MaxDomainContacts))
            return new(MovementAvailability.CapacityExceeded, 0, outputSpans.Count, 0, outputContacts.Count, Error, Identity);
        outputSpans.ToArray().AsSpan().CopyTo(spans);
        outputContacts.ToArray().AsSpan().CopyTo(contacts);
        return new(MovementAvailability.Known, outputSpans.Count, outputSpans.Count,
            outputContacts.Count, outputContacts.Count, Error, Identity);

        void Append(float start, float end)
        {
            double middle = ((double)start + end) * 0.5d;
            double d = distance + middle * change;
            bool wet = d + reach >= 0d;
            bool dry = d - reach < 0d;
            int first = outputContacts.Count;
            if (wet)
            {
                // The support point furthest into the wet half-space certifies a covered column.
                // It is a local fact only. The affine plane inequality certifies the whole span.
                double x = bodyCentre.X + (double)start * delta.X - sign * _slope * radius / _length;
                double z = bodyCentre.Z + (double)start * delta.Z;
                Vector2 column = new((float)x, (float)z);
                outputContacts.Add(new MovementDomainContact(Water, Room, Column(column.X), column,
                    -WetNormal, start, _ceiling ? "sloped-ceiling" : "sloped-floor", 0u));
            }
            outputSpans.Add(new MovementCoverageSpan(start, end, first, outputContacts.Count - first, dry));
        }
    }

    bool WithinFiniteCertificate(in MovementMediumSweepQuery query)
    {
        if (query.Body.CurrentSpace != Room || query.Body.Radius != 0.25f || query.Body.HalfHeight != 0.75f ||
            !float.IsFinite(_slope) || MathF.Abs(_slope) > 0.5f || !float.IsFinite(_height) || MathF.Abs(_height) > 1f)
            return false;
        Vector3 extent = new Vector3(query.Body.Radius, query.Body.HalfHeight, query.Body.Radius) + new Vector3(Skin);
        Vector3 end = query.Body.Centre + query.Delta;
        Vector3 min = Vector3.Min(query.Body.Centre, end) - extent;
        Vector3 max = Vector3.Max(query.Body.Centre, end) + extent;
        // These constant boundaries cannot participate. Joint clipping is deliberately unsupported.
        return min.X > -6f + Error && max.X < 6f - Error && min.Z > -6f + Error && max.Z < 6f - Error &&
            min.Y > -6f + Error && max.Y < 6f - Error &&
            MathF.Abs(query.Delta.X) <= 4f && MathF.Abs(query.Delta.Y) <= 4f && MathF.Abs(query.Delta.Z) <= 4f;
    }

    MovementWaterInterval Column(float x)
    {
        float height = (float)((double)_slope * x + _height);
        return _ceiling ? new(-6f, height, 6f, false, "floor", "sloped-ceiling") :
            new(height, 6f, 6f, true, "sloped-floor", "surface");
    }

    static void AddCut(SortedSet<float> cuts, double fraction)
    {
        if (fraction > 0d && fraction < 1d) cuts.Add((float)fraction);
    }
    static MovementWaterPoint UnknownPoint() => new(MovementAvailability.Unresolved, default, null, false, 0f, null);
    static MovementCoverageResult Refused() => new(MovementAvailability.Unresolved, 0, 0, 0, 0, 0f, Identity);
    public void Dispose() { Lease.Dispose(); _view.Dispose(); Physics.Dispose(); }
}
