using System;
using System.Collections.Generic;

namespace KhaozEngine.TileWorld;

/// <summary>The anchor and plane query of <see cref="TileWorldDocument.ObjectsIn"/> without iterator objects.
/// Intended for per-frame consumers. Region slots run Z then X and each resident list keeps its own order.</summary>
internal readonly struct TileObjectCandidates
{
    readonly TileWorldDocument _document;
    readonly TileRect _rect;
    readonly int? _plane;

    public TileObjectCandidates(TileWorldDocument document, TileRect rect, int? plane = null)
    {
        _document = document;
        _rect = rect;
        _plane = plane;
    }

    // The foreach pattern binds directly to this struct, avoiding IEnumerable and enumerator boxing.
    public Enumerator GetEnumerator() => new(_document, _rect, _plane);

    public struct Enumerator : IDisposable
    {
        readonly TileWorldDocument _document;
        readonly TileRect _rect;
        readonly int? _plane;
        readonly int _minRx, _maxRx, _maxRz;
        int _rx, _rz;
        bool _regionsRemain, _hasObjects;
        List<TileObject>.Enumerator _objects;

        internal Enumerator(TileWorldDocument document, TileRect rect, int? plane)
        {
            _document = document;
            _rect = rect;
            _plane = plane;
            _objects = default;
            _hasObjects = false;
            _regionsRemain = !rect.IsEmpty;
            RegionCoord lo = _regionsRemain ? RegionCoord.Of(rect.X, rect.Z) : default;
            RegionCoord hi = _regionsRemain ? RegionCoord.Of(rect.X1 - 1, rect.Z1 - 1) : default;
            _minRx = _rx = lo.Rx;
            _rz = lo.Rz;
            _maxRx = hi.Rx;
            _maxRz = hi.Rz;
        }

        public readonly TileObject Current => _objects.Current;

        public bool MoveNext()
        {
            while (true)
            {
                if (_hasObjects)
                {
                    while (_objects.MoveNext())
                    {
                        TileObject candidate = _objects.Current;
                        if (_rect.Contains(candidate.X, candidate.Z)
                            && (_plane is null || candidate.Plane == _plane.Value))
                            return true;
                    }
                    _objects.Dispose();
                    _hasObjects = false;
                }
                if (!_regionsRemain) return false;

                TileRegion? region = _document.GetRegion(new RegionCoord(_rx, _rz));
                if (_rx < _maxRx) _rx++;
                else
                {
                    _rx = _minRx;
                    if (_rz < _maxRz) _rz++;
                    else _regionsRemain = false;
                }
                if (region is null) continue;
                _objects = region.Objects.GetEnumerator();
                _hasObjects = true;
            }
        }

        public void Dispose()
        {
            if (_hasObjects) _objects.Dispose();
            _hasObjects = false;
            _regionsRemain = false;
        }
    }
}
