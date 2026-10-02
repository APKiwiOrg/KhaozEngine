using System;

namespace KhaozEngine.Navigation;

/// <summary>Owned row-major node acceptance and directed neighbor exits for one traversal layer.</summary>
public sealed class NavTraversalLayer
{
    readonly bool[] _acceptedNodes;
    readonly byte[] _exits;

    /// <summary>Width in cells.</summary>
    public int Width { get; }

    /// <summary>Height in cells.</summary>
    public int Height { get; }

    /// <summary>
    /// Copies both inputs. Exit bits 0 through 7 mean +X, -X, +Z, -Z, +X+Z, +X-Z, -X+Z, -X-Z.
    /// The owning graph validates that exits connect accepted cells within this layer.
    /// </summary>
    public NavTraversalLayer(int width, int height, ReadOnlySpan<bool> acceptedNodes, ReadOnlySpan<byte> exits)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0 || (long)width * height > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(height));
        int count = width * height;
        if (acceptedNodes.Length != count)
            throw new ArgumentException("Node storage must match the layer dimensions.", nameof(acceptedNodes));
        if (exits.Length != count)
            throw new ArgumentException("Exit storage must match the layer dimensions.", nameof(exits));

        Width = width;
        Height = height;
        _acceptedNodes = acceptedNodes.ToArray();
        _exits = exits.ToArray();
    }

    /// <summary>Whether the profile accepted this cell. False outside the layer.</summary>
    public bool IsAccepted(int x, int z) => InBounds(x, z) && _acceptedNodes[z * Width + x];

    /// <summary>Directed neighbor exits from this cell. Zero outside the layer.</summary>
    public byte ExitMask(int x, int z) => InBounds(x, z) ? _exits[z * Width + x] : (byte)0;

    bool InBounds(int x, int z) => x >= 0 && z >= 0 && x < Width && z < Height;

    internal static int NeighborX(int index) => index switch
    {
        0 or 4 or 5 => 1,
        1 or 6 or 7 => -1,
        _ => 0,
    };

    internal static int NeighborZ(int index) => index switch
    {
        2 or 4 or 6 => 1,
        3 or 5 or 7 => -1,
        _ => 0,
    };
}
