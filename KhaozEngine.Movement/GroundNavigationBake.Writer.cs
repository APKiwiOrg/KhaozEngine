using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

public sealed partial class GroundNavigationBake
{
    /// <summary>Container header length: magic, version, flags, identity length, payload length and checksum.</summary>
    internal const int HeaderLength = 52;

    internal static ReadOnlySpan<byte> Magic => "KENB"u8;

    /// <summary>Writes the canonical bytes. Two writes of one bake, and a write of a loaded bake, are byte-identical.
    /// Stream errors propagate.</summary>
    public void WriteTo(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        byte[] payload = EncodePayload();
        var header = new NavBakeWriter();
        header.WriteBytes(Magic);
        header.WriteUInt16(NavBakeIdentity.FormatVersion);
        header.WriteUInt16(0);
        header.WriteUInt32((uint)_identity.Length);
        header.WriteUInt64((ulong)payload.Length);
        header.WriteBytes(SHA256.HashData(payload));
        destination.Write(header.WrittenSpan);
        destination.Write(_identity);
        destination.Write(payload);
    }

    private byte[] EncodePayload()
    {
        var writer = new NavBakeWriter();
        int width = _columns.Width, height = _columns.Height;
        writer.WriteInt32(width);
        writer.WriteInt32(height);
        writer.WriteSingle(_origin.X);
        writer.WriteSingle(_origin.Y);
        writer.WriteSingle(_origin.Z);
        writer.WriteInt32(_columns.SurfaceCount);
        for (int z = 0; z < height; z++)
            for (int x = 0; x < width; x++)
            {
                int count = _columns.GetColumn(x, z).Length;
                if (count > MaxStoredSurfacesPerColumn)
                    throw new InvalidOperationException("A captured column exceeds the stored surface cap.");
                writer.WriteUInt8((byte)count);
            }
        for (int z = 0; z < height; z++)
            for (int x = 0; x < width; x++)
                foreach (PhysicsNavSurface surface in _columns.GetColumn(x, z))
                {
                    writer.WriteSingle(surface.Height);
                    writer.WriteSingle(surface.Headroom);
                    writer.WriteUInt32(surface.Areas);
                }
        foreach (GroundNavigation profile in _profiles) WriteProfile(writer, profile);
        return writer.ToArray();
    }

    private static void WriteProfile(NavBakeWriter writer, GroundNavigation profile)
    {
        NavSpace space = profile.Space;
        NavTraversalGraph graph = profile.Graph;
        writer.WriteInt32(space.Layers.Count);
        for (int layer = 0; layer < space.Layers.Count; layer++)
        {
            NavGrid grid = space.Layers[layer];
            NavTraversalLayer traversal = graph.Layers[layer];
            int cells = grid.Width * grid.Height;
            writer.WriteInt32(grid.Width);
            writer.WriteInt32(grid.Height);
            writer.WriteSingle(grid.CellSize);
            writer.WriteSingle(grid.OriginX);
            writer.WriteSingle(grid.OriginZ);
            writer.WriteSingle(grid.YawRadians);
            writer.WriteSingle(grid.YMin);
            writer.WriteSingle(grid.YMax);

            var blocked = new byte[BitsetLength(cells)];
            for (int i = 0; i < cells; i++)
            {
                int x = i % grid.Width, z = i / grid.Width;
                bool open = grid.ClearanceAt(x, z) != 0;
                if (traversal.IsAccepted(x, z) != open)
                    throw new InvalidOperationException("Accepted nodes must be exactly the open cells.");
                if (!open) blocked[i >> 3] |= (byte)(1 << (i & 7));
            }
            writer.WriteBytes(blocked);
            for (int i = 0; i < cells; i++)
                if (grid.SurfaceHeightAt(i % grid.Width, i / grid.Width) is float y) writer.WriteSingle(y);
            for (int i = 0; i < cells; i++)
                if (grid.ClearanceAt(i % grid.Width, i / grid.Width) != 0)
                    writer.WriteUInt8(traversal.ExitMask(i % grid.Width, i / grid.Width));
        }

        IReadOnlyList<NavLink> candidates = space.Links, accepted = graph.Links;
        writer.WriteInt32(candidates.Count);
        var bits = new byte[BitsetLength(candidates.Count)];
        int next = 0;
        for (int i = 0; i < candidates.Count; i++)
        {
            if (next >= accepted.Count || accepted[next] != candidates[i]) continue;
            bits[i >> 3] |= (byte)(1 << (i & 7));
            next++;
        }
        if (next != accepted.Count)
            throw new InvalidOperationException("Accepted links must be an ordered subset of the candidate links.");
        writer.WriteBytes(bits);
    }

    private static int BitsetLength(int count) => (int)(((long)count + 7) / 8);
}
