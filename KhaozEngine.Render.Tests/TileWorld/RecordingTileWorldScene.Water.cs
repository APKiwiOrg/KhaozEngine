using System.Collections.Generic;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;

namespace KhaozEngine.Tests.TileWorld;

public sealed partial class RecordingTileWorldScene
{
    /// <summary>Every water plane queued since the last <see cref="ClearWater"/>, in submission order.</summary>
    public List<WaterPlane> WaterDraws { get; } = new();

    /// <summary>Records one water plane. Kept in its own partial from the ground and prop recording, because the
    /// water seam is a default interface member and a fake that did not override it would silently record
    /// nothing instead of failing.</summary>
    public void DrawWater(in WaterPlane plane) => WaterDraws.Add(plane);

    /// <summary>Forgets the recorded water planes, so the next frame's records stand alone.</summary>
    public void ClearWater() => WaterDraws.Clear();
}
