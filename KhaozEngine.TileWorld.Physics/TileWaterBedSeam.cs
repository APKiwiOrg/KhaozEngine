using System;
using I = KhaozEngine.TileWorld.Physics.TileWaterArithmetic;
using P = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Plane;
using V = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Vector;

namespace KhaozEngine.TileWorld.Physics;

/// <summary>Proves that a diagonal root only partitions one continuously intersected bed boundary.</summary>
internal sealed class TileWaterBedSeam
{
    readonly P _bed;
    readonly I _minX, _maxX, _minZ, _maxZ;
    readonly double _heightError, _pointError;
    readonly float _surface, _tileSize;
    readonly int _diagonal, _tileX, _tileZ;
    readonly V _reference;

    TileWaterBedSeam(TileWaterPrism piece, TileWaterPrism other, int diagonal)
    {
        _bed = piece.Planes[3];
        I inset = I.Point(Math.Max(piece.HorizontalError, other.HorizontalError));
        I roundX = I.Point(DrawnGroundRounding.HalfSpacing(Math.Max(Math.Abs((double)piece.Column.Minimum.X), Math.Abs((double)piece.Column.Maximum.X))));
        I roundZ = I.Point(DrawnGroundRounding.HalfSpacing(Math.Max(Math.Abs((double)piece.Column.Minimum.Z), Math.Abs((double)piece.Column.Maximum.Z))));
        _minX = I.Point(piece.Column.Minimum.X) - piece.Reference.X + inset + roundX;
        _maxX = I.Point(piece.Column.Maximum.X) - piece.Reference.X - inset - roundX;
        _minZ = I.Point(piece.Column.Minimum.Z) - piece.Reference.Z + inset + roundZ;
        _maxZ = I.Point(piece.Column.Maximum.Z) - piece.Reference.Z - inset - roundZ;
        I height = I.Point(Math.Max(piece.HeightError, other.HeightError)) +
            I.Point(Math.Abs(_bed.Normal.X.Low)) * roundX + I.Point(Math.Abs(_bed.Normal.Z.Low)) * roundZ;
        _heightError = height.High;
        _pointError = (roundX.Square() + height.Square() + roundZ.Square()).Sqrt().High;
        _surface = piece.Column.Maximum.Y;
        _diagonal = diagonal;
        _reference = piece.Reference;
        _tileX = piece.TileX;
        _tileZ = piece.TileZ;
        _tileSize = piece.TileSize;
    }

    internal static TileWaterBedSeam? Create(TileWaterPrism piece, TileWaterPrism other)
    {
        // Only the canonical pair covers the whole rectangle. Exact plane and owner equality
        // exclude real creases, shoreline transitions and boundaries between raw columns.
        var a = piece.Triangle;
        var b = other.Triangle;
        bool first = a.PlanA == TileLatticePoint.Sw && a.PlanB == TileLatticePoint.Se && a.PlanC == TileLatticePoint.Ne &&
            b.PlanA == TileLatticePoint.Sw && b.PlanB == TileLatticePoint.Ne && b.PlanC == TileLatticePoint.Nw;
        bool second = b.PlanA == TileLatticePoint.Sw && b.PlanB == TileLatticePoint.Se && b.PlanC == TileLatticePoint.Ne &&
            a.PlanA == TileLatticePoint.Sw && a.PlanB == TileLatticePoint.Ne && a.PlanC == TileLatticePoint.Nw;
        if (!(first || second) || piece.Column != other.Column || piece.ColumnIndex != other.ColumnIndex ||
            piece.TileX != other.TileX || piece.TileZ != other.TileZ || piece.TileSize != other.TileSize ||
            piece.Reference != other.Reference || piece.Planes[3] != other.Planes[3] ||
            piece.Planes[5] != other.Planes[5] || piece.Normal(3) != other.Normal(3)) return null;
        return new(piece, other, first ? 2 : 0);
    }

    internal bool Covers(int a, int b, int c, V start, V drive, I core, float radius, double enter, double exit)
    {
        if (c >= 0 || !(a == _diagonal && b is 3 or 6)) return false;
        if (TileWaterQueryWork.Enabled) TileWaterQueryWork.BedSeamProofs++;
        V centre = start + drive * new I(enter, exit) - new V(I.Point(0), core, I.Point(0));
        I distance = V.Dot(_bed.Normal, centre) - _bed.Offset;
        V projected = centre - _bed.Normal * (distance / _bed.Normal.SquaredLength);
        if (!(projected.X.Low > _minX.High && projected.X.High < _maxX.Low &&
            projected.Z.Low > _minZ.High && projected.Z.High < _maxZ.Low &&
            (projected.Y + I.Point(_heightError)).High < _surface && SelectsOwnTile(projected))) return false;
        I separation = (centre - projected).SquaredLength.Sqrt() + I.Point(_pointError);
        if (!(separation.High < radius)) return false;
        // Rounding the projected column to binary32 stays inside this same wet patch.
        // Its full XYZ error is paid before proving it inside a sphere on the actual core.
        if (TileWaterQueryWork.Enabled) TileWaterQueryWork.BedSeamCertificates++;
        return true;
    }

    bool SelectsOwnTile(V projected)
    {
        // Rounding and division are monotone. Checking the represented endpoint enclosure
        // excludes a projection whose ground lookup would select a different captured cell.
        V absolute = projected + _reference;
        return Math.Floor(TileWorldSpace.TileX((float)absolute.X.Low, _tileSize)) == _tileX &&
            Math.Floor(TileWorldSpace.TileX((float)absolute.X.High, _tileSize)) == _tileX &&
            Math.Floor(TileWorldSpace.TileZ((float)absolute.Z.Low, _tileSize)) == _tileZ &&
            Math.Floor(TileWorldSpace.TileZ((float)absolute.Z.High, _tileSize)) == _tileZ;
    }
}
