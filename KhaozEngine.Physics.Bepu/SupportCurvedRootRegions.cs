using System;
using Number = KhaozEngine.Physics.Bepu.SupportCurvedSourceArithmetic.Number;

namespace KhaozEngine.Physics.Bepu;

internal interface ISupportCurvedRootFunction
{
    GeometryInterval Evaluate(GeometryInterval parameter, bool derivative);
    GeometrySign Sign(double parameter);
    GeometrySign DerivativeSign(GeometryInterval parameter);
}

/// <summary>Source-uniform simple-root regions for a degree-four boundary polynomial.
/// A region proves existence by endpoint signs, uniqueness by its derivative, and coverage by
/// exhaustive interval subdivision. An uncertified coefficient enclosure refuses at the live storage bound.</summary>
internal sealed class SupportCurvedRootRegions
{
    readonly SupportCurvedWork _work;
    internal SupportCurvedRootRegions(SupportCurvedWork work) => _work = work;

    internal const int MaximumLiveCells = 1024;
    internal const int MaximumTerminalCells = 12;
    readonly record struct Cell(double Low, double High);
    Cell[] _live = new Cell[MaximumLiveCells], _next = new Cell[MaximumLiveCells];
    readonly GeometryInterval[] _roots = new GeometryInterval[MaximumTerminalCells];
    readonly GeometryInterval[] _coefficients = new GeometryInterval[5];
    readonly Number[] _sourceCoefficients = new Number[5];
    int _degree;
    int _terminalLimit;
    SupportCurvedSourceArithmetic _arithmetic = null!;
    ISupportCurvedRootFunction? _function;
    internal int Count { get; private set; }
    internal int LiveHighWater { get; private set; }
    internal int TerminalCells => Count;
    internal int EvaluatedCells { get; private set; }
    internal bool CapacityRefused { get; private set; }
    internal GeometryInterval Get(int index) => _roots[index];

    internal void ResetStatistics()
    {
        Count = LiveHighWater = EvaluatedCells = 0;
        CapacityRefused = false;
    }

    internal bool Solve(SupportCurvedSourceArithmetic arithmetic, ReadOnlySpan<Number> coefficients,
        double low, double high)
    {
        Count = 0; LiveHighWater = 1; EvaluatedCells = 0; CapacityRefused = false;
        _arithmetic = arithmetic;
        _function = null;
        if (_work.Failed) return Refuse();
        _work.ObserveStorage(1, 0);
        if (coefficients.Length is < 2 or > 5 || low > high) return false;
        _degree = coefficients.Length - 1;
        _terminalLimit = _degree;
        for (int i = 0; i <= _degree; i++)
        {
            _sourceCoefficients[i] = coefficients[i];
            _coefficients[i] = arithmetic.Bounds(coefficients[i]);
            if (!_coefficients[i].IsResolved) return false;
        }
        while (_degree > 0 && arithmetic.Sign(_sourceCoefficients[_degree]) == GeometrySign.Zero) _degree--;
        if (_work.Failed) return Refuse();
        if (_degree == 0)
        {
            GeometrySign constant = arithmetic.Sign(_sourceCoefficients[0]);
            // A zero polynomial has a continuum of roots, not an empty root list.
            // Its optimizer needs the caller's separate coincident-stratum certificate.
            return constant is GeometrySign.Positive or GeometrySign.Negative;
        }
        int zeros = 0;
        while (zeros < _degree && arithmetic.Sign(_sourceCoefficients[zeros]) == GeometrySign.Zero) zeros++;
        if (_work.Failed) return Refuse();
        if (zeros > 0)
        {
            if (low <= 0 && high >= 0) _roots[Count++] = GeometryInterval.Exact(0);
            for (int i = zeros; i <= _degree; i++)
            { _sourceCoefficients[i - zeros] = _sourceCoefficients[i]; _coefficients[i - zeros] = _coefficients[i]; }
            _degree -= zeros;
            if (_degree == 0) return !_work.Failed;
        }
        return Traverse(low, high);
    }

    internal bool Solve(ISupportCurvedRootFunction function, double low, double high, int maximumRoots = MaximumTerminalCells)
    {
        _function = function;
        // Three axial-clamp branches, each with at most four unsquared-valid roots.
        _terminalLimit = maximumRoots;
        Count = 0; LiveHighWater = 1; EvaluatedCells = 0; CapacityRefused = false;
        return Traverse(low, high);
    }

    bool Traverse(double low, double high)
    {
        if (_work.Failed) return Refuse();
        _work.ObserveStorage(1, 0);
        _live[0] = new(low, high);
        int live = 1;
        while (live > 0)
        {
            int next = 0;
            for (int i = 0; i < live; i++)
            {
                if (!_work.TryCharge(SupportCurvedWorkKind.RootCell)) return Refuse();
                Cell cell = _live[i];
                EvaluatedCells++;
                GeometryInterval range = GeometryInterval.Enclose(cell.Low, cell.High);
                GeometryInterval value = Evaluate(range, false);
                if (_work.Failed) return Refuse();
                if (StrictSign(value) != 0) continue;
                int a = EndpointSign(cell.Low), b = EndpointSign(cell.High);
                GeometryInterval derivative = Evaluate(range, true);
                int slope = StrictSign(derivative);
                if (slope == 0 && _function is { } function)
                    slope = function.DerivativeSign(range) switch { GeometrySign.Positive => 1, GeometrySign.Negative => -1, _ => 0 };
                if (_work.Failed) return Refuse();
                if (slope != 0)
                {
                    if (a == 0 || b == 0 || (a != 2 && b != 2 && a != b))
                    {
                        GeometryInterval root = a == 0 ? GeometryInterval.Exact(cell.Low) :
                            b == 0 ? GeometryInterval.Exact(cell.High) : Refine(range, slope);
                        if (_work.Failed) return Refuse();
                        if (!Keep(root)) return Refuse();
                        continue;
                    }
                    if (a != 2 && b != 2 && a == b) continue;
                }
                double middle = cell.Low + (cell.High - cell.Low) * 0.5;
                // Do not cut through a source-dependent root family when a certified separator
                // is available. The root's existence bracket then survives in one child.
                if (EndpointSign(middle) == 2)
                {
                    double left = cell.Low + (cell.High - cell.Low) * 0.25;
                    double right = cell.Low + (cell.High - cell.Low) * 0.75;
                    if (left > cell.Low && left < cell.High && EndpointSign(left) != 2) middle = left;
                    else if (right > cell.Low && right < cell.High && EndpointSign(right) != 2) middle = right;
                }
                if (_work.Failed) return Refuse();
                if (!(middle > cell.Low && middle < cell.High)) return Refuse();
                if (next + 2 > MaximumLiveCells)
                {
                    LiveHighWater = Math.Max(LiveHighWater, next);
                    _work.ObserveStorage(LiveHighWater, 0);
                    return Refuse(SupportCurvedWorkFailure.RootStorage);
                }
                _next[next++] = new(cell.Low, middle);
                _next[next++] = new(middle, cell.High);
                LiveHighWater = Math.Max(LiveHighWater, next);
                _work.ObserveStorage(LiveHighWater, 0);
            }
            LiveHighWater = Math.Max(LiveHighWater, next);
            _work.ObserveStorage(LiveHighWater, 0);
            (_live, _next) = (_next, _live);
            live = next;
        }
        for (int i = 1; i < Count; i++)
        {
            GeometryInterval root = _roots[i];
            int j = i;
            while (j > 0 && _roots[j - 1].Lower > root.Lower)
            { _roots[j] = _roots[j - 1]; j--; }
            _roots[j] = root;
        }
        return true;
    }

    bool Refuse(SupportCurvedWorkFailure reason = SupportCurvedWorkFailure.None)
    {
        _work.Fail(reason);
        CapacityRefused = true;
        Count = 0;
        return false;
    }

    bool Keep(GeometryInterval root)
    {
        for (int i = 0; i < Count; i++)
            if (root.Lower <= _roots[i].Upper && root.Upper >= _roots[i].Lower)
            {
                // Adjacent source-uniform regions can duplicate an exact splitting-point root.
                if (root.Lower == root.Upper && _roots[i].Lower == root.Lower && _roots[i].Upper == root.Upper)
                    return true;
                return false;
            }
        if (Count == _terminalLimit) { _work.Fail(SupportCurvedWorkFailure.TerminalRoots); return false; }
        _roots[Count++] = root;
        return true;
    }

    GeometryInterval Refine(GeometryInterval region, int slope)
    {
        // Existence is already proved. Interval Newton preserves every source's root, even when
        // contraction ends at the nonzero source width rather than an adjacent binary64 pair.
        while (true)
        {
            if (!_work.TryCharge(SupportCurvedWorkKind.Refine)) return default;
            double middle = region.Lower + (region.Upper - region.Lower) * 0.5;
            if (!(middle > region.Lower && middle < region.Upper)) return region;
            GeometryInterval newton = GeometryInterval.Exact(middle).Subtract(
                Evaluate(GeometryInterval.Exact(middle), false).Divide(Evaluate(region, true)));
            if (_work.Failed) return default;
            if (!newton.IsResolved)
            {
                int sign = EndpointSign(middle);
                if (sign == 0) return GeometryInterval.Exact(middle);
                if (sign == 2) return region;
                region = sign == slope ? GeometryInterval.Enclose(region.Lower, middle) :
                    GeometryInterval.Enclose(middle, region.Upper);
                continue;
            }
            double low = Math.Max(region.Lower, newton.Lower), high = Math.Min(region.Upper, newton.Upper);
            if (low > high || (low == region.Lower && high == region.Upper)) return region;
            region = GeometryInterval.Enclose(low, high);
        }
    }

    GeometryInterval Evaluate(GeometryInterval x, bool derivative)
    {
        if (_work.Failed) return default;
        if (_function is { } function) return function.Evaluate(x, derivative);
        GeometryInterval value = derivative ? _coefficients[_degree].Multiply(GeometryInterval.Exact(_degree)) :
            _coefficients[_degree];
        for (int i = _degree - 1; i >= (derivative ? 1 : 0); i--)
            value = value.Multiply(x).Add(derivative ? _coefficients[i].Multiply(GeometryInterval.Exact(i)) :
                _coefficients[i]);
        return value;
    }

    int EndpointSign(double x)
    {
        if (_work.Failed) return 2;
        int sign = StrictSign(Evaluate(GeometryInterval.Exact(x), false));
        if (sign != 0) return sign;
        if (_function is { } function)
            return function.Sign(x) switch { GeometrySign.Negative => -1, GeometrySign.Zero => 0, GeometrySign.Positive => 1, _ => 2 };
        int checkpoint = _arithmetic.Checkpoint;
        Number value = _sourceCoefficients[_degree], argument = _arithmetic.Constant(x);
        for (int i = _degree - 1; i >= 0; i--)
            value = _arithmetic.Add(_arithmetic.Multiply(value, argument), _sourceCoefficients[i]);
        GeometrySign exact = _arithmetic.Sign(value);
        _arithmetic.Restore(checkpoint);
        return exact switch { GeometrySign.Negative => -1, GeometrySign.Zero => 0, GeometrySign.Positive => 1, _ => 2 };
    }

    static int StrictSign(GeometryInterval value) => !value.IsResolved ? 0 : value.Lower > 0 ? 1 :
        value.Upper < 0 ? -1 : 0;
}
