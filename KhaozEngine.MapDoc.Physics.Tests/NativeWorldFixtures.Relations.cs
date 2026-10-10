using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.MapDoc.Physics;

/// <summary>Named frame points for the physical relation tests.</summary>
internal static partial class NativeWorldFixtures
{
    /// <summary>The point <paramref name="relative"/> to the whole-metre move (<paramref name="moveX"/>, 0,
    /// <paramref name="moveZ"/>), in the frame nearest the move, so the local part stays small.</summary>
    internal static MapFramePoint FramePoint(int moveX, int moveZ, Vector3 relative)
    {
        WorldFrame frame = WorldFrame.Nearest(moveX, moveZ);
        return new(frame, new Vector3(moveX - frame.X * WorldFrame.Grid + relative.X, relative.Y,
            moveZ - frame.Z * WorldFrame.Grid + relative.Z));
    }
}

/// <summary><see cref="NativeWorldFixtures.StackedCaveAt"/>, with world points on the lower floor, under the low
/// ceiling, inside the shaft between the slabs and on the upper floor, and frame points relative to the
/// move.</summary>
internal sealed record StackedCaveFixture(MapDocument Document, MapAssetClosure Assets, MapResolvedDocument Resolved,
    MapScopedSurfaces View, int CompiledFaceCount, Vector3 LowerFloorPoint, Vector3 LowCeilingPoint, Vector3 ShaftPoint,
    Vector3 UpperFloorPoint, int MoveX, int MoveZ) : TerrainFixture(Document, Assets, Resolved, View, CompiledFaceCount)
{
    /// <summary>Mid-height in the lower room, under the 3 m ceiling.</summary>
    internal MapFramePoint LowerRoom => At(1.65f, 1.5f, 4.35f);

    /// <summary>Straight above <see cref="LowerRoom"/>, between the upper floor and the upper ceiling.</summary>
    internal MapFramePoint UpperRoom => At(1.65f, 5f, 4.35f);

    /// <summary>In the lower room under the shaft, 0.65 m from its west wall.</summary>
    internal MapFramePoint LowerUnderShaft => At(3.65f, 1.5f, 4.35f);

    /// <summary>Straight above <see cref="LowerUnderShaft"/>, in the upper room over the shaft.</summary>
    internal MapFramePoint UpperOverShaft => At(3.65f, 5f, 4.35f);

    /// <summary>Feet on the lower floor under the 3 m ceiling, with the shell's reach clear of the low corner.</summary>
    internal MapFramePoint LowerRoomFeet => At(1.65f, 0f, 4.35f);

    /// <summary>Feet on the lower floor under the 1.2 m ceiling, which the shell's top passes through.</summary>
    internal MapFramePoint UnderLowCeilingFeet => At(1f, 0f, 1f);

    /// <summary>Above the crate, where a downward pick ray enters its envelope.</summary>
    internal MapFramePoint OverCrate => At(5.5f, 1.5f, 0.5f);

    /// <summary>Points on both levels, in the shaft, under the low ceiling and over the crate.</summary>
    internal IReadOnlyList<MapFramePoint> ProbePoints => new[]
    {
        LowerRoom, UpperRoom, LowerUnderShaft, UpperOverShaft, At(3.65f, 3.25f, 4.35f), At(1f, 0.6f, 1f), OverCrate,
    };

    /// <summary>A 4 m pick ray straight down from <paramref name="point"/>.</summary>
    internal MapPickRay PickRayFrom(MapFramePoint point) => new(point.Frame.ToWorld(point.Local), -Vector3.UnitY, 4f);

    /// <summary>A support request at <paramref name="point"/> reaching 0.5 m up and 2 m down.</summary>
    internal MapSupportRequest SupportRequestAt(MapFramePoint point) => new(point, null, null, null, 0.5f, 2f, null);

    /// <summary>This cave built with <see cref="NativeWorldFixtures.Options"/>.</summary>
    internal MapBuiltWorld Build() => MapWorldBuilder.Build(Document, Assets, NativeWorldFixtures.Options());

    MapFramePoint At(float x, float y, float z) => NativeWorldFixtures.FramePoint(MoveX, MoveZ, new Vector3(x, y, z));
}

/// <summary><see cref="NativeWorldFixtures.Doorway"/>, with frame points 1.5 local units either side of the doorway's
/// plane through a jamb's centre line and through the middle of the opening, at 1.2 local units up and on the
/// floor.</summary>
internal sealed record DoorwayFixture(MapDocument Document, MapAssetClosure Assets, MapResolvedDocument Resolved,
    MapScopedSurfaces View) : NativeFixture(Document, Assets, Resolved, View)
{
    internal MapFramePoint BeforeJamb => Local(0.75f, 1.2f, -1.5f);
    internal MapFramePoint AfterJamb => Local(0.75f, 1.2f, 1.5f);
    internal MapFramePoint BeforeOpening => Local(0f, 1.2f, -1.5f);
    internal MapFramePoint AfterOpening => Local(0f, 1.2f, 1.5f);
    internal MapFramePoint FeetBeforeJamb => Local(0.75f, 0f, -1.5f);
    internal MapFramePoint FeetAfterJamb => Local(0.75f, 0f, 1.5f);
    internal MapFramePoint FeetBeforeOpening => Local(0f, 0f, -1.5f);
    internal MapFramePoint FeetAfterOpening => Local(0f, 0f, 1.5f);

    /// <summary>8 m beyond the playable bounds' +x edge, at the doorway's height.</summary>
    internal MapFramePoint FarOutsideBounds => new(WorldFrame.Origin, new Vector3(40f, 1.2f, 0.17f));

    MapFramePoint Local(float x, float y, float z) => new(WorldFrame.Origin,
        Resolved.Placements.Single(p => p.PlacementId == "doorway").Transform.TransformPoint(new Vector3(x, y, z)));
}
