using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using KhaozEngine.Tools.Coverage;
using Xunit;

namespace KhaozEngine.Tests.Tools.Coverage;

public sealed class CoveragePackedPoseCodecTests
{
    static readonly CoverageInputLocation Location = new("$.poseChunks[2]", "synthetic.kepp", 2, 3);

    [Fact]
    public void Kepp_preserves_every_finite_word_in_every_pose_field()
    {
        byte[] bytes = CoveragePackedFixture.ChunkBytes(true);
        var header = CoveragePackedPoseCodec.ReadHeader(bytes.AsSpan(0, 32), bytes.Length, Location);
        Assert.Equal(new CoveragePackedHeader(2, 1, 1, 336), header);
        byte[] encodedHeader = new byte[32];
        CoveragePackedPoseCodec.WriteHeader(encodedHeader, header);
        Assert.Equal(Convert.FromHexString(CoveragePackedFixture.HeaderHex), encodedHeader);
        Assert.Equal(8, CoveragePackedFixture.FiniteWords.Length);
        for (int wordIndex = 0; wordIndex < CoveragePackedFixture.FiniteWords.Length; wordIndex++)
        {
            uint word = CoveragePackedFixture.FiniteWords[wordIndex];
            var expected = new CoverageNumericPose(CoveragePackedFixture.Matrix([word]),
                [CoveragePackedFixture.Matrix([word]), CoveragePackedFixture.Matrix([word])],
                [new(23, 17, CoveragePackedFixture.Matrix([word]), CoveragePackedFixture.Matrix([word]), BitConverter.UInt32BitsToSingle(word))]);
            byte[] record = CoveragePackedFixture.RecordBytes(expected);
            CoveragePackedPoseCodec.ValidateRecord(record, header, Location);
            var decoded = CoveragePackedPoseCodec.ReadRecord(record, header, Location);
            Assert.Equal(record, CoveragePackedFixture.RecordBytes(decoded));
            Assert.Equal(23u, decoded.Commands[0].Id);
            Assert.Equal(17ul, decoded.Commands[0].EquipmentInstanceId);
            byte[] rewritten = new byte[336];
            CoveragePackedPoseCodec.WriteRecord(rewritten, header, decoded);
            Assert.Equal(record, rewritten);
        }
        var vector = CoveragePackedPoseCodec.ReadRecord(bytes.AsSpan(32), header, Location);
        Assert.Equal(bytes[32..], CoveragePackedFixture.RecordBytes(vector));
        byte[] rewrittenVector = new byte[336];
        CoveragePackedPoseCodec.WriteRecord(rewrittenVector, header, vector);
        Assert.Equal(bytes[32..], rewrittenVector);
        byte[] control = CoveragePackedFixture.ChunkBytes();
        Assert.Equal("79e5da6535a760e62db55573fbb996c67a1b4b6edcd1649eafb751f3a0ad42a5", Convert.ToHexStringLower(SHA256.HashData(control)));
    }

    public static IEnumerable<object[]> LayoutDefects()
    {
        yield return [0, 0u, 368, CoverageBuildInputRefusal.PackedHeaderInvalid, null!, null!];
        yield return [4, 2u, 368, CoverageBuildInputRefusal.UnsupportedVersion, null!, null!];
        yield return [4, uint.MaxValue, 368, CoverageBuildInputRefusal.UnsupportedVersion, null!, null!];
        yield return [8, 31u, 368, CoverageBuildInputRefusal.PackedHeaderInvalid, null!, null!];
        yield return [12, 0x04030201u, 368, CoverageBuildInputRefusal.PackedHeaderInvalid, null!, null!];
        yield return [16, 0u, 368, CoverageBuildInputRefusal.PackedHeaderInvalid, null!, null!];
        yield return [16, 1025u, 368, CoverageBuildInputRefusal.CapacityExceeded, 1025L, 1024L];
        yield return [16, uint.MaxValue, 368, CoverageBuildInputRefusal.CapacityExceeded, (long)uint.MaxValue, 1024L];
        yield return [20, 0u, 368, CoverageBuildInputRefusal.PackedHeaderInvalid, null!, null!];
        yield return [20, 5u, 368, CoverageBuildInputRefusal.CapacityExceeded, 5L, 4L];
        yield return [20, uint.MaxValue, 368, CoverageBuildInputRefusal.CapacityExceeded, (long)uint.MaxValue, 4L];
        yield return [24, 0u, 368, CoverageBuildInputRefusal.PackedCountMismatch, null!, null!];
        yield return [24, uint.MaxValue, 368, CoverageBuildInputRefusal.CapacityExceeded, 32L + uint.MaxValue * 336L, 8388608L];
        yield return [24, 24967u, 368, CoverageBuildInputRefusal.CapacityExceeded, 8388944L, 8388608L];
        yield return [28, 335u, 368, CoverageBuildInputRefusal.PackedHeaderInvalid, null!, null!];
        yield return [-1, 0u, 367, CoverageBuildInputRefusal.PackedChunkLengthMismatch, null!, null!];
        yield return [-1, 0u, 369, CoverageBuildInputRefusal.PackedChunkLengthMismatch, null!, null!];
    }

    [Theory]
    [MemberData(nameof(LayoutDefects))]
    public void Kepp_refuses_one_layout_defect_with_a_valid_control(int offset, uint value, int length,
        CoverageBuildInputRefusal reason, long? count, long? limit)
    {
        byte[] bytes = CoveragePackedFixture.ChunkBytes();
        if (offset >= 0) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
        var error = Assert.Throws<CoverageBuildDataException>(() => CoveragePackedPoseCodec.ReadHeader(bytes.AsSpan(0, 32), length, Location));
        Assert.Equal(reason, error.Reason);
        AssertLocation(error, offset < 0 ? 24 : offset);
        if (count.HasValue) Assert.Equal(count, error.Count);
        if (limit.HasValue) Assert.Equal(limit, error.Limit);
        if (reason == CoverageBuildInputRefusal.UnsupportedVersion) Assert.Equal((long)value, error.FormatVersion);
        if (offset < 0) { Assert.Equal(368L, error.ExpectedLength); Assert.Equal(length, error.ActualLength); }
        DecodeControl();
    }

    public static IEnumerable<object[]> NonfiniteWords() =>
        from offset in new[] { 0, 60, 64, 188, 204, 264, 268, 328, 332 }
        from word in new[] { 0x7f800000u, 0xff800000u, 0x7fc00001u, 0x7f800001u, 0xffc00001u }
        select new object[] { offset, word };

    [Theory]
    [MemberData(nameof(NonfiniteWords))]
    public void Kepp_nonfinite_word_refuses_without_inspecting_later_fields(int offset, uint word)
    {
        byte[] bytes = CoveragePackedFixture.ChunkBytes();
        var header = CoveragePackedPoseCodec.ReadHeader(bytes.AsSpan(0, 32), bytes.Length, Location);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32 + offset, 4), word);
        // A second bad word proves file order chooses the first numeric refusal.
        if (offset < 332) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(364, 4), 0x7f800000);
        var error = Assert.Throws<CoverageBuildDataException>(() => CoveragePackedPoseCodec.ValidateRecord(bytes.AsSpan(32), header, Location));
        Assert.Equal(CoverageBuildInputRefusal.PackedWordNonFinite, error.Reason);
        AssertLocation(error, 32L + 3 * 336 + offset);
        var decodeError = Assert.Throws<CoverageBuildDataException>(() => CoveragePackedPoseCodec.ReadRecord(bytes.AsSpan(32), header, Location));
        Assert.Equal(error.ByteOffset, decodeError.ByteOffset);
        DecodeControl();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void Kepp_header_requires_an_exact_span(int length)
    {
        byte[] bytes = new byte[length];
        var error = Assert.Throws<CoverageBuildDataException>(() => CoveragePackedPoseCodec.ReadHeader(bytes, 368, Location));
        Assert.Equal(CoverageBuildInputRefusal.PackedHeaderInvalid, error.Reason);
        Assert.Equal(32L, error.ExpectedLength);
        Assert.Equal(length, error.ActualLength);
        AssertLocation(error, 0);
        DecodeControl();
    }

    [Theory]
    [InlineData(335)]
    [InlineData(337)]
    public void Kepp_record_requires_an_exact_span(int length)
    {
        var header = new CoveragePackedHeader(2, 1, 1, 336);
        var error = Assert.Throws<CoverageBuildDataException>(() => CoveragePackedPoseCodec.ReadRecord(new byte[length], header, Location));
        Assert.Equal(CoverageBuildInputRefusal.PackedChunkLengthMismatch, error.Reason);
        Assert.Equal(336L, error.ExpectedLength);
        Assert.Equal(length, error.ActualLength);
        AssertLocation(error, 32L + 3 * 336);
        DecodeControl();
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1024, 4)]
    public void Kepp_writes_the_admitted_shape_and_unaligned_command_words(int bones, int commands)
    {
        var command = new CoveragePackedCommand(uint.MaxValue, ulong.MaxValue, Matrix4x4.Identity, Matrix4x4.Identity, 1);
        var poses = Enumerable.Range(0, commands).Select(i => command with { Id = (uint)(uint.MaxValue - i) }).ToArray();
        var pose = new CoverageNumericPose(Matrix4x4.Identity, Enumerable.Repeat(Matrix4x4.Identity, bones).ToArray(), poses);
        var header = new CoveragePackedHeader(bones, commands, 1, 64 + 64 * bones + 144 * commands);
        byte[] bytes = new byte[header.RecordStride];
        CoveragePackedPoseCodec.WriteRecord(bytes, header, pose);
        Assert.Equal(CoveragePackedFixture.RecordBytes(pose), bytes);
        var decoded = CoveragePackedPoseCodec.ReadRecord(bytes, header, Location);
        Assert.Equal(bytes, CoveragePackedFixture.RecordBytes(decoded));
    }

    [Fact]
    public void Kepp_chunk_byte_limit_is_inclusive()
    {
        byte[] bytes = Convert.FromHexString(CoveragePackedFixture.HeaderHex);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24, 4), 24966);
        var header = CoveragePackedPoseCodec.ReadHeader(bytes, 8388608, Location);
        Assert.Equal(24966, header.RecordCount);
        byte[] written = new byte[32];
        CoveragePackedPoseCodec.WriteHeader(written, header);
        Assert.Equal(bytes, written);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Kepp_command_identity_is_unique_and_instances_are_nonzero(bool duplicate)
    {
        var pose = CoveragePackedFixture.Pose();
        var valid = pose with { Commands = [pose.Commands[0], pose.Commands[0] with { Id = 24 }] };
        var header = new CoveragePackedHeader(2, 2, 1, 480);
        byte[] bytes = CoveragePackedFixture.RecordBytes(valid);
        int offset = duplicate ? 336 : 340;
        if (duplicate) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), 23);
        else BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset, 8), 0);
        var error = Assert.Throws<CoverageBuildDataException>(() => CoveragePackedPoseCodec.ReadRecord(bytes, header, Location));
        Assert.Equal(duplicate ? CoverageBuildInputRefusal.PackedCountMismatch : CoverageBuildInputRefusal.InvalidValue, error.Reason);
        AssertLocation(error, 32L + 3 * 480 + offset);
        Assert.Equal(CoveragePackedFixture.RecordBytes(valid),
            CoveragePackedFixture.RecordBytes(CoveragePackedPoseCodec.ReadRecord(CoveragePackedFixture.RecordBytes(valid), header, Location)));
    }

    [Fact]
    public void Kepp_writer_refuses_nonfinite_pose_words()
    {
        var pose = CoveragePackedFixture.Pose();
        var invalid = pose with { ActorModel = CoveragePackedFixture.Matrix([0x7fc00001]) };
        var header = new CoveragePackedHeader(2, 1, 1, 336);
        byte[] bytes = new byte[336];
        var error = Assert.Throws<CoverageBuildDataException>(() => CoveragePackedPoseCodec.WriteRecord(bytes, header, invalid));
        Assert.Equal(CoverageBuildInputRefusal.PackedWordNonFinite, error.Reason);
        Assert.Equal(32L, error.ByteOffset);
        CoveragePackedPoseCodec.WriteRecord(bytes, header, pose);
        Assert.Equal(CoveragePackedFixture.RecordBytes(pose), bytes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Kepp_writers_require_exact_destination_spans(bool headerOnly)
    {
        var header = new CoveragePackedHeader(2, 1, 1, 336);
        int length = headerOnly ? 32 : 336;
        var error = Assert.Throws<ArgumentException>(() =>
        {
            if (headerOnly) CoveragePackedPoseCodec.WriteHeader(new byte[length + 1], header);
            else CoveragePackedPoseCodec.WriteRecord(new byte[length + 1], header, CoveragePackedFixture.Pose());
        });
        Assert.Equal("destination", error.ParamName);
        DecodeControl();
    }

    [Fact]
    public void Packed_refusal_additions_preserve_existing_numeric_values()
    {
        CoverageBuildInputRefusal[] original = [CoverageBuildInputRefusal.InvalidJson, CoverageBuildInputRefusal.DuplicateMember,
            CoverageBuildInputRefusal.UnknownMember, CoverageBuildInputRefusal.MissingMember, CoverageBuildInputRefusal.InvalidValue,
            CoverageBuildInputRefusal.NonFiniteNumber, CoverageBuildInputRefusal.UnsupportedVersion, CoverageBuildInputRefusal.MalformedDigest,
            CoverageBuildInputRefusal.HashMismatch, CoverageBuildInputRefusal.CapacityExceeded, CoverageBuildInputRefusal.EmptyInventory,
            CoverageBuildInputRefusal.InvalidOrDuplicateId, CoverageBuildInputRefusal.DomainMembership, CoverageBuildInputRefusal.MissingInventorySample,
            CoverageBuildInputRefusal.UnknownCandidate, CoverageBuildInputRefusal.AssetBinding, CoverageBuildInputRefusal.InputIo];
        Assert.Equal(Enumerable.Range(0, 17), original.Select(value => (int)value));
        CoverageBuildValidationRefusal[] validation = [CoverageBuildValidationRefusal.ProvenanceMismatch, CoverageBuildValidationRefusal.RepresentativeMismatch,
            CoverageBuildValidationRefusal.CandidateConstruction, CoverageBuildValidationRefusal.StateInventoryMismatch,
            CoverageBuildValidationRefusal.OutputIo, CoverageBuildValidationRefusal.OutputAlias];
        Assert.Equal(Enumerable.Range(0, 6), validation.Select(value => (int)value));
        Assert.Equal(17, (int)CoverageBuildInputRefusal.PackedPathEscape);
        Assert.Equal(30, (int)CoverageBuildInputRefusal.ArtifactSetInvalid);
        Assert.Equal(6, (int)CoverageBuildValidationRefusal.ArtifactContentMismatch);
        Assert.Equal(7, (int)CoverageBuildValidationRefusal.OutputRecoveryRequired);
    }

    static void AssertLocation(CoverageBuildDataException error, long offset)
    {
        Assert.Equal(Location.Path, error.Path);
        Assert.Equal(Location.Identity, error.Identity);
        Assert.Equal(Location.ChunkIndex, error.ChunkIndex);
        Assert.Equal(Location.RecordIndex, error.RecordIndex);
        Assert.Equal(offset, error.ByteOffset);
    }

    static void DecodeControl()
    {
        byte[] control = CoveragePackedFixture.ChunkBytes();
        var header = CoveragePackedPoseCodec.ReadHeader(control.AsSpan(0, 32), control.Length, Location);
        Assert.Equal(CoveragePackedFixture.RecordBytes(CoveragePackedFixture.Pose()),
            CoveragePackedFixture.RecordBytes(CoveragePackedPoseCodec.ReadRecord(control.AsSpan(32), header, Location)));
    }
}
