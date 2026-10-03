using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using KhaozEngine.Locomotion;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

public sealed partial class GroundNavigationBake
{
    private const int CaptureHeaderBytes = 24;
    private const int SurfaceBytes = 12;
    private const int LayerHeaderBytes = 32;
    private const int LinkCountBytes = 4;

    // Exit bit order of NavTraversalLayer: +X, -X, +Z, -Z, +X+Z, +X-Z, -X+Z, -X-Z.
    private static readonly (int X, int Z)[] ExitDirections =
        [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)];

    /// <summary>Loads against <paramref name="engineVersion"/> instead of the Movement assembly's version.</summary>
    internal static NavBakeLoadResult Load(Stream source, NavBakeExpectation expected, string engineVersion)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(expected);
        NavBakeProfile[]? snapshot = expected.Profiles is null ? null : [.. expected.Profiles];
        byte[] encoded = NavBakeIdentity.Encode(expected with { Profiles = snapshot! }, engineVersion);
        if (encoded.Length > MaxIdentityLength)
            throw new ArgumentException("The expected identity block exceeds 1 MiB.", nameof(expected));
        LoadPlan plan = Plan(expected, snapshot!);

        Span<byte> header = stackalloc byte[HeaderLength];
        int read = source.ReadAtLeast(header, HeaderLength, throwOnEndOfStream: false);
        if (read < Magic.Length || !header[..Magic.Length].SequenceEqual(Magic))
            return Refuse(NavBakeLoadStatus.NotABake, "The bytes do not start with the KENB magic.");
        if (read < HeaderLength)
            return Refuse(NavBakeLoadStatus.Corrupt, $"The header is truncated at {read} of {HeaderLength} bytes.");
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
        uint identityLength = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        ulong payloadLength = BinaryPrimitives.ReadUInt64LittleEndian(header[12..]);
        if (version != NavBakeIdentity.FormatVersion || flags != 0)
            return Refuse(NavBakeLoadStatus.UnsupportedFormat, $"Format version {version} with flags {flags} is not " +
                $"supported. This engine reads version {NavBakeIdentity.FormatVersion} with flags 0.");
        if (identityLength > MaxIdentityLength)
            return Refuse(NavBakeLoadStatus.Corrupt, $"Identity block length {identityLength} exceeds 1 MiB.");
        if (source.CanSeek && identityLength > source.Length - source.Position)
            return Refuse(NavBakeLoadStatus.Corrupt,
                $"Identity block length {identityLength} exceeds the {source.Length - source.Position} remaining stream bytes.");

        byte[] identity = new byte[identityLength];
        read = source.ReadAtLeast(identity, identity.Length, throwOnEndOfStream: false);
        if (read < identity.Length)
            return Refuse(NavBakeLoadStatus.Corrupt, $"The identity block is truncated at {read} of {identity.Length} bytes.");
        (NavBakeLoadStatus status, string detail) = NavBakeIdentity.Compare(identity, encoded);
        if (status != NavBakeLoadStatus.Loaded) return Refuse(status, detail);

        ulong bound = PayloadBound(plan);
        if (payloadLength > bound)
            return Refuse(NavBakeLoadStatus.Corrupt, $"Payload length {payloadLength} exceeds the bound {bound} for the expected options.");
        if (source.CanSeek)
        {
            long remaining = source.Length - source.Position;
            if (remaining < 0 || (ulong)remaining != payloadLength)
                return Refuse(NavBakeLoadStatus.Corrupt, $"Payload length {payloadLength} differs from the {remaining} remaining stream bytes.");
        }
        byte[] payload = new byte[(int)payloadLength];
        read = source.ReadAtLeast(payload, payload.Length, throwOnEndOfStream: false);
        if (read < payload.Length)
            return Refuse(NavBakeLoadStatus.Corrupt, $"The payload is truncated at {read} of {payload.Length} bytes.");
        Span<byte> probe = stackalloc byte[1];
        if (source.Read(probe) != 0) return Refuse(NavBakeLoadStatus.Corrupt, "Bytes follow the payload.");
        Span<byte> checksum = stackalloc byte[32];
        SHA256.HashData(payload, checksum);
        if (!checksum.SequenceEqual(header[20..HeaderLength]))
            return Refuse(NavBakeLoadStatus.Corrupt, "The payload checksum does not match.");

        try
        {
            string? problem = Decode(payload, plan, out Vector3 origin, out PhysicsNavColumns? columns, out GroundNavigation[]? profiles);
            if (problem is not null) return Refuse(NavBakeLoadStatus.Corrupt, problem);
            var names = Array.ConvertAll(plan.Profiles, static p => p.Name);
            return new NavBakeLoadResult(NavBakeLoadStatus.Loaded, "", new GroundNavigationBake(identity, origin, columns!, names, profiles!));
        }
        catch (ArgumentException e)
        {
            return Refuse(NavBakeLoadStatus.Corrupt, $"Navigation validation rejected the payload: {e.Message}");
        }
    }

    // Validates the expectation as Create would, so invalid options or tuning never size a buffer.
    private static LoadPlan Plan(NavBakeExpectation expected, NavBakeProfile[] profiles)
    {
        PhysicsNavBakeOptions options = expected.Options;
        (int width, int height, int cells, _) = options.ValidateWithoutOrigin();
        if (options.MaxSurfacesPerColumn > MaxStoredSurfacesPerColumn)
            throw new ArgumentOutOfRangeException(nameof(expected), "A bake stores at most 255 surfaces per column.");
        NavBakeProfile[] sorted = SortedByName(profiles);
        foreach (NavBakeProfile profile in sorted)
        {
            GroundMoveContext.CheckTuning(profile.Tuning);
            if (profile.Tuning.MaxSlopeRadians != options.MaxSlopeRadians)
                throw new ArgumentException($"Profile '{profile.Name}' slope must equal the capture slope.", nameof(expected));
            if (!float.IsFinite(2f * profile.Tuning.CapsuleHalfHeight))
                throw new ArgumentOutOfRangeException(nameof(expected), $"Profile '{profile.Name}' height must be finite.");
        }
        return new LoadPlan(options, width, height, cells, options.MaxLayerCells / cells, sorted);
    }

    // The design bound plus one stored candidate count per profile, saturating instead of overflowing.
    private static ulong PayloadBound(LoadPlan plan)
    {
        ulong c = (ulong)plan.Cells, m = (ulong)plan.Options.MaxSurfacesPerColumn;
        ulong layers = (ulong)plan.MaxLayers, n = (ulong)plan.Profiles.Length;
        ulong capture = Add(Add(CaptureHeaderBytes, c), Mul(Mul(SurfaceBytes, c), m));
        ulong layer = Add(Add(LayerHeaderBytes, (c + 7) / 8), Mul(5, c));
        ulong links = Mul(Mul(layers, layers == 0 ? 0 : layers - 1), c);
        ulong profile = Add(Add(Add(4, Mul(layers, layer)), LinkCountBytes), links);
        return Math.Min(Add(capture, Mul(n, profile)), (ulong)Array.MaxLength);
    }

    private static ulong Add(ulong a, ulong b) => a > ulong.MaxValue - b ? ulong.MaxValue : a + b;

    private static ulong Mul(ulong a, ulong b) => a != 0 && b > ulong.MaxValue / a ? ulong.MaxValue : a * b;

    private static string? Decode(ReadOnlySpan<byte> payload, LoadPlan plan, out Vector3 origin,
        out PhysicsNavColumns? columns, out GroundNavigation[]? profiles)
    {
        profiles = null;
        var reader = new NavBakeReader(payload);
        if (DecodeColumns(ref reader, plan, out origin, out columns) is { } problem) return problem;
        var built = new GroundNavigation[plan.Profiles.Length];
        // One scratch set for every layer of every profile. Grids and traversal layers copy their inputs.
        var scratch = new LayerScratch(new bool[plan.Cells], new bool[plan.Cells], new float[plan.Cells], new byte[plan.Cells]);
        for (int i = 0; i < built.Length; i++)
        {
            if (DecodeProfile(ref reader, plan, plan.Profiles[i], columns!, scratch, out GroundNavigation? profile) is { } failed)
                return failed;
            built[i] = profile!;
        }
        if (reader.Remaining != 0) return $"The payload has {reader.Remaining} trailing bytes.";
        profiles = built;
        return null;
    }

    private static string? DecodeColumns(ref NavBakeReader reader, LoadPlan plan, out Vector3 origin, out PhysicsNavColumns? columns)
    {
        origin = default;
        columns = null;
        if (!reader.TryReadInt32(out int width) || !reader.TryReadInt32(out int height) ||
            !reader.TryReadSingle(out float x) || !reader.TryReadSingle(out float y) || !reader.TryReadSingle(out float z) ||
            !reader.TryReadInt32(out int surfaceCount))
            return "The payload is truncated in the capture header.";
        if (width != plan.Width || height != plan.Height)
            return $"Capture dimensions {width} by {height} differ from the options' {plan.Width} by {plan.Height}.";
        origin = new Vector3(x, y, z);
        int cap = plan.Options.MaxSurfacesPerColumn;
        if (surfaceCount < 0 || surfaceCount > (long)plan.Cells * cap ||
            (long)surfaceCount * SurfaceBytes > reader.Remaining - (long)plan.Cells)
            return $"Capture surface count {surfaceCount} is impossible for {plan.Cells} cells and the remaining bytes.";
        if (!reader.TryReadBytes(plan.Cells, out ReadOnlySpan<byte> counts)) return "The payload is truncated in the surface counts.";

        var starts = new int[plan.Cells + 1];
        int sum = 0;
        for (int cell = 0; cell < plan.Cells; cell++)
        {
            starts[cell] = sum;
            if (counts[cell] > cap) return $"Cell {cell} surface count {counts[cell]} is above MaxSurfacesPerColumn {cap}.";
            sum += counts[cell];
        }
        starts[plan.Cells] = sum;
        if (sum != surfaceCount) return $"Per-cell surface counts sum to {sum}, not the stored {surfaceCount}.";

        var surfaces = new PhysicsNavSurface[surfaceCount];
        for (int cell = 0; cell < plan.Cells; cell++)
        {
            float previous = float.NegativeInfinity;
            for (int i = starts[cell]; i < starts[cell + 1]; i++)
            {
                if (!reader.TryReadSingle(out float surfaceHeight) || !reader.TryReadSingle(out float headroom) ||
                    !reader.TryReadUInt32(out uint areas))
                    return "The payload is truncated in the surfaces.";
                if (!float.IsFinite(surfaceHeight) || surfaceHeight <= previous)
                    return $"Column {cell} heights are not finite and strictly ascending.";
                if (float.IsNaN(headroom) || headroom < 0f) return $"Column {cell} headroom {headroom} is NaN or negative.";
                surfaces[i] = new PhysicsNavSurface(surfaceHeight, headroom, areas);
                previous = surfaceHeight;
            }
        }
        columns = PhysicsNavColumns.Own(plan.Options, width, height, starts, surfaces);
        return null;
    }

    private static string? DecodeProfile(ref NavBakeReader reader, LoadPlan plan, NavBakeProfile profile,
        PhysicsNavColumns columns, LayerScratch scratch, out GroundNavigation? navigation)
    {
        navigation = null;
        string name = profile.Name;
        int cells = plan.Cells, maskBytes = BitsetLength(cells);
        if (!reader.TryReadInt32(out int layerCount)) return $"The payload is truncated before profile '{name}'.";
        if (layerCount < 1 || layerCount > plan.MaxLayers || layerCount > reader.Remaining / (LayerHeaderBytes + maskBytes))
            return $"Profile '{name}' layer count {layerCount} is impossible for the layer budget and the remaining bytes.";

        var grids = new NavGrid[layerCount];
        var traversal = new NavTraversalLayer[layerCount];
        (bool[] blocked, bool[] accepted, float[] heights, byte[] exits) = scratch;
        PhysicsNavBakeOptions options = plan.Options;
        for (int layer = 0; layer < layerCount; layer++)
        {
            if (!reader.TryReadInt32(out int width) || !reader.TryReadInt32(out int height) ||
                !reader.TryReadSingle(out float cellSize) || !reader.TryReadSingle(out float originX) ||
                !reader.TryReadSingle(out float originZ) || !reader.TryReadSingle(out float yaw) ||
                !reader.TryReadSingle(out float yMin) || !reader.TryReadSingle(out float yMax) ||
                !reader.TryReadBytes(maskBytes, out ReadOnlySpan<byte> mask))
                return $"Profile '{name}' layer {layer} is truncated.";
            if (width != plan.Width || height != plan.Height)
                return $"Profile '{name}' layer {layer} dimensions {width} by {height} differ from the capture.";
            if (Bits(cellSize) != Bits(options.CellSize) || Bits(originX) != Bits(options.MinX) ||
                Bits(originZ) != Bits(options.MinZ) || Bits(yaw) != 0u)
                return $"Profile '{name}' layer {layer} placement differs from the options.";
            if (float.IsNaN(yMin) || float.IsNaN(yMax)) return $"Profile '{name}' layer {layer} has a NaN band.";
            if (!UnusedBitsClear(mask, cells)) return $"Profile '{name}' layer {layer} blocked bitset has unused bits set.";

            int open = 0;
            for (int i = 0; i < cells; i++)
            {
                blocked[i] = IsSet(mask, i);
                accepted[i] = !blocked[i];
                if (accepted[i]) open++;
            }
            if (open * 5L > reader.Remaining) return $"Profile '{name}' layer {layer} is truncated in its open cells.";
            for (int i = 0; i < cells; i++)
            {
                heights[i] = 0f;
                if (blocked[i]) continue;
                if (!reader.TryReadSingle(out heights[i])) return $"Profile '{name}' layer {layer} is truncated in its heights.";
                if (!float.IsFinite(heights[i])) return $"Profile '{name}' layer {layer} open height at cell {i} is not finite.";
            }
            for (int i = 0; i < cells; i++)
            {
                exits[i] = 0;
                if (!blocked[i] && !reader.TryReadUInt8(out exits[i]))
                    return $"Profile '{name}' layer {layer} is truncated in its exits.";
            }
            if (FirstBadExit(blocked, exits, width, height) is int bad)
                return $"Profile '{name}' layer {layer} cell {bad} has an exit to a blocked or outside cell.";
            grids[layer] = NavGrid.FromBlockedSurfaces(width, height, cellSize, originX, originZ, blocked, heights, yMin, yMax, yaw);
            traversal[layer] = new NavTraversalLayer(width, height, accepted, exits);
        }

        // Bound the stored count by the bytes left before link generation can allocate a candidate list.
        if (!reader.TryReadInt32(out int storedCandidates)) return $"Profile '{name}' is truncated before its links.";
        if (storedCandidates < 0 || BitsetLength(storedCandidates) > reader.Remaining)
            return $"Profile '{name}' candidate link count {storedCandidates} is impossible for the remaining bytes.";
        MoveTuning tuning = profile.Tuning;
        IReadOnlyList<NavLink> candidates = NavLayerLinks.GenerateGrounded(grids, tuning.StepHeight);
        if (storedCandidates != candidates.Count)
            return $"Profile '{name}' stores {storedCandidates} candidate links but its grids regenerate {candidates.Count}.";
        if (!reader.TryReadBytes(BitsetLength(candidates.Count), out ReadOnlySpan<byte> linkBits))
            return $"Profile '{name}' is truncated in its accepted links.";
        if (!UnusedBitsClear(linkBits, candidates.Count)) return $"Profile '{name}' accepted-link bitset has unused bits set.";
        var links = new List<NavLink>();
        for (int i = 0; i < candidates.Count; i++)
            if (IsSet(linkBits, i)) links.Add(candidates[i]);

        var graph = new NavTraversalGraph(new NavSpace(grids, candidates), tuning.CapsuleRadius,
            2f * tuning.CapsuleHalfHeight, traversal, links);
        navigation = new GroundNavigation(graph, tuning, new NavAreaFootprint(columns, options, tuning, profile.Areas));
        return null;
    }

    private static int? FirstBadExit(bool[] blocked, byte[] exits, int width, int height)
    {
        for (int i = 0; i < exits.Length; i++)
        {
            if (exits[i] == 0) continue;
            int x = i % width, z = i / width;
            for (int direction = 0; direction < ExitDirections.Length; direction++)
            {
                if ((exits[i] & (1 << direction)) == 0) continue;
                int nx = x + ExitDirections[direction].X, nz = z + ExitDirections[direction].Z;
                if (nx < 0 || nz < 0 || nx >= width || nz >= height || blocked[nz * width + nx]) return i;
            }
        }
        return null;
    }

    private static bool IsSet(ReadOnlySpan<byte> bits, int index) => (bits[index >> 3] & (1 << (index & 7))) != 0;

    private static bool UnusedBitsClear(ReadOnlySpan<byte> bits, int count)
        => (count & 7) == 0 || bits[^1] >> (count & 7) == 0;

    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);

    private static NavBakeLoadResult Refuse(NavBakeLoadStatus status, string detail) => new(status, detail, null);

    private sealed record LoadPlan(PhysicsNavBakeOptions Options, int Width, int Height, int Cells, int MaxLayers,
        NavBakeProfile[] Profiles);

    private readonly record struct LayerScratch(bool[] Blocked, bool[] Accepted, float[] Heights, byte[] Exits);
}
