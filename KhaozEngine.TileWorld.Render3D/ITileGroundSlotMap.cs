using KhaozEngine.Render3D;

namespace KhaozEngine.TileWorld;

/// <summary>Turns a ground material id into the layer slot that material occupies in the tile-ground material
/// set the mesh will be drawn with, so the mesher can name slots in its vertices without knowing anything about
/// how the set was built.</summary>
public interface ITileGroundSlotMap
{
    /// <summary>The slot holding this material, or <see cref="MissingSlot"/> when the set does not carry it.</summary>
    int SlotOf(ushort materialId);

    /// <summary>The slot every set reserves for a material it does not carry, so a dangling id renders as the
    /// magenta that slot is filled with rather than as a neighbouring material or as nothing at all.</summary>
    int MissingSlot { get; }
}

/// <summary>The slot map the mesher uses until a real material set is handed to it: every id below
/// <see cref="MissingSlot"/> is its own slot, everything from there up is the reserved one. A stand-in rather
/// than a lookup, because it never reads the catalogs: an id inside the range that no catalog defines still maps
/// to itself, and only a real set can tell those two apart.</summary>
public sealed class IdentitySlotMap : ITileGroundSlotMap
{
    /// <summary>The shared instance, which is what <see cref="TileGroundMesherOptions.Slots"/> starts as.</summary>
    public static IdentitySlotMap Instance { get; } = new();

    /// <summary>The last slot of a full set, which is the one reserved for a material the set does not carry.</summary>
    public int MissingSlot => TileGroundMaterialConfig.MaxMaterials - 1;

    /// <summary>This id as its own slot, or <see cref="MissingSlot"/> for an id at or past the reserved one.</summary>
    public int SlotOf(ushort materialId) => materialId < MissingSlot ? materialId : MissingSlot;
}
