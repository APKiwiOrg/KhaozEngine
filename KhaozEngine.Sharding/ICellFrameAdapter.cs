using KhaozEngine.Ecs;
using KhaozEngine.Primitives;

namespace KhaozEngine.Sharding;

/// <summary>
/// Re-expresses an entity that has just ENTERED a cell into that cell's <see cref="WorldFrame"/>. A cell is a
/// simulation island and every entity in its world - owned or mirrored - must carry that island's frame, or a
/// consumer system reading a frame-local position off a ghost is querying the cell's physics world about somewhere
/// else entirely.
/// <para>
/// The seam exists because the position component lives one layer up: <c>KhaozEngine.Sharding</c> owns the
/// topology and knows nothing about <c>ReplicatedPosition</c>, which is the layer that references it. So the cell
/// calls out through this interface at each door an entity can arrive by (a migrate adoption, a ghost mirror, a
/// persistence restore) and the layer that owns the component supplies the conversion. Null on a cell means no
/// conversion, which is correct for a plain unframed cell.
/// </para>
/// </summary>
public interface ICellFrameAdapter
{
    /// <summary>
    /// Converts <paramref name="entity"/>'s framed components into <paramref name="frame"/>, in
    /// <paramref name="world"/>. Must be idempotent on an already-converted entity and must preserve the absolute
    /// world position exactly, since it runs on every ghost sync pass.
    /// </summary>
    void ToFrame(World world, Entity entity, WorldFrame frame);
}
