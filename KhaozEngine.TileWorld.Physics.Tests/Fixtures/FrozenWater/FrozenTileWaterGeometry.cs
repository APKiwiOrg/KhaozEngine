using System.Linq;
using KhaozEngine.TileWorld.Physics;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

// Test-owned capture adapter. Rebuild authored geometry without live plane or basis construction.
internal sealed class FrozenTileWaterGeometry
{
    internal FrozenTileWaterPrism[] Prisms { get; }
    internal FrozenTileWaterPrismFace[] Faces { get; }

    internal FrozenTileWaterGeometry(TileWaterTraceTestSnapshot source, bool rebuild = false)
    {
        Prisms = source.Prisms.Select(piece => new FrozenTileWaterPrism(piece, rebuild)).ToArray();
        Faces = rebuild
            ? FrozenTileWaterPrismFaces.Build(Prisms).OrderBy(face => Prisms[face.Prism].ColumnIndex)
                .ThenBy(face => face.Prism).ToArray()
            : source.Faces.Select(face => new FrozenTileWaterPrismFace(face.Prism, face.Plane, face.Wet, face.Dry,
                new(face.Shape, Seam(face)))).ToArray();
    }

    FrozenTileWaterBedSeam? Seam(TileWaterPrismFace face)
    {
        if (face.Shape.CaptureGeometryForTests().BedSeam is null) return null;
        int i = face.Prism;
        int other = i > 0 && Prisms[i - 1].ColumnIndex == Prisms[i].ColumnIndex ? i - 1 : i + 1;
        return other < Prisms.Length ? FrozenTileWaterBedSeam.Create(Prisms[i], Prisms[other]) : null;
    }
}
