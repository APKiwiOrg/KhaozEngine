using System;

namespace KhaozEngine.MapDoc.Surfaces;

public readonly record struct MapPatchKey(string SurfaceId, long SlotX, long SlotZ) : IComparable<MapPatchKey>
{
    public const int SlotCells = 64;
    public static MapPatchKey ForCell(string surfaceId, long cellX, long cellZ)
    {
        if (string.IsNullOrWhiteSpace(surfaceId)) throw new MapDocumentException("surface id is required");
        return new(surfaceId, FloorSlot(cellX), FloorSlot(cellZ));
    }
    static long FloorSlot(long cell) => cell / SlotCells - (cell % SlotCells < 0 ? 1 : 0);
    public int CompareTo(MapPatchKey other)
    {
        int surface = StringComparer.Ordinal.Compare(SurfaceId, other.SurfaceId);
        return surface != 0 ? surface : SlotZ != other.SlotZ ? SlotZ.CompareTo(other.SlotZ) : SlotX.CompareTo(other.SlotX);
    }
}
