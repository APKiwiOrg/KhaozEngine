using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using KhaozEngine.Movement;
using KhaozEngine.Physics.Bepu;
using Xunit;
using static KhaozEngine.Tests.Movement.GroundNavigationBakeRoundTripTests;

namespace KhaozEngine.Tests.Movement;

/// <summary>Container, identity and payload refusals. Two facts bound allocation, so the class joins the
/// AllocSensitive collection.</summary>
[Collection("AllocSensitive")]
public class GroundNavigationBakeRefusalTests
{
    private static readonly Lazy<Baked> Deck = new(() => Bake("deck"));
    private static readonly Lazy<Baked> StairOpen = new(() => Bake("stair-open"));
    private static readonly Lazy<Baked> WetPool = new(() => Bake("pool"));

    [Fact]
    public void NonBakeBytesAreNotABake()
    {
        byte[] file = Deck.Value.File;
        byte[] wrongMagic = [.. "KENC"u8, .. file.AsSpan(4, 48)];
        byte[] random = new byte[64];
        new Random(1234).NextBytes(random);

        foreach (byte[] bytes in new[] { Array.Empty<byte>(), wrongMagic, random, file[..3] })
            AssertRefused(Load(bytes), NavBakeLoadStatus.NotABake, "magic");
    }

    [Theory]
    [InlineData((ushort)2, (ushort)0)]
    [InlineData((ushort)1, (ushort)1)]
    public void UnknownVersionOrFlagsAreUnsupported(ushort version, ushort flags)
    {
        byte[] file = Clone(Deck.Value.File);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(4), version);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(6), flags);

        AssertRefused(Load(file), NavBakeLoadStatus.UnsupportedFormat, $"version {version}");
    }

    [Theory]
    [InlineData("engine", NavBakeLoadStatus.EngineChanged, "0.0.0-other")]
    [InlineData("options", NavBakeLoadStatus.OptionsChanged, "ProbeRange")]
    [InlineData("digest", NavBakeLoadStatus.SourcesChanged, "world")]
    [InlineData("extra-source", NavBakeLoadStatus.SourcesChanged, "catalog")]
    [InlineData("tuning", NavBakeLoadStatus.ProfilesChanged, "Gravity")]
    [InlineData("areas", NavBakeLoadStatus.ProfilesChanged, "Required")]
    public void StaleInputsAreRefusedWithTheFirstDifference(string change, NavBakeLoadStatus status, string detailFragment)
    {
        Baked deck = Deck.Value;
        NavBakeExpectation expected = deck.Expected;
        NavBakeProfile wet = expected.Profiles.Single(p => p.Name == "wet");
        NavBakeProfile dry = expected.Profiles.Single(p => p.Name == "dry");
        expected = change switch
        {
            "options" => expected with { Options = expected.Options with { ProbeRange = 7f } },
            "digest" => expected with { Sources = new NavBakeSources().Add("world", new byte[32]).AddHashOf("classifier", "fixture-v1"u8) },
            "extra-source" => expected with { Sources = Sources().Add("catalog", new byte[32]) },
            "tuning" => expected with { Profiles = [wet, dry with { Tuning = dry.Tuning with { Gravity = 24f } }] },
            "areas" => expected with { Profiles = [wet with { Areas = new NavAreaFilter(0x01u, 0u) }, dry] },
            _ => expected,
        };

        NavBakeLoadResult result = GroundNavigationBake.Load(new MemoryStream(deck.File), expected,
            change == "engine" ? "0.0.0-other" : NavBakeIdentity.CurrentEngineVersion);

        AssertRefused(result, status, detailFragment);
    }

    [Theory]
    [InlineData("empty-sources")]
    [InlineData("surface-cap-256")]
    [InlineData("cell-size-zero")]
    [InlineData("slope-mismatch")]
    [InlineData("negative-gravity")]
    [InlineData("half-height-below-radius")]
    public void InvalidExpectationThrows(string fault)
    {
        NavBakeExpectation expected = Deck.Value.Expected;
        NavBakeProfile dry = expected.Profiles.Single(p => p.Name == "dry");
        expected = fault switch
        {
            "empty-sources" => expected with { Sources = new NavBakeSources() },
            "surface-cap-256" => expected with { Options = expected.Options with { MaxSurfacesPerColumn = 256 } },
            "cell-size-zero" => expected with { Options = expected.Options with { CellSize = 0f } },
            "slope-mismatch" => expected with { Profiles = [dry with { Tuning = dry.Tuning with { MaxSlopeRadians = 0.7f } }] },
            "negative-gravity" => expected with { Profiles = [dry with { Tuning = dry.Tuning with { Gravity = -1f } }] },
            "half-height-below-radius" => expected with { Profiles = [dry with { Tuning = dry.Tuning with { CapsuleHalfHeight = 0.1f } }] },
            _ => throw new ArgumentOutOfRangeException(nameof(fault)),
        };

        Assert.ThrowsAny<ArgumentException>(() => GroundNavigationBake.Load(new UnreadableStream(), expected));
    }

    [Fact]
    public void StaleRefusalDoesNotReadThePayload()
    {
        Baked deck = Deck.Value;
        var stream = new CountingStream(deck.File);
        NavBakeExpectation stale = deck.Expected with { Options = deck.Expected.Options with { ProbeRange = 7f } };

        AssertRefused(GroundNavigationBake.Load(stream, stale), NavBakeLoadStatus.OptionsChanged, "ProbeRange");
        Assert.InRange(stream.BytesRead, 1, 52 + Header(deck.File).IdentityLength);
    }

    [Fact]
    public void EveryTruncationIsCorruptOrNotABake()
    {
        byte[] file = Deck.Value.File;
        var lengths = Enumerable.Range(0, 61).Concat(Enumerable.Range(0, file.Length / 97 + 1).Select(i => i * 97))
            .Where(length => length < file.Length).Distinct();

        foreach (int length in lengths)
        {
            foreach (NavBakeLoadResult result in new[] { Load(file[..length]), LoadOneByte(file[..length]) })
            {
                Assert.True(result.Status is NavBakeLoadStatus.Corrupt or NavBakeLoadStatus.NotABake,
                    $"Prefix of {length} bytes returned {result.Status}: {result.Detail}");
                Assert.Null(result.Bake);
                Assert.NotEmpty(result.Detail);
            }
        }
    }

    [Fact]
    public void TrailingBytesAreCorrupt()
    {
        byte[] file = [.. Deck.Value.File, 0];

        AssertRefused(Load(file), NavBakeLoadStatus.Corrupt, "remaining");
        AssertRefused(LoadOneByte(file), NavBakeLoadStatus.Corrupt, "follow the payload");
    }

    [Fact]
    public void SeekableLengthMismatchIsCorruptBeforeAllocation()
    {
        Baked large = LargeFlat();
        byte[] file = Clone(large.File);
        ulong payload = Header(file).PayloadLength;
        Assert.True(payload > 64 * 1024, $"The fixture payload of {payload} bytes cannot show the allocation bound.");
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(12), payload + 1);
        AssertRefused(GroundNavigationBake.Load(new MemoryStream(file), large.Expected), NavBakeLoadStatus.Corrupt, "remaining");

        var stream = new MemoryStream(file);
        long before = GC.GetAllocatedBytesForCurrentThread();
        NavBakeLoadResult result = GroundNavigationBake.Load(stream, large.Expected);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        AssertRefused(result, NavBakeLoadStatus.Corrupt, "remaining");
        Assert.True(allocated < 64 * 1024, $"A refused length allocated {allocated} bytes.");
    }

    [Fact]
    public void FlippedPayloadByteIsCorrupt()
    {
        byte[] file = Clone(Deck.Value.File);
        file[^1] ^= 0x40;

        AssertRefused(Load(file), NavBakeLoadStatus.Corrupt, "checksum");
    }

    [Theory]
    [InlineData("surfaces", "surface count")]
    [InlineData("layers", "layer count")]
    public void HostileCountsAreCorruptWithoutLargeAllocation(string field, string detailFragment)
    {
        Baked deck = Deck.Value;
        byte[] file = Clone(deck.File);
        PayloadMap map = Map(file, 2);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(field == "surfaces" ? map.SurfaceCount : map.Profiles[0].LayerCount), int.MaxValue);
        Reseal(file);
        AssertRefused(Load(file), NavBakeLoadStatus.Corrupt, detailFragment);

        var stream = new MemoryStream(file);
        long before = GC.GetAllocatedBytesForCurrentThread();
        NavBakeLoadResult result = GroundNavigationBake.Load(stream, deck.Expected);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        AssertRefused(result, NavBakeLoadStatus.Corrupt, detailFragment);
        Assert.True(allocated - file.Length < 1024 * 1024, $"A hostile count allocated {allocated} bytes for a {file.Length} byte file.");
    }

    [Theory]
    [InlineData("count-above-cap", "count 5")]
    [InlineData("count-sum", "sum")]
    [InlineData("descending-heights", "strictly ascending")]
    [InlineData("nan-height", "strictly ascending")]
    [InlineData("nan-headroom", "headroom")]
    [InlineData("negative-headroom", "headroom")]
    [InlineData("nan-open-height", "open height")]
    [InlineData("nan-ymin", "band")]
    [InlineData("cell-size", "placement")]
    [InlineData("yaw", "placement")]
    [InlineData("layer-width", "dimensions")]
    [InlineData("capture-width", "dimensions")]
    [InlineData("candidate-count", "candidate")]
    [InlineData("candidate-count-hostile", "candidate link count 2147483647 is impossible")]
    [InlineData("candidate-count-negative", "candidate link count -1 is impossible")]
    [InlineData("trailing-payload", "trailing")]
    public void ValueInvariantsAreEnforced(string fault, string detailFragment)
    {
        byte[] file = Clone(Deck.Value.File);
        PayloadMap map = Map(file, 2);
        int twoSurfaces = Array.IndexOf(file, (byte)2, map.Counts, map.Cells);
        int oneSurface = Array.IndexOf(file, (byte)1, map.Counts, map.Cells);
        Assert.True(twoSurfaces >= 0 && oneSurface >= 0, "The deck fixture needs columns with one and two surfaces.");
        int surface = map.Surfaces + 12 * file.AsSpan(map.Counts, twoSurfaces - map.Counts).ToArray().Sum(b => b);
        LayerMap layer = map.Profiles[0].Layers[0];
        switch (fault)
        {
            case "count-above-cap": file[map.Counts] = 5; break;
            case "count-sum": file[oneSurface] = 0; break;
            case "descending-heights":
                file.AsSpan(surface, 4).CopyTo(file.AsSpan(surface + 12, 4));
                break;
            case "nan-height": Single(file, map.Surfaces, float.NaN); break;
            case "nan-headroom": Single(file, map.Surfaces + 4, float.NaN); break;
            case "negative-headroom": Single(file, map.Surfaces + 4, -1f); break;
            case "nan-open-height": Single(file, layer.Heights, float.NaN); break;
            case "nan-ymin": Single(file, layer.Start + 24, float.NaN); break;
            case "cell-size": Single(file, layer.Start + 8, 0.25f); break;
            case "yaw": Single(file, layer.Start + 20, 0.1f); break;
            case "layer-width": BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(layer.Start), 19); break;
            case "capture-width": BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(map.Payload), 19); break;
            case "candidate-count": BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(map.Profiles[0].CandidateCount), 1); break;
            case "candidate-count-hostile":
                BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(map.Profiles[0].CandidateCount), int.MaxValue);
                break;
            case "candidate-count-negative": BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(map.Profiles[0].CandidateCount), -1); break;
            case "trailing-payload": file = [.. file, 0]; BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(12), Header(file).PayloadLength + 1); break;
            default: throw new ArgumentOutOfRangeException(nameof(fault));
        }
        Reseal(file);

        AssertRefused(Load(file), NavBakeLoadStatus.Corrupt, detailFragment);
    }

    [Theory]
    [InlineData("count-above-cells", "Water entry count 9 is impossible")]
    [InlineData("unsorted-cells", "strictly ascending")]
    [InlineData("cell-beyond", "strictly ascending")]
    [InlineData("nan-surface", "not finite")]
    [InlineData("below-lowest", "not above")]
    public void WaterInvariantsAreEnforced(string fault, string detailFragment)
    {
        Baked pool = WetPool.Value;
        byte[] file = Clone(pool.File);
        PayloadMap map = Map(file, 1);
        Assert.Equal(8, map.Cells);
        Assert.Equal(4, I32(file, map.Water));
        int first = map.Water + 4, second = first + 12, last = first + 36;
        switch (fault)
        {
            case "count-above-cells": BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(map.Water), map.Cells + 1); break;
            case "unsorted-cells":
                int cell = I32(file, first);
                BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(first), I32(file, second));
                BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(second), cell);
                break;
            case "cell-beyond": BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(last), map.Cells); break;
            case "nan-surface": Single(file, first + 4, float.NaN); break;
            case "below-lowest": Single(file, first + 4, -0.5f); break;
            default: throw new ArgumentOutOfRangeException(nameof(fault));
        }
        Reseal(file);

        AssertRefused(GroundNavigationBake.Load(new MemoryStream(file), pool.Expected), NavBakeLoadStatus.Corrupt, detailFragment);
    }

    [Fact]
    public void WaterEntryWithoutSampleWaterIsCorrupt()
    {
        byte[] dry = Deck.Value.File;
        PayloadMap map = Map(dry, 2);
        Assert.Equal(0, I32(dry, map.Water));
        byte[] entry = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(entry, 1);
        BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(4), 0);
        BinaryPrimitives.WriteSingleLittleEndian(entry.AsSpan(8), 1.5f);
        byte[] file = [.. dry.AsSpan(0, map.Water), .. entry, .. dry.AsSpan(map.Water + 4)];
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(12), Header(dry).PayloadLength + 12);
        Reseal(file);

        AssertRefused(Load(file), NavBakeLoadStatus.Corrupt, "SampleWater");
    }

    [Fact]
    public void EveryTruncationOfAWetBakeIsCorruptOrNotABake()
    {
        Baked pool = WetPool.Value;
        byte[] file = pool.File;

        for (int length = 0; length < file.Length; length++)
        {
            foreach (NavBakeLoadResult result in new[]
            {
                GroundNavigationBake.Load(new MemoryStream(file[..length]), pool.Expected),
                GroundNavigationBake.Load(new OneByteStream(file[..length]), pool.Expected),
            })
            {
                Assert.True(result.Status is NavBakeLoadStatus.Corrupt or NavBakeLoadStatus.NotABake,
                    $"Prefix of {length} bytes returned {result.Status}: {result.Detail}");
                Assert.Null(result.Bake);
            }
        }
    }

    [Fact]
    public void UnusedBitsetBitsAreCorrupt()
    {
        byte[] blocked = Clone(Deck.Value.File);
        PayloadMap deck = Map(blocked, 2);
        Assert.NotEqual(0, deck.Cells % 8);
        blocked[deck.Profiles[0].Layers[0].Heights - 1] |= 0x80;
        Reseal(blocked);
        AssertRefused(Load(blocked), NavBakeLoadStatus.Corrupt, "unused");

        Baked stair = StairOpen.Value;
        byte[] links = Clone(stair.File);
        ProfileMap profile = Map(links, 1).Profiles[0];
        Assert.InRange(profile.Candidates % 8, 1, 7);
        Assert.NotEqual(0, links[profile.LinkBits]);
        links[^1] |= 0x80;
        Reseal(links);
        AssertRefused(GroundNavigationBake.Load(new MemoryStream(links), stair.Expected), NavBakeLoadStatus.Corrupt, "unused");
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("blocked-neighbour")]
    public void ExitToABlockedCellIsCorrupt(string target)
    {
        Baked deck = Deck.Value;
        byte[] file = Clone(deck.File);
        PayloadMap map = Map(file, 2);
        (int X, int Z)[] directions = [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)];
        int width = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(map.Payload));
        int height = map.Cells / width;
        // First open cell and direction whose target leaves the layer, or lands on a blocked cell inside it.
        var found = map.Profiles.SelectMany(p => p.Layers).SelectMany(layer =>
            Enumerable.Range(0, layer.Cells).Where(i => Open(file, layer, i)).SelectMany(i =>
                Enumerable.Range(0, 8).Select(d => (Layer: layer, Cell: i, Direction: d,
                    X: i % width + directions[d].X, Z: i / width + directions[d].Z))))
            .First(c =>
            {
                bool inside = c.X >= 0 && c.Z >= 0 && c.X < width && c.Z < height;
                return target == "outside" ? !inside : inside && !Open(file, c.Layer, c.Z * width + c.X);
            });
        int ordinal = Enumerable.Range(0, found.Cell).Count(i => Open(file, found.Layer, i));
        file[found.Layer.Exits + ordinal] |= (byte)(1 << found.Direction);
        Reseal(file);

        AssertRefused(Load(file), NavBakeLoadStatus.Corrupt, $"cell {found.Cell} has an exit");
    }

    [Fact]
    public void SeekableIdentityLengthBeyondTheStreamIsCorruptBeforeAllocation()
    {
        byte[] file = Clone(Deck.Value.File);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), 1 << 20);
        AssertRefused(Load(file), NavBakeLoadStatus.Corrupt, "remaining");

        var stream = new MemoryStream(file);
        long before = GC.GetAllocatedBytesForCurrentThread();
        NavBakeLoadResult result = GroundNavigationBake.Load(stream, Deck.Value.Expected);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        AssertRefused(result, NavBakeLoadStatus.Corrupt, "remaining");
        Assert.True(allocated < 64 * 1024, $"A refused identity length allocated {allocated} bytes.");
        AssertRefused(LoadOneByte(file), NavBakeLoadStatus.Corrupt, "identity block is truncated");
    }

    [Fact]
    public void LoadHandlesOneByteReads()
    {
        Baked deck = Deck.Value;

        NavBakeLoadResult result = LoadOneByte(deck.File);

        Assert.True(result.Status == NavBakeLoadStatus.Loaded, $"{result.Status}: {result.Detail}");
        Assert.Equal(deck.File, Write(result.Bake!));
    }

    [Fact]
    public void SubsetExpectationIsRefusedNamingTheMissingProfile()
    {
        Baked door = Bake("door");
        NavBakeExpectation small = door.Expected with { Profiles = [door.Expected.Profiles.Single(p => p.Name == "small")] };

        AssertRefused(GroundNavigationBake.Load(new MemoryStream(door.File), small), NavBakeLoadStatus.ProfilesChanged, "'wide'");
    }

    private static NavBakeLoadResult Load(byte[] file) => GroundNavigationBake.Load(new MemoryStream(file), Deck.Value.Expected);

    private static NavBakeLoadResult LoadOneByte(byte[] file) => GroundNavigationBake.Load(new OneByteStream(file), Deck.Value.Expected);

    private static void AssertRefused(NavBakeLoadResult result, NavBakeLoadStatus status, string detailFragment)
    {
        Assert.True(result.Status == status, $"Expected {status}, got {result.Status}: {result.Detail}");
        Assert.Null(result.Bake);
        Assert.Contains(detailFragment, result.Detail, StringComparison.Ordinal);
    }

    private static Baked LargeFlat()
    {
        var options = new PhysicsNavBakeOptions(-8f, -8f, 8f, 8f, 0.25f, 5f, 6f, 0.8f, 4096, 4096);
        NavBakeProfile[] profiles = [new("player", GroundTraversalProbeTests.Tuning, default)];
        using BepuPhysicsWorld world = GroundTraversalProbeTests.FlatWorld();
        using PhysicsNavBake capture = PhysicsNavBake.Capture(new GroundMoveContext((_, _) => 0f, physics: world), options, _ => 0u);
        GroundNavigationBake bake = GroundNavigationBake.Create(capture, Sources(), profiles);
        return new Baked(bake, new NavBakeExpectation(options, Sources(), profiles), Write(bake));
    }

    private static byte[] Clone(byte[] file) => (byte[])file.Clone();

    private static bool Open(byte[] file, LayerMap layer, int cell) => (file[layer.Blocked + (cell >> 3)] & (1 << (cell & 7))) == 0;

    private static void Single(byte[] file, int offset, float value) =>
        BinaryPrimitives.WriteSingleLittleEndian(file.AsSpan(offset), value);

    /// <summary>Recomputes the payload checksum after a deliberate payload edit, so the decoder sees the edit.</summary>
    private static void Reseal(byte[] file)
    {
        int payload = 52 + Header(file).IdentityLength;
        SHA256.HashData(file.AsSpan(payload)).CopyTo(file, 20);
    }

    private sealed record LayerMap(int Start, int Blocked, int Heights, int Exits, int Cells);

    private sealed record ProfileMap(int LayerCount, LayerMap[] Layers, int CandidateCount, int LinkBits, int Candidates);

    private sealed record PayloadMap(int Payload, int SurfaceCount, int Counts, int Surfaces, int Water, int Cells,
        ProfileMap[] Profiles);

    // Walks the version 1 payload layout independently of the reader and checks that it ends at the file end.
    private static PayloadMap Map(byte[] file, int profileCount)
    {
        int payload = 52 + Header(file).IdentityLength;
        int cells = I32(file, payload) * I32(file, payload + 4);
        int counts = payload + 24, surfaces = counts + cells;
        int water = surfaces + 12 * I32(file, payload + 20);
        int at = water + 4 + 12 * I32(file, water);
        var profiles = new ProfileMap[profileCount];
        for (int p = 0; p < profileCount; p++)
        {
            int layerCount = at;
            var layers = new LayerMap[I32(file, at)];
            at += 4;
            for (int l = 0; l < layers.Length; l++)
            {
                int blocked = at + 32, heights = blocked + (cells + 7) / 8;
                int open = Enumerable.Range(0, cells).Count(i => (file[blocked + (i >> 3)] & (1 << (i & 7))) == 0);
                layers[l] = new LayerMap(at, blocked, heights, heights + 4 * open, cells);
                at = heights + 5 * open;
            }
            int candidates = I32(file, at);
            profiles[p] = new ProfileMap(layerCount, layers, at, at + 4, candidates);
            at += 4 + (candidates + 7) / 8;
        }
        Assert.Equal(file.Length, at);
        return new PayloadMap(payload, payload + 20, counts, surfaces, water, cells, profiles);
    }

    private static int I32(byte[] file, int offset) => BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(offset));

    private sealed class OneByteStream(byte[] data) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty || _position == data.Length) return 0;
            buffer[0] = data[_position++];
            return 1;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Seekable like a file, counting every byte handed to the reader.
    private sealed class CountingStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int read = _inner.Read(buffer);
            BytesRead += read;
            return read;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class UnreadableStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => throw new InvalidOperationException("The expectation must be refused first.");
        public override long Position
        {
            get => throw new InvalidOperationException("The expectation must be refused first.");
            set => throw new InvalidOperationException("The expectation must be refused first.");
        }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("The expectation must be refused first.");
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
