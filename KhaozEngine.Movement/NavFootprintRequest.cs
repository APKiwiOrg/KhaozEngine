using System.Numerics;

namespace KhaozEngine.Movement;

/// <summary>Visits the same footprint candidates as the synchronous scan, one row bound or one column per operation.</summary>
internal struct NavFootprintRequest(NavAreaFootprint footprint, Vector3 from, Vector3 to)
{
    NavFootprintRows _rows;
    int _x0, _x1, _z1, _x, _z, _last;
    double _lowY, _highY, _bottom;

    internal NavSegmentOperation NextOperation { get; private set; }

    internal bool Accepted { get; private set; }

    /// <summary>Checks capture bounds and initializes the row cursor without visiting a row.</summary>
    internal void Start()
    {
        if (!footprint.TryBounds(from, to, out _x0, out _x1, out _z, out _z1))
        {
            Finish(false);
            return;
        }

        NavAreaFootprint.HeightRange(from, to, out _lowY, out _highY);
        _rows = new NavFootprintRows(from, to, footprint.Options, footprint.Radius, _x0, _x1, _z, _z1);
        if (_z > _z1) Finish(true);
        else NextOperation = NavSegmentOperation.FootprintRow;
    }

    /// <summary>Runs one footprint row or column test after the caller has admitted its charge.</summary>
    internal void Step()
    {
        if (NextOperation == NavSegmentOperation.FootprintRow)
        {
            _bottom = footprint.RowBottom(_z);
            (_x, _last) = _rows.At(_bottom, _x0, _x1);
            if (_x > _last) FinishRow();
            else NextOperation = NavSegmentOperation.FootprintColumn;
            return;
        }

        bool refused = footprint.RefusesCell(from, to, _x, _z, _bottom, _lowY, _highY, onlyExclusions: false);
        NavFootprintDiagnostics.Current?.CountRow(_x, _x);
        if (refused) Finish(false);
        else if (_x == _last) FinishRow();
        else _x++;
    }

    void FinishRow()
    {
        if (_z == _z1) Finish(true);
        else
        {
            _z++;
            NextOperation = NavSegmentOperation.FootprintRow;
        }
    }

    void Finish(bool accepted)
    {
        Accepted = accepted;
        NextOperation = NavSegmentOperation.Done;
    }
}
