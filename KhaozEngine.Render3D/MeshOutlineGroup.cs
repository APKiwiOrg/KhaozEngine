namespace KhaozEngine.Render3D;

/// <summary>
/// Identifies one frame-local target outline. Every mesh part submitted through the same group forms one
/// camera-projected union with one colour and pixel width.
/// </summary>
public readonly struct MeshOutlineGroup
{
    internal int Owner { get; }
    internal int Frame { get; }
    /// <summary>Implementation-owned group index. Consumers pass the handle back unchanged.</summary>
    public int Index { get; }

    internal MeshOutlineGroup(int owner, int frame, int index)
    {
        Owner = owner;
        Frame = frame;
        Index = index;
    }

    /// <summary>
    /// Creates a handle for a custom <c>ITileWorldScene</c> implementation. <see cref="Scene3D"/> returns
    /// scene-owned handles and rejects handles constructed by callers.
    /// </summary>
    public MeshOutlineGroup(int index)
    {
        Owner = 0;
        Frame = 0;
        Index = index;
    }
}
