namespace KhaozEngine.TileWorld;

public sealed partial class TileWorldDocument
{
    TilePlaneData? PlaneAt(int x, int z, int plane)
    {
        RequirePlane(plane);
        return RegionAt(x, z)?.Plane(plane);
    }

    TilePlaneData WritablePlaneAt(int x, int z, int plane)
    {
        RequirePlane(plane);
        TileRegion r = RequireRegion(x, z);
        r.Dirty = true;
        return r.Plane(plane);
    }

    static int LocalIndex(int x, int z) =>
        TilePlaneData.Index(RegionCoord.FloorMod(x, TileRegion.Size), RegionCoord.FloorMod(z, TileRegion.Size));

    public ushort GetUnderlay(int x, int z, int plane) => PlaneAt(x, z, plane)?.Underlay?[LocalIndex(x, z)] ?? 0;
    public void SetUnderlay(int x, int z, int plane, ushort id) => WritablePlaneAt(x, z, plane).UnderlayOrAlloc()[LocalIndex(x, z)] = id;

    public ushort GetOverlay(int x, int z, int plane) => PlaneAt(x, z, plane)?.Overlay?[LocalIndex(x, z)] ?? 0;
    public void SetOverlay(int x, int z, int plane, ushort id) => WritablePlaneAt(x, z, plane).OverlayOrAlloc()[LocalIndex(x, z)] = id;

    public TileOverlayShape GetOverlayShape(int x, int z, int plane) =>
        (TileOverlayShape)(PlaneAt(x, z, plane)?.OverlayShape?[LocalIndex(x, z)] ?? 0);
    public void SetOverlayShape(int x, int z, int plane, TileOverlayShape shape) =>
        WritablePlaneAt(x, z, plane).OverlayShapeOrAlloc()[LocalIndex(x, z)] = (byte)shape;

    public int GetOverlayRotation(int x, int z, int plane) => PlaneAt(x, z, plane)?.OverlayRotation?[LocalIndex(x, z)] ?? 0;
    public void SetOverlayRotation(int x, int z, int plane, int rotation) =>
        WritablePlaneAt(x, z, plane).OverlayRotationOrAlloc()[LocalIndex(x, z)] = (byte)(rotation & 3);

    public TileSettings GetSettings(int x, int z, int plane) => (TileSettings)(PlaneAt(x, z, plane)?.Settings?[LocalIndex(x, z)] ?? 0);
    public void SetSettings(int x, int z, int plane, TileSettings settings) =>
        WritablePlaneAt(x, z, plane).SettingsOrAlloc()[LocalIndex(x, z)] = (byte)settings;
}
