using System;
using System.IO;
using System.Security.Cryptography;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

// Exact public output bits and order, captured from the separately run r0 producer.
internal sealed class WaterCoverageDigest : IDisposable
{
    readonly MemoryStream _stream = new();
    readonly BinaryWriter _writer;
    internal WaterCoverageDigest() => _writer = new(_stream);

    internal void Add(MovementMediumSweepQuery query, MovementCoverageResult result,
        ReadOnlySpan<MovementCoverageSpan> spans, ReadOnlySpan<MovementDomainContact> contacts)
    {
        FloatBits(query.Body.Centre.X); FloatBits(query.Body.Centre.Y); FloatBits(query.Body.Centre.Z);
        FloatBits(query.Body.Radius); FloatBits(query.Body.HalfHeight);
        _writer.Write(query.Body.CurrentSpace.WorldId); _writer.Write(query.Body.CurrentSpace.LocalId);
        FloatBits(query.Delta.X); FloatBits(query.Delta.Y); FloatBits(query.Delta.Z);
        _writer.Write((int)result.Availability);
        _writer.Write(result.SpansWritten); _writer.Write(result.RequiredSpanCapacity);
        _writer.Write(result.ContactsWritten); _writer.Write(result.RequiredContactCapacity);
        FloatBits(result.CertifiedErrorMetres);
        _writer.Write(result.Identity.ClosureId); _writer.Write(result.Identity.PolicyVersion); _writer.Write(result.Identity.ScopeDigest);
        foreach (var span in spans)
        {
            FloatBits(span.EnterFraction); FloatBits(span.ExitFraction);
            _writer.Write(span.ContactStart); _writer.Write(span.ContactCount); _writer.Write(span.HasDryCoverage);
        }
        foreach (var contact in contacts)
        {
            _writer.Write(contact.Domain.WorldId); _writer.Write(contact.Domain.LocalId);
            _writer.Write(contact.Space.WorldId); _writer.Write(contact.Space.LocalId);
            FloatBits(contact.Interval.LowerY); FloatBits(contact.Interval.UpperY); FloatBits(contact.Interval.NominalSurfaceY);
            _writer.Write(contact.Interval.UpperIsFreeSurface);
            _writer.Write(contact.Interval.LowerBoundaryId); _writer.Write(contact.Interval.UpperBoundaryId);
            FloatBits(contact.IntervalColumnXZ.X); FloatBits(contact.IntervalColumnXZ.Y);
            FloatBits(contact.Normal.X); FloatBits(contact.Normal.Y); FloatBits(contact.Normal.Z); FloatBits(contact.Fraction);
            _writer.Write(contact.BoundaryId); _writer.Write(contact.CoverageRegionHandle);
            _writer.Write((int)contact.Overlap); _writer.Write((int)contact.SpanOverlap);
        }
    }

    void FloatBits(float value) => _writer.Write(BitConverter.SingleToInt32Bits(value));
    internal string Digest()
    {
        _writer.Flush();
        return Convert.ToHexString(SHA256.HashData(_stream.GetBuffer().AsSpan(0, (int)_stream.Length)));
    }
    public void Dispose() => _writer.Dispose();
}
